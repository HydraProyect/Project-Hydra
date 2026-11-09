using System.Data;
using System.Data.Common;
using CaeManager.Application.Common;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Interceptors;
using CaeManager.Infrastructure.Persistence.Seed;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace CaeManager.IntegrationTests.Tenants;

/// <summary>
/// Una conexión y un contexto RLS firmado por operación de
/// <see cref="PuertaAccesoDatos"/> (decisión D3 del plan de rendimiento), contra
/// PostgreSQL real y autenticando como <c>cae_app_runtime</c>.
///
/// <para>
/// Lo que se mide es el mecanismo nuevo y que las garantías de antes siguen:
/// N comandos de una operación pagan un solo <c>set_config</c> firmado (el
/// token no cambia entre comandos y hay una sola apertura física); un cambio de
/// Tenant, de sesión privilegiada o de contexto a mitad de operación llega al
/// comando siguiente; una excepción a mitad cierra la conexión; una transacción
/// por la estrategia de reintentos sigue funcionando dentro de la operación; y
/// una conexión que tenía abierta otro no la cierra la puerta. La reutilización
/// de la conexión física entre usuarios va aparte, en
/// <see cref="ConexionPorOperacionPoolTests"/>, porque necesita el pool sin
/// vaciados concurrentes.
/// </para>
///
/// <para>
/// Las lecturas de filas usan <c>IgnoreQueryFilters</c> a propósito: así solo
/// filtra RLS con el contexto de la conexión, y lo que se ve demuestra qué
/// contexto lleva el comando (con el filtro de EF, el cambio de Tenant se vería
/// aunque la conexión siguiera con el contexto viejo).
/// </para>
/// </summary>
public class ConexionPorOperacionTests : IAsyncLifetime
{
    internal readonly string CadenaPropietario = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    internal Guid TenantA { get; private set; }
    internal Guid TenantB { get; private set; }

