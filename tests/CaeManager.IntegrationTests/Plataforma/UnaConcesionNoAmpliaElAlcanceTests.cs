using CaeManager.Application.Common;
using CaeManager.Application.Plataforma;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Operaciones;
using CaeManager.Domain.Plataforma;
using CaeManager.Infrastructure.Autorizacion;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace CaeManager.IntegrationTests.Plataforma;

/// <summary>
/// La pregunta que el resto de la batería del plano 3 no responde: ¿la RLS
/// protege <b>exactamente</b> el conjunto que el contrato define, o la mera
/// existencia de una concesión se ha convertido en una capacidad más amplia?
///
/// Los tests de <c>RlsPlanoPrivilegioTests</c> demuestran que el mecanismo
/// funciona <i>dentro</i> del plano 3: se ven las filas que te nombran. Lo que
/// no demuestran es lo de fuera — que tener una concesión vigente no mueva ni un
/// milímetro lo que el usuario ve en los datos tenantizados ni en los catálogos
/// de asignación.
///
/// Hoy eso es cierto por construcción: la concesión no aparece en ninguna de
/// esas políticas. Pero "cierto por construcción" es precisamente la clase de
/// afirmación que este ciclo ha ido convirtiendo en test, porque es la que deja
/// de ser cierta sin que nadie lo note. La forma en que dejaría de serlo tiene
/// nombre y es concreta: alguien añade un <c>OR EXISTS (concesión del usuario)</c>
/// a una política tenantizada "para que soporte pueda ver", y convierte la
/// concesión —que solo debería permitir <b>abrir</b> una sesión— en una llave
/// permanente que no necesita sesión ninguna.
///
/// Los tres estados de ADR-011 § 8.1 tienen que seguir separados:
/// <code>
/// concesión existe  ≠  concesión válida ahora  ≠  sesión activa
/// </code>
/// Aquí se ataca el primer eslabón: existir no basta, y no basta <b>en ninguna
/// parte</b>.
/// </summary>
public class UnaConcesionNoAmpliaElAlcanceTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenantVisitado = Guid.NewGuid();
    private readonly Guid _tenantDelSoporte = Guid.NewGuid();
    private readonly Guid _usuarioSoporte = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto(_tenantVisitado);
        await contexto.Database.MigrateAsync();

        contexto.Empresas.Add(Empresa.CrearComoCliente(
            "Cliente del tenant visitado", "B12345674", esCritico: false, notas: null, ejecutivoUsuarioId: null));

        // Una asignación del tenant visitado, para el catálogo global.
        contexto.AsignacionesOperacion.Add(AsignacionOperacion.Raiz(
            _tenantVisitado, ServicioCae.Outbound, DateTime.UtcNow.AddDays(-1), DateTime.UtcNow));

        // Y la concesión: vigente, sobre el tenant visitado, a nombre del
        // usuario de soporte. Lo que NO hay es sesión abierta.
        var ahora = DateTime.UtcNow;
        contexto.ConcesionesPrivilegio.Add(ConcesionPrivilegio.SobreTenants(
            _usuarioSoporte, CapacidadPrivilegio.SoporteLectura, [_tenantVisitado],
            vigenciaDesde: ahora.AddMinutes(-10), vigenciaHasta: ahora.AddHours(4)));

        await contexto.SaveChangesAsync();
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Fact]
    public async Task Con_concesion_vigente_pero_sin_sesion_los_datos_tenantizados_siguen_cerrados()
    {
        // El usuario de soporte tiene una concesión viva sobre este tenant. Sin
        // sesión abierta, su contexto sigue siendo el suyo — y desde ahí, los
        // datos del tenant visitado no existen.
        await using var conexion = await AbrirRestringidaAsync(
            tenantActivo: _tenantDelSoporte, tenantOrigen: _tenantDelSoporte, usuario: _usuarioSoporte);

        (await ContarAsync(conexion, "Empresas")).Should().Be(0,
            "la concesión permite ABRIR una sesión sobre ese tenant, no leer sus datos por el hecho de existir");
    }

    [Fact]
    public async Task Con_concesion_vigente_pero_sin_sesion_los_catalogos_de_asignacion_siguen_cerrados()
    {
        await using var conexion = await AbrirRestringidaAsync(
            tenantActivo: _tenantDelSoporte, tenantOrigen: _tenantDelSoporte, usuario: _usuarioSoporte);

        (await ContarAsync(conexion, "AsignacionesOperacion")).Should().Be(0,
            "la política de los catálogos mira propietario y operador; la concesión no es ninguna de las dos cosas");
    }

    [Fact]
    public async Task Lo_unico_que_la_concesion_le_concede_es_verse_a_si_misma()
    {
        // El control positivo que delimita el conjunto por el otro lado: no es
        // que la concesión no sirva de nada, es que sirve exactamente para una
        // cosa.
        await using var conexion = await AbrirRestringidaAsync(
            tenantActivo: _tenantDelSoporte, tenantOrigen: _tenantDelSoporte, usuario: _usuarioSoporte);

        (await ContarAsync(conexion, "ConcesionesPrivilegio")).Should().Be(1);
    }

    [Fact]
    public async Task El_alcance_de_datos_de_la_aplicacion_tampoco_se_mueve()
    {
        // La misma pregunta una capa más arriba: AlcanceDatosService resuelve el
        // alcance funcional, y tampoco puede reaccionar a la concesión. Se le
        // pasa un resolutor que dice "no hay sesión privilegiada", que es la
        // verdad: hay concesión, no sesión.
        await using var contexto = CrearContexto(_tenantVisitado);

        var alcance = new AlcanceDatosService(
            contexto,
            new CurrentUserServiceFalso(_usuarioSoporte, rol: null, tenantOrigenId: _tenantDelSoporte),
            new TenantActualAmbiental { TenantId = _tenantVisitado },
            new SesionPrivilegiadaAusente());

        (await alcance.TieneAccesoTotalAsync()).Should().BeFalse(
            "sin sesión privilegiada no hay acceso total, por mucha concesión que exista");

        (await alcance.ObtenerClienteIdsVisiblesAsync()).Should().BeEmpty(
            "y el reparto por cliente sale del rol, que aquí no existe");
    }

    private async Task<NpgsqlConnection> AbrirRestringidaAsync(Guid tenantActivo, Guid tenantOrigen, Guid usuario)
    {
        var conexion = new NpgsqlConnection(_cadenaConexion);
        await conexion.OpenAsync();

        await FijarAsync(conexion, "app.tenant_id", tenantActivo);
        await FijarAsync(conexion, "app.tenant_origen_id", tenantOrigen);
        await FijarAsync(conexion, "app.usuario_id", usuario);

        await using var setRol = conexion.CreateCommand();
        setRol.CommandText = "SET ROLE cae_app_runtime;";
        await setRol.ExecuteNonQueryAsync();

        return conexion;
    }

    private static async Task FijarAsync(NpgsqlConnection conexion, string variable, Guid valor)
    {
        await using var comando = conexion.CreateCommand();
        comando.CommandText = "SELECT set_config(@variable, @valor, false);";
        comando.Parameters.AddWithValue("variable", variable);
        comando.Parameters.AddWithValue("valor", valor.ToString());
        await comando.ExecuteNonQueryAsync();
    }

    private static async Task<long> ContarAsync(NpgsqlConnection conexion, string tabla)
    {
        await using var consulta = conexion.CreateCommand();
        consulta.CommandText = $"SELECT count(*) FROM \"{tabla}\";";
        return (long)(await consulta.ExecuteScalarAsync())!;
    }

    private CaeManagerDbContext CrearContexto(Guid tenantId)
    {
        var tenantActual = new TenantActualAmbiental { TenantId = tenantId };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual))
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
    }
}
