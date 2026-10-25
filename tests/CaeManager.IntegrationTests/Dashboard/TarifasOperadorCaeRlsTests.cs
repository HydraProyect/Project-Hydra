using CaeManager.Application.Common;
using CaeManager.Application.Dashboard;
using CaeManager.Application.Dashboard.TarifasOperadorCae;
using CaeManager.Domain.Common;
using CaeManager.Domain.Dashboard;
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

namespace CaeManager.IntegrationTests.Dashboard;

/// <summary>
/// Tarifas del Operador CAE externo por Empresa gestionada (decisión de Chris, 2026-10-20) con el cableado de
/// producción (<see cref="ArnesDeArranqueRuntime"/>: conexión como <c>cae_app_runtime</c> y sus interceptores), para
/// que la RLS <c>aislamiento_tenant</c> de la migración <c>AnadeTarifasOperadorCae</c> sea la que se mide.
///
/// <para>
/// Escenario: el Operador A opera hoy a la Empresa X (Asignación de Operación externa vigente); el Operador B opera
/// a la Empresa Y. Las tarifas son filas del Tenant de origen de cada Operador: ni el otro Operador ni la Empresa
/// gestionada las ven, un Gestor CAE no las fija, y no se fija una tarifa a una Empresa que el Operador no gestiona.
/// </para>
/// </summary>
public class TarifasOperadorCaeRlsTests : IAsyncLifetime
{
    private const string PermisoDenegado = "42501";

    private readonly Guid _usuario = Guid.NewGuid();
    private readonly UsuarioConmutable _sesion = new();
    private ArnesDeArranqueRuntime _arnes = null!;

    private Guid _operadorA;
    private Guid _operadorB;
    private Guid _empresaX;
    private Guid _empresaY;

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
        propietario.Tenants.AddRange(a, b, x, y);
        await propietario.SaveChangesAsync();
        (_operadorA, _operadorB, _empresaX, _empresaY) = (a.Id, b.Id, x.Id, y.Id);