    public async Task InitializeAsync()
    {
        // Mismo andamiaje que ContextoRlsFirmadoTests: Tenants reales y sus
        // Asignaciones de Operación raíz, que es lo que cada usuario ve por RLS.
        var opciones = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(CadenaPropietario, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .Options;
        await using var contexto = new CaeManagerDbContext(opciones, new EphemeralDataProtectionProvider(), new TenantActualAmbiental());
        await contexto.Database.MigrateAsync();

        var a = new Tenant("Tenant de prueba A");
        var b = new Tenant("Tenant de prueba B");
        contexto.Tenants.AddRange(a, b);
        await contexto.SaveChangesAsync();
        await AsignacionesOperativasBackfillSeeder.SeedAsync(contexto, NullLogger.Instance);
        TenantA = a.Id;
        TenantB = b.Id;
    }
    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(CadenaPropietario);

    [Fact]
    public async Task N_comandos_de_una_operacion_pagan_un_solo_contexto_firmado()
    {
        var puerta = new PuertaAccesoDatos();
        var (contexto, aperturas) = CrearContextoRuntime(new TenantMutable(TenantA), new SeleccionMutable(), puerta);
        await using var _ = contexto;

        // Control del instrumento: fuera de la puerta, cada comando abre la
        // conexión y firma un token nuevo (nonce aleatorio), así que dos
        // lecturas seguidas ven tokens distintos.
        var fueraPrimero = await TokenAsync(contexto);
        var fueraSegundo = await TokenAsync(contexto);
        fueraPrimero.Should().NotBeEmpty("sin token firmado el resto del test no mediría nada");
        fueraSegundo.Should().NotBe(fueraPrimero, "control: fuera de la operación, un set_config por comando");
        aperturas.Aperturas.Should().Be(2);

        aperturas.Aperturas = 0;
        var tokens = await puerta.EjecutarAsync(async () =>
        {
            var leidos = new List<string>();
            for (var i = 0; i < 4; i++)
                leidos.Add(await TokenAsync(contexto));
            (await TenantsVisiblesAsync(contexto)).Should().Equal([TenantA]);
            leidos.Add(await TokenAsync(contexto));
            return leidos;
        });

        tokens.Distinct().Should().ContainSingle(
            "dentro de la operación ningún comando vuelve a firmar: un solo set_config para todos");
        aperturas.Aperturas.Should().Be(1, "una sola apertura física por operación");
        contexto.Database.GetDbConnection().State.Should().Be(ConnectionState.Closed,
            "al salir de la operación la conexión vuelve al pool");
    }

    [Fact]
    public async Task Un_cambio_de_Tenant_a_mitad_de_operacion_llega_al_comando_siguiente()
    {
        var puerta = new PuertaAccesoDatos();
        var tenant = new TenantMutable(TenantA);
        var (contexto, aperturas) = CrearContextoRuntime(tenant, new SeleccionMutable(), puerta);
        await using var _ = contexto;

        await puerta.EjecutarAsync(async () =>
        {
            var tokenA = await TokenAsync(contexto);
            (await TenantsVisiblesAsync(contexto)).Should().Equal([TenantA], "control positivo antes del cambio");

            tenant.TenantId = TenantB;

            (await TenantsVisiblesAsync(contexto)).Should().Equal([TenantB],
                "el comando siguiente al cambio lleva el contexto nuevo, nunca el viejo");
            (await TokenAsync(contexto)).Should().NotBe(tokenA, "el cambio obliga a firmar de nuevo");

            tenant.TenantId = null;
            (await TenantsVisiblesAsync(contexto)).Should().BeEmpty("sin Tenant, fallo cerrado también a mitad");
        });

        aperturas.Aperturas.Should().Be(1, "se realinea sobre la misma conexión, sin reabrirla");
    }

    [Fact]
    public async Task Guardar_tras_un_cambio_de_Tenant_a_mitad_de_operacion_escribe_con_el_contexto_nuevo()
    {
        var puerta = new PuertaAccesoDatos();
        var tenant = new TenantMutable(TenantA);
        var (contexto, _) = CrearContextoRuntime(tenant, new SeleccionMutable(), puerta);
        await using var __ = contexto;
        var empresa = Empresa.CrearComoCliente(
            "Escrita tras el cambio S.L.", "B12345674", esCritico: false, notas: null, ejecutivoUsuarioId: null);

        await puerta.EjecutarAsync(async () =>
        {
            (await TenantsVisiblesAsync(contexto)).Should().Equal([TenantA], "la operación retiene la conexión con A");

            tenant.TenantId = TenantB;
            contexto.Empresas.Add(empresa);

            // Con el contexto de A, el WITH CHECK rechazaría la fila sellada con B (42501).
            await contexto.SaveChangesAsync();
        });

        (await TenantDeEmpresaComoPropietarioAsync(empresa.Id)).Should().Be(TenantB);
    }

    [Fact]
    public async Task Retirar_la_sesion_privilegiada_a_mitad_de_operacion_devuelve_el_rol_en_el_comando_siguiente()
    {
        var puerta = new PuertaAccesoDatos();
        var seleccion = new SeleccionMutable { SesionPrivilegiadaIdSeleccionada = Guid.NewGuid() };
        var (contexto, aperturas) = CrearContextoRuntime(new TenantMutable(TenantA), seleccion, puerta);
        await using var _ = contexto;

        await puerta.EjecutarAsync(async () =>
        {
            (await RolActualAsync(contexto)).Should().Be(("cae_app_soporte", true),
                "control: con sesión privilegiada la conexión adopta el rol de solo lectura");
            (await TenantsVisiblesAsync(contexto)).Should().Equal([TenantA], "y sigue acotada por RLS");

            seleccion.SesionPrivilegiadaIdSeleccionada = null;
            var (rol, esSoporte) = await RolActualAsync(contexto);
            esSoporte.Should().BeFalse($"retirada la sesión, el comando siguiente ya no corre como soporte (corrió como {rol})");
            (await TenantsVisiblesAsync(contexto)).Should().Equal([TenantA]);

            seleccion.SesionPrivilegiadaIdSeleccionada = Guid.NewGuid();
            (await RolActualAsync(contexto)).Should().Be(("cae_app_soporte", true),
                "y al volver a seleccionarla, el siguiente vuelve a ser de solo lectura");
        });

        aperturas.Aperturas.Should().Be(1);
    }

    [Fact]
    public async Task Una_excepcion_a_mitad_de_operacion_cierra_la_conexion()
    {
        var puerta = new PuertaAccesoDatos();
        var (contexto, aperturas) = CrearContextoRuntime(new TenantMutable(TenantA), new SeleccionMutable(), puerta);
        await using var _ = contexto;

        var accion = () => puerta.EjecutarAsync(async () =>
        {
            await TokenAsync(contexto);
            contexto.Database.GetDbConnection().State.Should().Be(ConnectionState.Open, "control: retenida");
            throw new InvalidOperationException("fallo a mitad de la operación");
        });

        await accion.Should().ThrowAsync<InvalidOperationException>();
        contexto.Database.GetDbConnection().State.Should().Be(ConnectionState.Closed,
            "una excepción no deja la conexión abierta fuera de la operación");

        (await TenantsVisiblesAsync(contexto)).Should().Equal([TenantA],
            "el contexto sigue utilizable: la apertura siguiente prepara la sesión de cero");
        aperturas.Aperturas.Should().Be(2);
    }

    [Fact]
    public async Task Una_transaccion_por_la_estrategia_de_reintentos_funciona_dentro_de_la_operacion()
    {
        var puerta = new PuertaAccesoDatos();
        var (contexto, aperturas) = CrearContextoRuntime(new TenantMutable(TenantA), new SeleccionMutable(), puerta);
        await using var _ = contexto;
        var empresa = Empresa.CrearComoCliente(
            "Escrita en transacción S.L.", "B12345674", esCritico: false, notas: null, ejecutivoUsuarioId: null);

        await puerta.EjecutarAsync(async () =>
        {
            (await TenantsVisiblesAsync(contexto)).Should().Equal([TenantA]);

            var estrategia = contexto.Database.CreateExecutionStrategy();
            estrategia.RetriesOnFailure.Should().BeTrue("control: la estrategia es la de reintentos de producción");
            await estrategia.ExecuteAsync(async () =>
            {
                await using var transaccion = await contexto.Database.BeginTransactionAsync();
                contexto.Empresas.Add(empresa);
                await contexto.SaveChangesAsync();
                await transaccion.CommitAsync();
            });

            contexto.Database.GetDbConnection().State.Should().Be(ConnectionState.Open,
                "cerrar la transacción no devuelve la conexión retenida por la operación");
        });

        contexto.Database.GetDbConnection().State.Should().Be(ConnectionState.Closed);
        aperturas.Aperturas.Should().Be(1);
        (await TenantDeEmpresaComoPropietarioAsync(empresa.Id)).Should().Be(TenantA);
    }

    [Fact]
    public async Task Una_conexion_que_abrio_otro_no_la_cierra_la_puerta()
    {
        var puerta = new PuertaAccesoDatos();
        var (contexto, aperturas) = CrearContextoRuntime(new TenantMutable(TenantA), new SeleccionMutable(), puerta);
        await using var _ = contexto;

        await contexto.Database.OpenConnectionAsync();
        try
        {
            await puerta.EjecutarAsync(async () =>
            {
                (await TenantsVisiblesAsync(contexto)).Should().Equal([TenantA]);
                await TokenAsync(contexto);
            });

            contexto.Database.GetDbConnection().State.Should().Be(ConnectionState.Open,
                "la apertura de fuera sigue siendo de quien la hizo");
            (await TenantsVisiblesAsync(contexto)).Should().Equal([TenantA]);
        }
        finally
        {
            await contexto.Database.CloseConnectionAsync();
        }

        contexto.Database.GetDbConnection().State.Should().Be(ConnectionState.Closed);
        aperturas.Aperturas.Should().Be(1);
    }

    // ── Andamiaje (también lo usa ConexionPorOperacionPoolTests) ───────────

    internal (CaeManagerDbContext Contexto, ContadorAperturas Aperturas) CrearContextoRuntime(
        TenantMutable tenant, SeleccionMutable seleccion, PuertaAccesoDatos puerta, string? cadena = null,
        Guid? usuarioId = null)
    {
        var aperturas = new ContadorAperturas();
        var opciones = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(
                cadena ?? BaseDatosPostgresDePruebas.CadenaComoRuntime(CadenaPropietario),
                npgsql => npgsql.EnableRetryOnFailure())
            // Mismo orden que ConfiguracionDeContexto: el interceptor RLS delante del sellado.
            .AddInterceptors(
                new TenantRlsConnectionInterceptor(
                    tenant, seleccion, new UsuarioDelTenant(usuarioId ?? Guid.NewGuid(), () => tenant.TenantId),
                    BaseDatosPostgresDePruebas.FirmanteContextoRls, puerta),
                new TenantSelladoInterceptor(tenant),
                aperturas)
            .Options;

        return (new CaeManagerDbContext(opciones, new EphemeralDataProtectionProvider(), tenant), aperturas);
    }

    internal static Task<string> TokenAsync(CaeManagerDbContext contexto) =>
        contexto.Database
            .SqlQueryRaw<string>("SELECT coalesce(current_setting('app.contexto', true), '') AS \"Value\"")
            .SingleAsync();

    internal static Task<int> PidAsync(CaeManagerDbContext contexto) =>
        contexto.Database.SqlQueryRaw<int>("SELECT pg_backend_pid() AS \"Value\"").SingleAsync();

    internal static Task<List<Guid>> TenantsVisiblesAsync(CaeManagerDbContext contexto) =>
        contexto.AsignacionesOperacion.IgnoreQueryFilters().Where(a => a.EsRaiz).Select(a => a.PropietarioTenantId).ToListAsync();

    private static async Task<(string Rol, bool EsSoporte)> RolActualAsync(CaeManagerDbContext contexto)
    {
        var rol = await contexto.Database
            .SqlQueryRaw<string>("SELECT current_user::text AS \"Value\"").SingleAsync();
        return (rol, rol == "cae_app_soporte");
    }

    private async Task<Guid> TenantDeEmpresaComoPropietarioAsync(Guid empresaId)
    {
        await using var conexion = new NpgsqlConnection(CadenaPropietario);
        await conexion.OpenAsync();
        await using var comando = conexion.CreateCommand();
        comando.CommandText = "SELECT \"TenantId\" FROM \"Empresas\" WHERE \"Id\" = @id;";
        comando.Parameters.AddWithValue("id", empresaId);
        return (Guid)(await comando.ExecuteScalarAsync())!;
    }

    internal sealed class TenantMutable(Guid? tenantId) : ITenantActual
    {
        public Guid? TenantId { get; set; } = tenantId;
    }

    internal sealed class SeleccionMutable : IClienteActivoSeleccionado
    {
        public Guid? TenantIdSeleccionado => null;
        public Guid? AsignacionOperacionIdSeleccionada => null;
        public Guid? SesionPrivilegiadaIdSeleccionada { get; set; }
    }

    /// <summary>
    /// Usuario cuyo Tenant de origen es el Tenant activo en cada instante: así un
    /// cambio de Tenant a mitad de operación cambia el contexto entero (objetivo y
    /// origen) sin depender de Asignaciones de Operación entre Tenants, que no son
    /// lo que este test mide. Lo que mide es que el contexto nuevo llegue al
    /// comando siguiente, sea cual sea la coordenada que cambió.
    /// </summary>
    private sealed class UsuarioDelTenant(Guid usuarioId, Func<Guid?> tenantOrigenId) : ICurrentUserService
    {
        public Task<Guid?> ObtenerUsuarioActualIdAsync() => Task.FromResult<Guid?>(usuarioId);
        public Task<string?> ObtenerRolOrigenAsync() => ObtenerRolEfectivoAsync();
        public Task<string?> ObtenerRolEfectivoAsync() => Task.FromResult<string?>("Administrador");
        public Task<Guid?> ObtenerTenantOrigenIdAsync() => Task.FromResult(tenantOrigenId());
        public Task<bool> TieneDobleFactorActivoAsync() => Task.FromResult(true);
    }
    /// <summary>Aperturas físicas de la conexión que EF hizo para este contexto.</summary>
    internal sealed class ContadorAperturas : DbConnectionInterceptor
    {
        public int Aperturas;

        public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData) =>
            Interlocked.Increment(ref Aperturas);

        public override Task ConnectionOpenedAsync(
            DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Aperturas);
            return Task.CompletedTask;
        }
    }
}

