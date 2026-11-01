using CaeManager.Application.Common;
using CaeManager.Domain.Integraciones;
using CaeManager.Domain.Plataforma;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace CaeManager.IntegrationTests.Autenticacion;

/// <summary>
/// ASVS 4.0.3 V4.1.3 y V4.2.1: las resoluciones transversales que se usan antes de conocer el Tenant
/// (cuenta por nombre, correo o Id, webhooks de Graph y WhatsApp) y la comprobación del Actor de Plataforma
/// TALVEG no responden a quien solo tiene la credencial de <c>cae_app_runtime</c>: exigen el contexto RLS
/// firmado, que la aplicación firma en cada conexión y que no se puede fabricar por SQL. Se mide con
/// conexiones autenticadas como <c>cae_app_runtime</c>, sin <c>SET ROLE</c> desde el propietario, porque la
/// excepción de la guardia mira el rol de la sesión. El contexto firmado sin Tenant ni usuario, el de una
/// petición anónima, sigue resolviendo: es el control positivo de cada comprobación.
/// </summary>
public class OraculosTransversalesBajoRuntimeTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly TenantPorAmbito _tenantActual = new();
    private CaeManagerDbContext _propietario = null!;
    private Guid _tenantA;
    private Guid _tenantB;
    private Guid _cuentaA;
    private Guid _actor;
    private Guid _conexionGraph;
    private string _nombreA = null!;
    private string _correoA = null!;
    private string _correoActor = null!;
    private const string Telefono = "phone-oraculo";

    public async Task InitializeAsync()
    {
        var opciones = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(_tenantActual))
            .Options;
        _propietario = new CaeManagerDbContext(opciones, new EphemeralDataProtectionProvider(), _tenantActual);
        await _propietario.Database.MigrateAsync();

        var tenantA = new Tenant("Tenant propietario A");
        var tenantB = new Tenant("Tenant propietario B");
        var plataforma = new Tenant("TALVEG", esPlataforma: true);
        _propietario.Tenants.AddRange(tenantA, tenantB, plataforma);
        await _propietario.SaveChangesAsync();
        (_tenantA, _tenantB) = (tenantA.Id, tenantB.Id);

        (_cuentaA, _nombreA, _correoA) = await SembrarCuentaAsync(_tenantA);
        (_actor, _, _correoActor) = await SembrarCuentaAsync(plataforma.Id);
        _propietario.ConcesionesPrivilegio.Add(ConcesionPrivilegio.SobreTenants(
            _actor, CapacidadPrivilegio.AdminPlataforma, [_tenantA], DateTime.UtcNow.AddMinutes(-10), null));
        await _propietario.SaveChangesAsync();

        using (AmbitoTenantExplicito.Establecer(_tenantA))
        {
            var buzon = new ConexionIntegracion("buzon@ejemplo.test", "Buzón");
            _conexionGraph = buzon.Id;
            var linea = new ConexionIntegracion($"{Telefono}@ejemplo.test", "Línea");
            _propietario.ConexionesIntegracion.AddRange(buzon, linea);
            _propietario.SuscripcionesWebhook.Add(new SuscripcionWebhook(buzon.Id, "sub", "secreto", DateTime.UtcNow.AddDays(3)));
            _propietario.LineasWhatsApp.Add(new LineaWhatsApp(linea.Id, Telefono, "waba", "+34123456789", "token", ModoAsignacionLinea.PoolInbound));
            await _propietario.SaveChangesAsync();
        }
    }

    public async Task DisposeAsync()
    {
        await _propietario.DisposeAsync();
        await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);
    }

    /// <summary>Cada resolución como consulta booleana: «¿ha respondido con el dato?».</summary>
    private IReadOnlyList<(string Funcion, string Sql)> Resoluciones =>
    [
        ("app_cuenta_por_nombre_normalizado", $"SELECT EXISTS (SELECT 1 FROM app_cuenta_por_nombre_normalizado('{_nombreA}'))"),
        ("app_cuentas_por_email_normalizado", $"SELECT EXISTS (SELECT 1 FROM app_cuentas_por_email_normalizado('{_correoA}'))"),
        ("app_tenant_de_cuenta", $"SELECT app_tenant_de_cuenta('{_cuentaA}') IS NOT NULL"),
        ("app_tenant_de_webhook_graph", $"SELECT app_tenant_de_webhook_graph('{_conexionGraph}') IS NOT NULL"),
        ("app_tenant_de_webhook_whatsapp", $"SELECT app_tenant_de_webhook_whatsapp('{Telefono}') IS NOT NULL"),
        ("app_nombre_en_uso_por_otra_cuenta", $"SELECT app_nombre_en_uso_por_otra_cuenta('{_nombreA}', '{Guid.NewGuid()}')"),
        ("app_correo_en_uso_por_otra_cuenta", $"SELECT app_correo_en_uso_por_otra_cuenta('{_correoA}', '{Guid.NewGuid()}')"),
    ];

    [Fact]
    public async Task Sin_contexto_firmado_ninguna_resolucion_responde_y_con_el_contexto_anonimo_si()
    {
        await using var conexion = await AbrirComoRuntimeAsync();

        foreach (var (funcion, sql) in Resoluciones)
            await ExigeContextoFirmadoAsync(conexion, sql, funcion);

        await ContextoRlsDePruebas.FijarAsync(conexion);
        foreach (var (funcion, sql) in Resoluciones)
            (await ConsultarAsync(conexion, sql)).Should().BeTrue(funcion);
    }

    [Fact]
    public async Task Las_variables_de_sesion_sueltas_no_sustituyen_al_contexto_firmado()
    {
        await using var conexion = await AbrirComoRuntimeAsync();
        await using (var variables = new NpgsqlCommand(
            "SELECT set_config('app.tenant_id', @tenant, false), set_config('app.usuario_id', @usuario, false)", conexion))
        {
            variables.Parameters.AddWithValue("tenant", _tenantA.ToString());
            variables.Parameters.AddWithValue("usuario", _cuentaA.ToString());
            await variables.ExecuteNonQueryAsync();
        }

        foreach (var (funcion, sql) in Resoluciones)
            await ExigeContextoFirmadoAsync(conexion, sql, funcion);
    }

    [Fact]
    public async Task La_identidad_que_ya_evita_RLS_conserva_la_resolucion_sin_firmar()
    {
        // La siembra y el backfill del arranque conectan como propietario: la guardia no les quita nada que
        // la propia tabla no les dé ya, y no la limita ningún Tenant de contexto porque no llevan ninguno.
        await using var conexion = new NpgsqlConnection(_cadenaConexion);
        await conexion.OpenAsync();

        foreach (var (funcion, sql) in Resoluciones)
            (await ConsultarAsync(conexion, sql)).Should().BeTrue(funcion);
    }

    [Fact]
    public async Task Los_webhooks_no_se_resuelven_desde_una_conexion_con_Tenant()
    {
        await using var conexion = await AbrirComoRuntimeAsync();

        await ContextoRlsDePruebas.FijarAsync(conexion, tenantId: _tenantB);
        (await ConsultarAsync(conexion, $"SELECT app_tenant_de_webhook_graph('{_conexionGraph}') IS NULL")).Should().BeTrue();
        (await ConsultarAsync(conexion, $"SELECT app_tenant_de_webhook_whatsapp('{Telefono}') IS NULL")).Should().BeTrue();

        // Ni siquiera el propio: la entrada de un webhook es anónima.
        await ContextoRlsDePruebas.FijarAsync(conexion, tenantId: _tenantA);
        (await ConsultarAsync(conexion, $"SELECT app_tenant_de_webhook_graph('{_conexionGraph}') IS NULL")).Should().BeTrue();

        await ContextoRlsDePruebas.FijarAsync(conexion);
        (await ConsultarAsync(conexion, $"SELECT app_tenant_de_webhook_graph('{_conexionGraph}') = '{_tenantA}'")).Should().BeTrue();
    }

    [Fact]
    public async Task La_unicidad_solo_dice_si_el_valor_lo_usa_otra_cuenta()
    {
        await using var conexion = await AbrirComoRuntimeAsync();
        await ContextoRlsDePruebas.FijarAsync(conexion, tenantId: _tenantB);

        (await ConsultarAsync(conexion, $"SELECT app_nombre_en_uso_por_otra_cuenta('{_nombreA}', '{Guid.NewGuid()}')")).Should().BeTrue();
        (await ConsultarAsync(conexion, $"SELECT app_nombre_en_uso_por_otra_cuenta('{_nombreA}', '{_cuentaA}')")).Should().BeFalse();
        (await ConsultarAsync(conexion, $"SELECT app_nombre_en_uso_por_otra_cuenta('NADIE', '{Guid.NewGuid()}')")).Should().BeFalse();
        (await ConsultarAsync(conexion, $"SELECT app_correo_en_uso_por_otra_cuenta('{_correoA}', '{Guid.NewGuid()}')")).Should().BeTrue();
        (await ConsultarAsync(conexion, $"SELECT app_correo_en_uso_por_otra_cuenta('{_correoA}', '{_cuentaA}')")).Should().BeFalse();
        (await ConsultarAsync(conexion, $"SELECT app_correo_en_uso_por_otra_cuenta('NADIE', '{Guid.NewGuid()}')")).Should().BeFalse();
    }

    [Fact]
    public async Task El_Actor_que_exporta_solo_se_comprueba_en_el_contexto_sin_usuario_del_Tenant_exportado()
    {
        var sql = $"SELECT EXISTS (SELECT 1 FROM app_actor_plataforma_que_exporta('{_correoActor}', '{_tenantA}'))";
        await using var conexion = await AbrirComoRuntimeAsync();

        await ExigeContextoFirmadoAsync(conexion, sql, "sin contexto");

        // Como la CLI: firmado, con el Tenant exportado y sin usuario.
        await ContextoRlsDePruebas.FijarAsync(conexion, tenantId: _tenantA);
        (await ConsultarAsync(conexion, sql)).Should().BeTrue();

        await ContextoRlsDePruebas.FijarAsync(conexion, tenantId: _tenantB);
        (await RechazoAsync(conexion, sql)).SqlState.Should().Be("42501", "preguntar por otro Tenant es el oráculo que se cierra");

        await ContextoRlsDePruebas.FijarAsync(conexion);
        (await RechazoAsync(conexion, sql)).SqlState.Should().Be("42501");

        // Una conexión de una petición autenticada lleva usuario, aunque el Tenant coincida.
        await ContextoRlsDePruebas.FijarAsync(conexion, tenantId: _tenantA, usuarioId: _cuentaA);
        (await RechazoAsync(conexion, sql)).SqlState.Should().Be("42501");
    }

    [Fact]
    public async Task Contrato_de_catalogo_de_las_resoluciones_y_de_la_guardia()
    {
        await using var propietario = new NpgsqlConnection(_cadenaConexion);
        await propietario.OpenAsync();
        var definidoras = Resoluciones.Select(r => r.Funcion).Append("app_actor_plataforma_que_exporta").ToArray();

        await using var orden = new NpgsqlCommand(
            """
            SELECT p.proname,
                   p.prosecdef,
                   array_to_string(p.proconfig, ';'),
                   has_function_privilege('cae_app_runtime', p.oid, 'EXECUTE'),
                   has_function_privilege('cae_app_soporte', p.oid, 'EXECUTE'),
                   has_function_privilege('cae_app_aprovisionamiento', p.oid, 'EXECUTE'),
                   p.proacl IS NULL OR EXISTS (SELECT 1 FROM aclexplode(p.proacl) a WHERE a.grantee = 0),
                   p.provolatile::text
            FROM pg_proc p
            WHERE p.proname = ANY(@nombres) AND p.pronamespace = 'public'::regnamespace
            """, propietario);
        orden.Parameters.AddWithValue("nombres", definidoras.Append("app_exigir_contexto_firmado").ToArray());

        var vistas = new HashSet<string>();
        await using var lector = await orden.ExecuteReaderAsync();
        while (await lector.ReadAsync())
        {
            var nombre = lector.GetString(0);
            vistas.Add(nombre);
            var esGuardia = nombre == "app_exigir_contexto_firmado";

            lector.GetBoolean(1).Should().Be(!esGuardia, $"{nombre}: la guardia corre con la identidad de la definidora que la llama");
            lector.GetString(2).Should().Be("search_path=pg_catalog, pg_temp", nombre);
            lector.GetBoolean(3).Should().Be(!esGuardia, $"{nombre}: cae_app_runtime");
            lector.GetBoolean(4).Should().BeFalse($"{nombre}: cae_app_soporte");
            lector.GetBoolean(5).Should().BeFalse($"{nombre}: cae_app_aprovisionamiento");
            lector.GetBoolean(6).Should().BeFalse($"{nombre}: REVOKE ALL ... FROM PUBLIC");
            lector.GetString(7).Should().Be("s", $"{nombre}: STABLE, solo lee");
        }

        vistas.Should().HaveCount(definidoras.Length + 1, "todas las funciones existen, con una sola sobrecarga");
    }

    private async Task<(Guid Id, string Nombre, string Correo)> SembrarCuentaAsync(Guid tenantId)
    {
        var id = Guid.NewGuid();
        var nombre = id.ToString("N").ToUpperInvariant();
        var correo = $"{nombre}@ORACULO.TEST";
        using var ambito = AmbitoTenantExplicito.Establecer(tenantId);
        _propietario.Users.Add(new ApplicationUser
        {
            Id = id,
            TenantId = tenantId,
            UserName = nombre,
            NormalizedUserName = nombre,
            Email = correo,
            NormalizedEmail = correo,
            NombreCompleto = "Usuario",
            EmailConfirmed = true,
            PasswordHash = "hash",
            SecurityStamp = Guid.NewGuid().ToString(),
            ConcurrencyStamp = Guid.NewGuid().ToString(),
        });
        await _propietario.SaveChangesAsync();
        return (id, nombre, correo);
    }

    private async Task<NpgsqlConnection> AbrirComoRuntimeAsync()
    {
        var conexion = new NpgsqlConnection(BaseDatosPostgresDePruebas.CadenaComoRuntime(_cadenaConexion));
        await conexion.OpenAsync();

        await using var identidad = new NpgsqlCommand("SELECT session_user::text", conexion);
        ((string)(await identidad.ExecuteScalarAsync())!).Should().Be("cae_app_runtime");
        return conexion;
    }

    private static async Task<bool> ConsultarAsync(NpgsqlConnection conexion, string sql)
    {
        await using var orden = new NpgsqlCommand(sql, conexion);
        return (bool)(await orden.ExecuteScalarAsync())!;
    }

    private static async Task<PostgresException> RechazoAsync(NpgsqlConnection conexion, string sql)
    {
        var consultar = () => ConsultarAsync(conexion, sql);
        return (await consultar.Should().ThrowAsync<PostgresException>()).Which;
    }

    private static async Task ExigeContextoFirmadoAsync(NpgsqlConnection conexion, string sql, string funcion)
    {
        var rechazo = await RechazoAsync(conexion, sql);
        rechazo.SqlState.Should().Be("42501", funcion);
        rechazo.MessageText.Should().Contain("contexto RLS firmado", funcion);
    }

    private sealed class TenantPorAmbito : ITenantActual
    {
        public Guid? TenantId => AmbitoTenantExplicito.TenantIdActual;
    }
}
