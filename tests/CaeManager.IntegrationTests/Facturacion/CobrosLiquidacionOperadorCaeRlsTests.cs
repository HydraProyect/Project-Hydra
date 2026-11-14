using CaeManager.Application.Common;
using CaeManager.Application.Dashboard.TarifasOperadorCae;
using CaeManager.Application.Facturacion.LiquidacionesOperadorCae;
using CaeManager.Domain.Common;
using CaeManager.Domain.Facturacion;
using CaeManager.Domain.Operaciones;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.IntegrationTests.Arranque;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace CaeManager.IntegrationTests.Facturacion;

/// <summary>
/// Marca manual de cobro de la liquidación mensual del Operador CAE externo a cada Tenant beneficiario (V-2, D-F5 y
/// D-F9) con el cableado de producción (<see cref="ArnesDeArranqueRuntime"/>: conexión como <c>cae_app_runtime</c> y sus
/// interceptores), para que la RLS <c>aislamiento_tenant</c> de la migración <c>AnadeCobrosLiquidacionOperadorCae</c> sea la
/// que se mide.
///
/// <para>
/// Escenario: el Operador A gestionó a la Empresa X todo el mes pasado (Asignación de Operación externa vigente desde
/// antes); el Operador B, a la Empresa Y. Las marcas son filas del Tenant de origen del Operador: ni el otro Operador ni
/// el Tenant beneficiario las ven; marcan el Administrador y la Dirección CAE; el Coordinador CAE solo lee; y no se marca
/// una Empresa que el Operador no gestionó, ni el propio Operador, ni un mes que no ha terminado.
/// </para>
/// </summary>
public class CobrosLiquidacionOperadorCaeRlsTests : IAsyncLifetime
{
    private const string PermisoDenegado = "42501";

    private readonly Guid _usuario = Guid.NewGuid();
    private readonly UsuarioConmutable _sesion = new();
    private ArnesDeArranqueRuntime _arnes = null!;

    private Guid _operadorA;
    private Guid _operadorB;
    private Guid _empresaX;
    private Guid _empresaY;
    private Guid _empresaW;
    private Guid _empresaZ;
    private int _anyo;
    private int _mes;
    private int _anyoAnterior;
    private int _mesAnterior;