/// <summary>
/// La conexión física que una operación de la puerta retuvo vuelve al pool sin
/// el contexto ni el rol de su usuario, y el siguiente usuario que la saca solo
/// ve lo suyo. Necesita que el pool devuelva el mismo backend, así que corre en
/// <see cref="ColeccionPoolNpgsqlSinVaciadosConcurrentes"/> (ver su porqué) y
/// reutiliza la base y el andamiaje de <see cref="ConexionPorOperacionTests"/>
/// por composición. Clase de primer nivel por el mismo motivo que
/// <see cref="ContextoRlsFirmadoPoolTests"/>.
/// </summary>
[Collection(ColeccionPoolNpgsqlSinVaciadosConcurrentes.Nombre)]
public sealed class ConexionPorOperacionPoolTests : IAsyncLifetime
{
    private readonly ConexionPorOperacionTests _clase = new();

    public Task InitializeAsync() => _clase.InitializeAsync();

    public Task DisposeAsync() => _clase.DisposeAsync();

    [Fact]
    public async Task Dos_usuarios_que_reutilizan_la_misma_conexion_fisica_solo_ven_lo_suyo()
    {
        var cadena = CadenaConUnSoloHueco("conexion-por-operacion-dos-usuarios");

        var puertaA = new PuertaAccesoDatos();
        var (contextoA, _) = _clase.CrearContextoRuntime(
            new ConexionPorOperacionTests.TenantMutable(_clase.TenantA), new ConexionPorOperacionTests.SeleccionMutable(),
            puertaA, cadena);
        await using var a = contextoA;

        var puertaB = new PuertaAccesoDatos();
        var (contextoB, _) = _clase.CrearContextoRuntime(
            new ConexionPorOperacionTests.TenantMutable(_clase.TenantB), new ConexionPorOperacionTests.SeleccionMutable(),
            puertaB, cadena);
        await using var b = contextoB;

        var (pidA, tokenA) = await puertaA.EjecutarAsync(async () =>
        {
            (await ConexionPorOperacionTests.TenantsVisiblesAsync(contextoA)).Should().Equal([_clase.TenantA]);
            return (await ConexionPorOperacionTests.PidAsync(contextoA), await ConexionPorOperacionTests.TokenAsync(contextoA));
        });

        var (pidB, tokenB, visiblesB) = await puertaB.EjecutarAsync(async () =>
        {
            var pid = await ConexionPorOperacionTests.PidAsync(contextoB);
            var token = await ConexionPorOperacionTests.TokenAsync(contextoB);
            return (pid, token, await ConexionPorOperacionTests.TenantsVisiblesAsync(contextoB));
        });

        pidB.Should().Be(pidA, "el test solo demuestra algo si es el mismo backend físico sacado del pool");
        tokenB.Should().NotBe(tokenA);
        visiblesB.Should().Equal([_clase.TenantB], "el usuario B nunca hereda el contexto de la operación de A");
    }