        var ahora = DateTime.UtcNow;
        using (AmbitoTenantExplicito.Establecer(_operadorA))
            propietario.AsignacionesOperacion.Add(AsignacionOperacion.Externa(
                _empresaX, _operadorA, ServicioCae.Outbound, AmbitoAsignacion.Universal, ahora.AddDays(-2), null, ahora));
        using (AmbitoTenantExplicito.Establecer(_operadorB))
            propietario.AsignacionesOperacion.Add(AsignacionOperacion.Externa(
                _empresaY, _operadorB, ServicioCae.Outbound, AmbitoAsignacion.Universal, ahora.AddDays(-2), null, ahora));
        await propietario.SaveChangesAsync();
    }

    public async Task DisposeAsync() => await _arnes.DisposeAsync();

    [Fact]
    public async Task Un_Operador_CAE_no_ve_las_tarifas_de_otro_ni_la_Empresa_gestionada_las_del_suyo()
    {
        Como("Administrador", _operadorA);
        (await FijarAsync(_empresaX, ConceptoTarifaOperadorCae.CentroNuevo, 50m)).EsExitoso.Should().BeTrue();

        (await LeerAsync()).Should().ContainSingle("control positivo: el Administrador del Operador A ve su tarifa")
            .Which.Should().Match<TarifaOperadorCaeDto>(t =>
                t.PropietarioTenantId == _empresaX && t.Concepto == ConceptoTarifaOperadorCae.CentroNuevo && t.PrecioUnitario == 50m);

        Como("DireccionCae", _operadorB);
        (await LeerAsync()).Should().BeEmpty("las tarifas del Operador A no son filas del Tenant del Operador B");

        // La base, sin ayuda del filtro global ni del handler: la política por TenantId decide.
        (await ContarComoRuntimeAsync(_operadorA)).Should().Be(1, "control positivo de la RLS");
        (await ContarComoRuntimeAsync(_operadorB)).Should().Be(0, "otro Operador CAE no lee las tarifas del Operador A");
        (await ContarComoRuntimeAsync(_empresaX)).Should().Be(0, "la Empresa gestionada tampoco: la fila es del Operador");
    }

    [Theory]
    [InlineData("GestorCae")]
    [InlineData("CoordinadorCae")]
    [InlineData(null)]
    public async Task Ni_un_Gestor_CAE_ni_un_Coordinador_CAE_ni_una_sesion_sin_rol_fijan_ni_leen_tarifas(string? rol)
    {
        Como(rol, _operadorA);

        var resultado = await FijarAsync(_empresaX, ConceptoTarifaOperadorCae.ObraNueva, 100m);

        resultado.EsExitoso.Should().BeFalse();
        resultado.Error.Should().Be(AutorizacionTarifasOperadorCae.SinPermiso);
        (await LeerAsync()).Should().BeNull("sin Administrador o Dirección CAE del Operador no hay tarifas que ver");
        (await ContarComoRuntimeAsync(_operadorA)).Should().Be(0);
    }

    [Fact]
    public async Task No_se_fija_una_tarifa_a_una_Empresa_que_el_Operador_no_gestiona_ni_a_si_mismo()
    {
        Como("Administrador", _operadorA);

        (await FijarAsync(_empresaY, ConceptoTarifaOperadorCae.VisitaTrabajadorExtranjero, 20m)).Error
            .Should().Be(AutorizacionTarifasOperadorCae.EmpresaNoGestionada, "la Empresa Y la opera el Operador B");
        (await FijarAsync(_operadorA, ConceptoTarifaOperadorCae.VisitaTrabajadorExtranjero, 20m)).Error
            .Should().Be(AutorizacionTarifasOperadorCae.EmpresaNoGestionada, "el Operador no se cobra a sí mismo");
        (await ContarComoRuntimeAsync(_operadorA)).Should().Be(0);

        (await FijarAsync(_empresaX, ConceptoTarifaOperadorCae.VisitaTrabajadorExtranjero, 20m)).EsExitoso
            .Should().BeTrue("control positivo: la Empresa X sí la gestiona");
    }

    [Fact]
    public async Task La_base_no_admite_escribir_en_otro_Tenant_ni_editar_una_version()
    {
        Como("Administrador", _operadorA);
        (await FijarAsync(_empresaX, ConceptoTarifaOperadorCae.CentroNuevo, 50m)).EsExitoso.Should().BeTrue();

        await using var conexion = await AbrirComoRuntimeAsync(_operadorB);
        await using var colada = conexion.CreateCommand();
        colada.CommandText = """
            INSERT INTO "TarifasOperadorCae" ("Id", "PropietarioTenantId", "Concepto", "PrecioUnitario", "MonedaIso", "Activa", "VigenteDesdeUtc", "RegistradaPorUsuarioId", "TenantId")
            VALUES (@id, @empresa, 2, 1, 'EUR', true, now(), @usuario, @operador);
            """;
        colada.Parameters.AddWithValue("id", Guid.NewGuid());
        colada.Parameters.AddWithValue("empresa", _empresaX);
        colada.Parameters.AddWithValue("usuario", _usuario);
        colada.Parameters.AddWithValue("operador", _operadorA);
        (await Record.ExceptionAsync(() => colada.ExecuteNonQueryAsync())).Should().BeOfType<PostgresException>()
            .Which.SqlState.Should().Be(PermisoDenegado, "WITH CHECK de aislamiento_tenant: el Operador B no escribe filas del Operador A");

        await using var propia = await AbrirComoRuntimeAsync(_operadorA);
        await using var edicion = propia.CreateCommand();
        edicion.CommandText = """UPDATE "TarifasOperadorCae" SET "PrecioUnitario" = 1;""";
        (await Record.ExceptionAsync(() => edicion.ExecuteNonQueryAsync())).Should().BeOfType<PostgresException>()
            .Which.SqlState.Should().Be(PermisoDenegado, "historial de versiones: cae_app_runtime no tiene UPDATE sobre la tabla");
    }

    [Fact]
    public async Task Soporte_TALVEG_no_lee_las_tarifas_ni_dentro_del_Tenant_del_Operador()
    {
        Como("Administrador", _operadorA);
        (await FijarAsync(_empresaX, ConceptoTarifaOperadorCae.CentroNuevo, 50m)).EsExitoso.Should().BeTrue();

        await using var conexion = new NpgsqlConnection(_arnes.CadenaPropietario);
        await conexion.OpenAsync();
        await using (var rol = conexion.CreateCommand())
        {
            rol.CommandText = "SET ROLE cae_app_soporte;";
            await rol.ExecuteNonQueryAsync();
        }

        await FijarTenantAsync(conexion, _operadorA);
        await using var consulta = conexion.CreateCommand();
        consulta.CommandText = """SELECT count(*) FROM "TarifasOperadorCae";""";
        (await Record.ExceptionAsync(() => consulta.ExecuteScalarAsync())).Should().BeOfType<PostgresException>()
            .Which.SqlState.Should().Be(PermisoDenegado, "la migración no concede nada a cae_app_soporte: Soporte TALVEG no es Operador CAE");
    }

    [Fact]
    public async Task La_guarda_mira_el_rol_en_el_Tenant_de_origen_del_Operador_y_no_en_el_del_ambito()
    {
        // Gestor CAE en su Operador y Administrador en el Tenant del ámbito: no fija tarifas.
        Como("GestorCae", _operadorA, rolFueraDelOrigen: "Administrador");
        using (AmbitoTenantExplicito.Establecer(_empresaX))
            (await FijarAsync(_empresaX, ConceptoTarifaOperadorCae.CentroNuevo, 50m)).Error
                .Should().Be(AutorizacionTarifasOperadorCae.SinPermiso, "el rol que cuenta es el del Tenant de origen del Operador");

        // Al revés, Administrador en su Operador y Gestor CAE en el Tenant del ámbito: sí las fija.
        Como("Administrador", _operadorA, rolFueraDelOrigen: "GestorCae");
        using (AmbitoTenantExplicito.Establecer(_empresaX))
            (await FijarAsync(_empresaX, ConceptoTarifaOperadorCae.CentroNuevo, 50m)).EsExitoso
                .Should().BeTrue("control positivo: Administrador en el Tenant de origen del Operador");
        (await ContarComoRuntimeAsync(_operadorA)).Should().Be(1);
    }

    [Fact]
    public async Task La_base_no_admite_borrar_ni_una_version_retroactiva_ni_incoherente()
    {
        Como("Administrador", _operadorA);
        (await FijarAsync(_empresaX, ConceptoTarifaOperadorCae.CentroNuevo, 50m)).EsExitoso.Should().BeTrue();

        await using var conexion = await AbrirComoRuntimeAsync(_operadorA);
        await using (var borrado = conexion.CreateCommand())
        {
            borrado.CommandText = """DELETE FROM "TarifasOperadorCae";""";
            (await Record.ExceptionAsync(() => borrado.ExecuteNonQueryAsync())).Should().BeOfType<PostgresException>()
                .Which.SqlState.Should().Be(PermisoDenegado, "historial de versiones: cae_app_runtime no tiene DELETE (la purga corre como propietario)");
        }

        (await InsertarComoRuntimeAsync(conexion, _empresaX, 10m, "now() - interval '1 day'")).Should().Be(
            PostgresErrorCodes.CheckViolation, "una versión que empieza en el pasado cambiaría meses ya cerrados");
        (await InsertarComoRuntimeAsync(conexion, _operadorA, 10m, "now()")).Should().Be(
            PostgresErrorCodes.CheckViolation, "el Operador CAE no se cobra a sí mismo");
        (await InsertarComoRuntimeAsync(conexion, _empresaX, -1m, "now()")).Should().Be(
            PostgresErrorCodes.CheckViolation, "no hay precios negativos");
        (await InsertarComoRuntimeAsync(conexion, _empresaX, 10m, "now()")).Should().BeNull(
            "control positivo: una versión desde ahora se registra");
        (await ContarComoRuntimeAsync(_operadorA)).Should().Be(2);
    }

    /// <summary>Inserta una versión del Operador A como runtime; devuelve el SqlState del error o null si entra.</summary>
    private async Task<string?> InsertarComoRuntimeAsync(NpgsqlConnection conexion, Guid propietario, decimal precio, string vigenteDesde)
    {
        await using var insercion = conexion.CreateCommand();
        insercion.CommandText = $"""
            INSERT INTO "TarifasOperadorCae" ("Id", "PropietarioTenantId", "Concepto", "PrecioUnitario", "MonedaIso", "Activa", "VigenteDesdeUtc", "RegistradaPorUsuarioId", "TenantId")
            VALUES (@id, @propietario, 2, @precio, 'EUR', true, {vigenteDesde}, @usuario, @operador);
            """;
        insercion.Parameters.AddWithValue("id", Guid.NewGuid());
        insercion.Parameters.AddWithValue("propietario", propietario);
        insercion.Parameters.AddWithValue("precio", precio);
        insercion.Parameters.AddWithValue("usuario", _usuario);
        insercion.Parameters.AddWithValue("operador", _operadorA);
        return await Record.ExceptionAsync(() => insercion.ExecuteNonQueryAsync()) switch
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

    private async Task<Result> FijarAsync(Guid empresa, ConceptoTarifaOperadorCae concepto, decimal precio)
    {
        await using var scope = _arnes.Servicios.CreateAsyncScope();
        var contexto = scope.ServiceProvider.GetRequiredService<CaeManagerDbContext>();
        var handler = new FijarTarifaOperadorCaeCommandHandler(_sesion, contexto, contexto, contexto, TimeProvider.System);
        return await handler.Handle(new FijarTarifaOperadorCaeCommand(empresa, concepto, precio, "EUR", true), CancellationToken.None);
    }

    private async Task<IReadOnlyList<TarifaOperadorCaeDto>?> LeerAsync()
    {
        await using var scope = _arnes.Servicios.CreateAsyncScope();
        var contexto = scope.ServiceProvider.GetRequiredService<CaeManagerDbContext>();
        return await new ObtenerTarifasOperadorCaeQueryHandler(_sesion, contexto).Handle(new ObtenerTarifasOperadorCaeQuery(), CancellationToken.None);
    }

    private async Task<long> ContarComoRuntimeAsync(Guid tenant)
    {
        await using var conexion = await AbrirComoRuntimeAsync(tenant);
        await using var consulta = conexion.CreateCommand();
        consulta.CommandText = """SELECT count(*) FROM "TarifasOperadorCae";""";
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