    public async Task InitializeAsync()
    {
        _arnes = await ArnesDeArranqueRuntime.CrearAsync(datosDePruebaActivos: false, currentUserServicePersonalizado: _sesion);

        // La siembra va como propietario de las tablas: la Asignación de Operación la crea el alta, no el Operador.
        var tenantActual = new TenantActualPorAmbito();
        var opciones = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_arnes.CadenaPropietario, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual))
            .Options;
        await using var propietario = new CaeManagerDbContext(opciones, new EphemeralDataProtectionProvider(), tenantActual);

        var a = new Tenant("Operador A");
        var b = new Tenant("Operador B");
        var x = new Tenant("Empresa X");
        var y = new Tenant("Empresa Y");
        var w = new Tenant("Empresa W");
        var z = new Tenant("Empresa Z");
        propietario.Tenants.AddRange(a, b, x, y, w, z);
        await propietario.SaveChangesAsync();
        (_operadorA, _operadorB, _empresaX, _empresaY, _empresaW, _empresaZ) = (a.Id, b.Id, x.Id, y.Id, w.Id, z.Id);

        var ahora = DateTime.UtcNow;
        var mesPasado = ahora.AddMonths(-1);
        var mesAntepasado = ahora.AddMonths(-2);
        (_anyo, _mes) = (mesPasado.Year, mesPasado.Month);
        (_anyoAnterior, _mesAnterior) = (mesAntepasado.Year, mesAntepasado.Month);
        // Desde antes del mes antepasado: el Operador A tiene liquidación de X y de W en los dos meses terminados.
        var desde = new DateTime(_anyoAnterior, _mesAnterior, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(-3);
        using (AmbitoTenantExplicito.Establecer(_operadorA))
        {
            propietario.AsignacionesOperacion.Add(AsignacionOperacion.Externa(
                _empresaX, _operadorA, ServicioCae.Outbound, AmbitoAsignacion.Universal, desde, null, ahora));
            propietario.AsignacionesOperacion.Add(AsignacionOperacion.Externa(
                _empresaW, _operadorA, ServicioCae.Outbound, AmbitoAsignacion.Universal, desde, null, ahora));
            // Solo Inbound: el Operador A opera a Z, pero no la factura en «Facturación» (vía Outbound), así que no se marca.
            propietario.AsignacionesOperacion.Add(AsignacionOperacion.Externa(
                _empresaZ, _operadorA, ServicioCae.Inbound, AmbitoAsignacion.Universal, desde, null, ahora));
        }
        using (AmbitoTenantExplicito.Establecer(_operadorB))
            propietario.AsignacionesOperacion.Add(AsignacionOperacion.Externa(
                _empresaY, _operadorB, ServicioCae.Outbound, AmbitoAsignacion.Universal, desde, null, ahora));
        await propietario.SaveChangesAsync();
    }

    public async Task DisposeAsync() => await _arnes.DisposeAsync();

    [Theory]
    [InlineData("Administrador")]
    [InlineData("DireccionCae")]
    public async Task El_Administrador_y_la_Direccion_CAE_marcan_y_el_otro_Operador_y_la_Empresa_no_ven_la_marca(string rol)
    {
        Como(rol, _operadorA);
        (await MarcarAsync(_empresaX, EstadoCobroLiquidacion.Cobrada, "F-2026/118")).EsExitoso.Should().BeTrue();

        var propias = await LeerAsync();
        propias.PuedeMarcar.Should().BeTrue();
        propias.De(_empresaX).Should().NotBeNull("control positivo: quien marca ve su marca")
            .And.Match<CobroLiquidacionOperadorCaeDto>(c => c.Estado == EstadoCobroLiquidacion.Cobrada && c.ReferenciaFacturaEmisor == "F-2026/118");

        Como("Administrador", _operadorB);
        (await LeerAsync()).Cobros.Should().BeEmpty("las marcas del Operador A no son filas del Tenant del Operador B");

        // La base, sin ayuda del filtro global ni del handler: la política por TenantId decide.
        (await ContarComoRuntimeAsync(_operadorA)).Should().Be(1, "control positivo de la RLS");
        (await ContarComoRuntimeAsync(_operadorB)).Should().Be(0, "otro Operador CAE no lee las marcas del Operador A");
        (await ContarComoRuntimeAsync(_empresaX)).Should().Be(0, "el Tenant beneficiario tampoco: la marca es del Operador");
    }

    [Fact]
    public async Task El_Coordinador_CAE_lee_las_marcas_pero_no_marca()
    {
        Como("Administrador", _operadorA);
        (await MarcarAsync(_empresaX, EstadoCobroLiquidacion.Impagada, null)).EsExitoso.Should().BeTrue();

        Como("CoordinadorCae", _operadorA);
        var lectura = await LeerAsync();
        lectura.PuedeMarcar.Should().BeFalse("el Coordinador CAE solo lee (D-F9)");
        lectura.De(_empresaX)!.Estado.Should().Be(EstadoCobroLiquidacion.Impagada);

        (await MarcarAsync(_empresaX, EstadoCobroLiquidacion.Cobrada, null)).Error
            .Should().Be(AutorizacionCobrosLiquidacionOperadorCae.SinPermiso);
        Como("Administrador", _operadorA);
        (await LeerAsync()).De(_empresaX)!.Estado.Should().Be(EstadoCobroLiquidacion.Impagada, "la marca del Coordinador no entró");
    }

    [Theory]
    [InlineData("GestorCae")]
    [InlineData(null)]
    public async Task Ni_un_Gestor_CAE_ni_una_sesion_sin_rol_leen_ni_marcan(string? rol)
    {
        Como("Administrador", _operadorA);
        (await MarcarAsync(_empresaX, EstadoCobroLiquidacion.Cobrada, null)).EsExitoso.Should().BeTrue();

        Como(rol, _operadorA);
        (await MarcarAsync(_empresaX, EstadoCobroLiquidacion.Impagada, null)).Error
            .Should().Be(AutorizacionCobrosLiquidacionOperadorCae.SinPermiso);
        var lectura = await LeerAsync();
        lectura.PuedeMarcar.Should().BeFalse();
        lectura.Cobros.Should().BeEmpty("sin Administrador, Dirección CAE ni Coordinador CAE del Operador no hay marcas que ver");
    }

    [Fact]
    public async Task No_se_marca_una_Empresa_que_el_Operador_no_gestiono_ni_a_si_mismo_ni_un_mes_sin_terminar()
    {
        Como("Administrador", _operadorA);

        (await MarcarAsync(_empresaY, EstadoCobroLiquidacion.Cobrada, null)).Error
            .Should().Be(AutorizacionCobrosLiquidacionOperadorCae.EmpresaNoGestionada, "la Empresa Y la gestiona el Operador B");
        (await MarcarAsync(_operadorA, EstadoCobroLiquidacion.Cobrada, null)).Error
            .Should().Be(AutorizacionCobrosLiquidacionOperadorCae.EmpresaNoGestionada, "el Operador no se liquida a sí mismo");
        (await MarcarAsync(_empresaZ, EstadoCobroLiquidacion.Cobrada, null)).Error
            .Should().Be(AutorizacionCobrosLiquidacionOperadorCae.EmpresaNoGestionada,
                "una Asignación de Operación solo Inbound no da liquidación: la pantalla «Facturación» no lista a Z");

        var hoy = DateTime.UtcNow;
        (await MarcarAsync(_empresaX, EstadoCobroLiquidacion.Cobrada, null, hoy.Year, hoy.Month)).Error
            .Should().Be(AutorizacionCobrosLiquidacionOperadorCae.MesNoTerminado, "el mes en curso aún no tiene liquidación");
        (await ContarComoRuntimeAsync(_operadorA)).Should().Be(0);

        (await MarcarAsync(_empresaX, EstadoCobroLiquidacion.Cobrada, null)).EsExitoso
            .Should().BeTrue("control positivo: la Empresa X sí la gestionó el mes pasado");
    }

    [Fact]
    public async Task Volver_a_marcar_actualiza_la_misma_fila()
    {
        Como("Administrador", _operadorA);
        (await MarcarAsync(_empresaX, EstadoCobroLiquidacion.Impagada, "F-1")).EsExitoso.Should().BeTrue();
        (await MarcarAsync(_empresaX, EstadoCobroLiquidacion.Cobrada, "F-2")).EsExitoso.Should().BeTrue();

        (await ContarComoRuntimeAsync(_operadorA)).Should().Be(1, "una marca por Operador, Tenant beneficiario y mes");
        (await LeerAsync()).De(_empresaX).Should().Match<CobroLiquidacionOperadorCaeDto>(c =>
            c.Estado == EstadoCobroLiquidacion.Cobrada && c.ReferenciaFacturaEmisor == "F-2");
    }

    [Fact]
    public async Task Cada_marca_es_de_su_Tenant_beneficiario_y_de_su_mes()
    {
        Como("Administrador", _operadorA);
        (await MarcarAsync(_empresaX, EstadoCobroLiquidacion.Cobrada, "X-pasado")).EsExitoso.Should().BeTrue();
        (await MarcarAsync(_empresaW, EstadoCobroLiquidacion.Impagada, "W-pasado")).EsExitoso.Should().BeTrue();
        (await MarcarAsync(_empresaX, EstadoCobroLiquidacion.Impagada, "X-antepasado", _anyoAnterior, _mesAnterior)).EsExitoso.Should().BeTrue();

        (await ContarComoRuntimeAsync(_operadorA)).Should().Be(3, "una fila por Tenant beneficiario y mes: ninguna marca pisó a otra");

        var pasado = await LeerAsync();
        pasado.Cobros.Should().HaveCount(2, "la lectura de un mes no trae marcas de otro");
        pasado.De(_empresaX)!.ReferenciaFacturaEmisor.Should().Be("X-pasado");
        pasado.De(_empresaW)!.ReferenciaFacturaEmisor.Should().Be("W-pasado");

        var antepasado = await LeerAsync(_anyoAnterior, _mesAnterior);
        antepasado.Cobros.Should().ContainSingle().Which.ReferenciaFacturaEmisor.Should().Be("X-antepasado");
    }

    [Fact]
    public async Task El_Administrador_del_Tenant_beneficiario_no_lee_la_marca_de_su_liquidacion()
    {
        Como("Administrador", _operadorA);
        (await MarcarAsync(_empresaX, EstadoCobroLiquidacion.Impagada, null)).EsExitoso.Should().BeTrue();

        Como("Administrador", _empresaX);
        var lectura = await LeerAsync();
        lectura.Cobros.Should().BeEmpty("la marca es del Operador CAE emisor: la Empresa gestionada no la ve, ni dentro de la aplicación");
        (await MarcarAsync(_empresaX, EstadoCobroLiquidacion.Cobrada, null)).EsExitoso
            .Should().BeFalse("la Empresa no se marca a sí misma como cobrada");
        Como("Administrador", _operadorA);
        (await LeerAsync()).De(_empresaX)!.Estado.Should().Be(EstadoCobroLiquidacion.Impagada);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Dos_marcas_a_la_vez_gana_la_primera_entera_y_la_segunda_recibe_el_conflicto(bool yaHabiaMarca)
    {
        Como("Administrador", _operadorA);
        if (yaHabiaMarca)
            (await MarcarAsync(_empresaX, EstadoCobroLiquidacion.Pendiente, "F-0")).EsExitoso.Should().BeTrue();

        await using var scope = _arnes.Servicios.CreateAsyncScope();
        var contexto = scope.ServiceProvider.GetRequiredService<CaeManagerDbContext>();
        var unidad = new UnidadConOtraMarcaAntes(contexto, async () =>
            (await MarcarAsync(_empresaX, EstadoCobroLiquidacion.Cobrada, "F-1")).EsExitoso.Should().BeTrue("la primera marca entra"));

        (await MarcarConAsync(scope.ServiceProvider, unidad, _empresaX, EstadoCobroLiquidacion.Impagada, "F-2")).Error
            .Should().Be(AutorizacionCobrosLiquidacionOperadorCae.Conflicto,
                yaHabiaMarca ? "el token de concurrencia detecta la edición pisada" : "el índice único detecta la segunda alta");
        contexto.ChangeTracker.Entries().Should().BeEmpty("lo que el intento fallido dejó rastreado se suelta");

        (await ContarComoRuntimeAsync(_operadorA)).Should().Be(1);
        (await LeerAsync()).De(_empresaX).Should().Match<CobroLiquidacionOperadorCaeDto>(c =>
            c.Estado == EstadoCobroLiquidacion.Cobrada && c.ReferenciaFacturaEmisor == "F-1",
            "queda la primera marca entera, nunca una mezcla de las dos");
    }

    [Fact]
    public async Task La_guarda_mira_el_rol_en_el_Tenant_de_origen_del_Operador_y_no_en_el_del_ambito()
    {
        Como("GestorCae", _operadorA, rolFueraDelOrigen: "Administrador");
        using (AmbitoTenantExplicito.Establecer(_empresaX))
            (await MarcarAsync(_empresaX, EstadoCobroLiquidacion.Cobrada, null)).Error
                .Should().Be(AutorizacionCobrosLiquidacionOperadorCae.SinPermiso, "el rol que cuenta es el del Tenant de origen del Operador");

        Como("Administrador", _operadorA, rolFueraDelOrigen: "GestorCae");
        using (AmbitoTenantExplicito.Establecer(_empresaX))
            (await MarcarAsync(_empresaX, EstadoCobroLiquidacion.Cobrada, null)).EsExitoso
                .Should().BeTrue("control positivo: Administrador en el Tenant de origen del Operador");
        (await ContarComoRuntimeAsync(_operadorA)).Should().Be(1);
    }

    [Fact]
    public async Task La_base_no_admite_escribir_en_otro_Tenant_ni_mover_la_marca_ni_borrarla()
    {
        Como("Administrador", _operadorA);
        (await MarcarAsync(_empresaX, EstadoCobroLiquidacion.Cobrada, null)).EsExitoso.Should().BeTrue();

        await using (var ajena = await AbrirComoRuntimeAsync(_operadorB))
        {
            await using var colada = ajena.CreateCommand();
            colada.CommandText = """
                INSERT INTO "CobrosLiquidacionOperadorCae" ("Id", "PropietarioTenantId", "Anyo", "Mes", "Estado", "MarcadaPorUsuarioId", "MarcadaEnUtc", "Version", "TenantId")
                VALUES (@id, @empresa, 2026, 1, 2, @usuario, now(), @id, @operador);
                """;
            colada.Parameters.AddWithValue("id", Guid.NewGuid());
            colada.Parameters.AddWithValue("empresa", _empresaY);
            colada.Parameters.AddWithValue("usuario", _usuario);
            colada.Parameters.AddWithValue("operador", _operadorA);
            (await Record.ExceptionAsync(() => colada.ExecuteNonQueryAsync())).Should().BeOfType<PostgresException>()
                .Which.SqlState.Should().Be(PermisoDenegado, "WITH CHECK de aislamiento_tenant: el Operador B no escribe filas del Operador A");
        }

        await using var propia = await AbrirComoRuntimeAsync(_operadorA);
        (await EjecutarAsync(propia, """UPDATE "CobrosLiquidacionOperadorCae" SET "Estado" = 3;""")).Should().BeNull(
            "control positivo: cae_app_runtime cambia el estado de su marca");
        (await EjecutarAsync(propia, $"""UPDATE "CobrosLiquidacionOperadorCae" SET "PropietarioTenantId" = '{_empresaY}';"""))
            .Should().Be(PermisoDenegado, "la marca no cambia de Tenant beneficiario: solo hay UPDATE de estado, referencia y autoría");
        (await EjecutarAsync(propia, $"""UPDATE "CobrosLiquidacionOperadorCae" SET "TenantId" = '{_operadorB}';"""))
            .Should().Be(PermisoDenegado, "ni de Operador");
        (await EjecutarAsync(propia, """UPDATE "CobrosLiquidacionOperadorCae" SET "Mes" = 1;"""))
            .Should().Be(PermisoDenegado, "ni de mes");
        (await EjecutarAsync(propia, """DELETE FROM "CobrosLiquidacionOperadorCae";"""))
            .Should().Be(PermisoDenegado, "cae_app_runtime no borra marcas (la retirada de demostración corre como propietario)");
    }

    [Fact]
    public async Task Soporte_TALVEG_no_lee_las_marcas_ni_dentro_del_Tenant_del_Operador()
    {
        Como("Administrador", _operadorA);
        (await MarcarAsync(_empresaX, EstadoCobroLiquidacion.Cobrada, null)).EsExitoso.Should().BeTrue();

        await using var conexion = new NpgsqlConnection(_arnes.CadenaPropietario);
        await conexion.OpenAsync();
        await using (var rol = conexion.CreateCommand())
        {
            rol.CommandText = "SET ROLE cae_app_soporte;";
            await rol.ExecuteNonQueryAsync();
        }

        await FijarTenantAsync(conexion, _operadorA);
        (await EjecutarAsync(conexion, """SELECT count(*) FROM "CobrosLiquidacionOperadorCae";"""))
            .Should().Be(PermisoDenegado, "la migración no concede nada a cae_app_soporte: Soporte TALVEG no es Operador CAE");
    }

    /// <summary>Ejecuta una sentencia; devuelve el SqlState del error o null si entra.</summary>
    private static async Task<string?> EjecutarAsync(NpgsqlConnection conexion, string sql)
    {
        await using var comando = conexion.CreateCommand();
        comando.CommandText = sql;
        return await Record.ExceptionAsync(() => comando.ExecuteNonQueryAsync()) switch
        {
            null => null,
            PostgresException e => e.SqlState,
            var otra => throw otra,
        };
    }

    private void Como(string? rol, Guid tenantOrigen, string? rolFueraDelOrigen = null)
    {
        _sesion.UsuarioId = _usuario;
        _sesion.Rol = rol;
        _sesion.RolFueraDelOrigen = rolFueraDelOrigen;
        _sesion.TenantOrigenId = tenantOrigen;
    }

    private async Task<Result> MarcarAsync(
        Guid empresa, EstadoCobroLiquidacion estado, string? referencia, int? anyo = null, int? mes = null)
    {
        await using var scope = _arnes.Servicios.CreateAsyncScope();
        var contexto = scope.ServiceProvider.GetRequiredService<CaeManagerDbContext>();
        return await MarcarConAsync(scope.ServiceProvider, contexto, empresa, estado, referencia, anyo, mes);
    }

    /// <summary>El handler con las piezas del contenedor de producción (la fuente, con su mediador y la serie del Cuadro).</summary>
    private Task<Result> MarcarConAsync(
        IServiceProvider servicios, IUnitOfWork unidad, Guid empresa, EstadoCobroLiquidacion estado, string? referencia,
        int? anyo = null, int? mes = null)
    {
        var contexto = servicios.GetRequiredService<CaeManagerDbContext>();
        var handler = new MarcarCobroLiquidacionOperadorCaeCommandHandler(
            _sesion, contexto, servicios.GetRequiredService<IFuenteLiquidacionOperadorCae>(), unidad, TimeProvider.System, contexto);
        return handler.Handle(
            new MarcarCobroLiquidacionOperadorCaeCommand(empresa, anyo ?? _anyo, mes ?? _mes, estado, referencia), CancellationToken.None);
    }

    /// <summary>
    /// Mete otra marca completa entre la lectura y el guardado de este handler: la ventana exacta de dos personas que
    /// marcan el mismo cobro a la vez.
    /// </summary>
    private sealed class UnidadConOtraMarcaAntes(IUnitOfWork real, Func<Task> otraMarca) : IUnitOfWork
    {
        public async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            await otraMarca();
            return await real.SaveChangesAsync(cancellationToken);
        }
    }

    private async Task<CobrosLiquidacionOperadorCaeDto> LeerAsync(int? anyo = null, int? mes = null)
    {
        await using var scope = _arnes.Servicios.CreateAsyncScope();
        var contexto = scope.ServiceProvider.GetRequiredService<CaeManagerDbContext>();
        return await new ObtenerCobrosLiquidacionOperadorCaeQueryHandler(scope.ServiceProvider.GetRequiredService<IFuenteLiquidacionOperadorCae>(), contexto)
            .Handle(new ObtenerCobrosLiquidacionOperadorCaeQuery(anyo ?? _anyo, mes ?? _mes), CancellationToken.None);
    }

    private async Task<long> ContarComoRuntimeAsync(Guid tenant)
    {
        await using var conexion = await AbrirComoRuntimeAsync(tenant);
        await using var consulta = conexion.CreateCommand();
        consulta.CommandText = """SELECT count(*) FROM "CobrosLiquidacionOperadorCae";""";
        return (long)(await consulta.ExecuteScalarAsync())!;
    }

    private async Task<NpgsqlConnection> AbrirComoRuntimeAsync(Guid tenant)
    {
        var conexion = new NpgsqlConnection(BaseDatosPostgresDePruebas.CadenaComoRuntime(_arnes.CadenaPropietario));
        await conexion.OpenAsync();
        await FijarTenantAsync(conexion, tenant);
        return conexion;
    }

    private Task FijarTenantAsync(NpgsqlConnection conexion, Guid tenant) =>
        ContextoRlsDePruebas.FijarAsync(conexion, tenantId: tenant, usuarioId: _usuario);

    private sealed class TenantActualPorAmbito : ITenantActual
    {
        public Guid? TenantId => AmbitoTenantExplicito.TenantIdActual;
    }

    /// <summary>La persona de la sesión, cambiable entre pasos: el interceptor la lee en cada apertura de conexión.</summary>
    /// <remarks>
    /// Como el servicio real, el rol efectivo depende del Tenant del ámbito: <see cref="Rol"/> en el Tenant de origen y
    /// <see cref="RolFueraDelOrigen"/> en cualquier otro (o sin ámbito). Así la guarda solo pasa si fija el ámbito en
    /// el Tenant de origen del Operador antes de preguntar el rol.
    /// </remarks>
    private sealed class UsuarioConmutable : ICurrentUserService
    {
        public Guid? UsuarioId { get; set; }
        public string? Rol { get; set; }
        public string? RolFueraDelOrigen { get; set; }
        public Guid? TenantOrigenId { get; set; }

        public Task<Guid?> ObtenerUsuarioActualIdAsync() => Task.FromResult(UsuarioId);
        public Task<string?> ObtenerRolEfectivoAsync() =>
            Task.FromResult(AmbitoTenantExplicito.TenantIdActual is { } ambito && ambito == TenantOrigenId ? Rol : RolFueraDelOrigen);
        public Task<string?> ObtenerRolOrigenAsync() => Task.FromResult(Rol);
        public Task<Guid?> ObtenerTenantOrigenIdAsync() => Task.FromResult(TenantOrigenId);
        public Task<bool> TieneDobleFactorActivoAsync() => Task.FromResult(true);
    }
}