    [Fact]
    public async Task Tras_una_excepcion_a_mitad_la_conexion_vuelve_al_pool_sin_contexto_ni_rol()
    {
        var cadena = CadenaConUnSoloHueco("conexion-por-operacion-excepcion");
        var puerta = new PuertaAccesoDatos();
        var seleccion = new ConexionPorOperacionTests.SeleccionMutable { SesionPrivilegiadaIdSeleccionada = Guid.NewGuid() };
        var (contexto, _) = _clase.CrearContextoRuntime(
            new ConexionPorOperacionTests.TenantMutable(_clase.TenantA), seleccion, puerta, cadena);
        await using var _c = contexto;
        var pid = 0;

        var accion = () => puerta.EjecutarAsync(async () =>
        {
            pid = await ConexionPorOperacionTests.PidAsync(contexto);
            (await ConexionPorOperacionTests.TenantsVisiblesAsync(contexto)).Should().Equal([_clase.TenantA],
                "control positivo: la conexión lleva el contexto de A y el rol de soporte");
            throw new InvalidOperationException("fallo a mitad de la operación");
        });
        await accion.Should().ThrowAsync<InvalidOperationException>();

        await using var reutilizada = new NpgsqlConnection(cadena);
        await reutilizada.OpenAsync();
        reutilizada.ProcessID.Should().Be(pid, "el mismo backend físico, devuelto al pool por la puerta");

        await using var comando = reutilizada.CreateCommand();
        comando.CommandText =
            "SELECT coalesce(current_setting('app.contexto', true), ''), current_user::text = session_user::text, " +
            "(SELECT count(*) FROM \"AsignacionesOperacion\" WHERE \"EsRaiz\");";
        await using var lector = await comando.ExecuteReaderAsync();
        await lector.ReadAsync();
        lector.GetString(0).Should().BeEmpty("el contexto firmado no sobrevive a la devolución al pool");
        lector.GetBoolean(1).Should().BeTrue("ni el rol de soporte");
        lector.GetInt64(2).Should().Be(0, "sin contexto, RLS no deja ver ninguna fila");
    }

    /// <summary>
    /// Pool propio (Application Name distinto = cadena distinta = pool distinto)
    /// con un solo hueco: la segunda apertura tiene que ser el mismo backend.
    /// </summary>
    private string CadenaConUnSoloHueco(string nombre) =>
        new NpgsqlConnectionStringBuilder(BaseDatosPostgresDePruebas.CadenaComoRuntime(_clase.CadenaPropietario))
        {
            ApplicationName = nombre,
            MaxPoolSize = 1,
        }.ConnectionString;
}
