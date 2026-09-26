using CaeManager.Infrastructure.Coordinacion;
using CaeManager.IntegrationTests.Arranque;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Xunit;

namespace CaeManager.IntegrationTests.Coordinacion;

/// <summary>
/// P3-30 de Project-Hydra-Negocio/MATURITY_REVIEW.md: la elección de líder es lo que
/// evita que dos réplicas de <c>ProcesadorAnalisisDocumentoHostedService</c>
/// hagan el mismo trabajo a la vez. Contra PostgreSQL real y no una base de datos de prueba con
/// esquema propio — <c>pg_try_advisory_lock</c> no necesita ninguna tabla, el
/// espacio de claves es del clúster, no de una base de datos concreta.
/// </summary>
public class EleccionLiderPostgresServiceTests
{
    private static readonly string CadenaConexion =
        $"{Environment.GetEnvironmentVariable("CAEMANAGER_TESTS_PG") ?? "Host=localhost;Username=postgres;Password=postgres"};Database=postgres";

    // Development sin CaeManagerDbRuntime: ResolverCadenaDeTrafico cae al rol
    // propietario, que es lo que estos tests de exclusión mutua necesitan (el
    // espacio de claves es del clúster, la identidad no cambia la semántica).
    private static EleccionLiderPostgresService CrearServicio() =>
        new(new ConfigurationBuilder()
            .AddInMemoryCollection([new("ConnectionStrings:CaeManagerDb", CadenaConexion)])
            .Build(), EntornoDePrueba.Desarrollo);

    [Fact]
    public async Task Solo_una_replica_gana_el_liderazgo_para_la_misma_clave()
    {
        var clave = $"test-{Guid.NewGuid()}";
        var servicio = CrearServicio();

        var liderTomoElLock = new TaskCompletionSource();
        var liberarLock = new TaskCompletionSource();

        var tareaLider = servicio.IntentarEjecutarComoLiderAsync(clave, async _ =>
        {
            liderTomoElLock.SetResult();
            await liberarLock.Task;
        }, CancellationToken.None);

        await liderTomoElLock.Task;

        // Con el líder todavía dentro de "trabajo", una segunda réplica que
        // intenta la misma clave no espera — vuelve false de inmediato.
        var gananciaSegundaReplica = await servicio.IntentarEjecutarComoLiderAsync(
            clave, _ => Task.CompletedTask, CancellationToken.None);

        liberarLock.SetResult();
        var gananciaPrimeraReplica = await tareaLider;

        gananciaSegundaReplica.Should().BeFalse();
        gananciaPrimeraReplica.Should().BeTrue();
    }

    [Fact]
    public async Task Tras_liberar_el_lock_otra_replica_puede_ganar_la_misma_clave()
    {
        var clave = $"test-{Guid.NewGuid()}";
        var servicio = CrearServicio();

        var primeraGanancia = await servicio.IntentarEjecutarComoLiderAsync(clave, _ => Task.CompletedTask, CancellationToken.None);
        var segundaGanancia = await servicio.IntentarEjecutarComoLiderAsync(clave, _ => Task.CompletedTask, CancellationToken.None);

        primeraGanancia.Should().BeTrue();
        segundaGanancia.Should().BeTrue();
    }

    [Fact]
    public async Task Claves_distintas_no_se_bloquean_entre_si()
    {
        var claveA = $"test-a-{Guid.NewGuid()}";
        var claveB = $"test-b-{Guid.NewGuid()}";
        var servicio = CrearServicio();

        var liderATomoElLock = new TaskCompletionSource();
        var liberarLockA = new TaskCompletionSource();

        var tareaLiderA = servicio.IntentarEjecutarComoLiderAsync(claveA, async _ =>
        {
            liderATomoElLock.SetResult();
            await liberarLockA.Task;
        }, CancellationToken.None);

        await liderATomoElLock.Task;

        var gananciaB = await servicio.IntentarEjecutarComoLiderAsync(claveB, _ => Task.CompletedTask, CancellationToken.None);

        liberarLockA.SetResult();
        await tareaLiderA;

        gananciaB.Should().BeTrue("una clave distinta no comparte el mismo advisory lock");
    }

    /// <summary>
    /// La configuración que reciben staging y producción: <c>CaeManagerDb</c>
    /// vacía a propósito (P0-2, <c>deploy/local/docker-compose.*.yml</c>) y solo
    /// <c>CaeManagerDbRuntime</c>. El lock tiene que tomarse con el rol
    /// restringido y el trabajo ejecutarse; antes, la cadena vacía llegaba a
    /// <c>OpenAsync</c> y todos los servicios con líder fallaban en cada ciclo
    /// sin hacer nada (hallazgo de Codex en P1-M2).
    /// </summary>
    [Fact]
    public async Task Con_la_configuracion_de_despliegue_el_lock_se_toma_como_runtime_y_el_trabajo_corre()
    {
        await using var arnes = await ArnesDeArranqueRuntime.CrearAsync(datosDePruebaActivos: false);
        var configuracion = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:CaeManagerDb"] = "",
                ["ConnectionStrings:CaeManagerDbRuntime"] = BaseDatosPostgresDePruebas.CadenaComoRuntime(arnes.CadenaPropietario),
            })
            .Build();
        var servicio = new EleccionLiderPostgresService(configuracion, new EntornoDePrueba("Production"));
        var clave = $"test-runtime-{Guid.NewGuid()}";
        string? titularDelLock = null;

        var gano = await servicio.IntentarEjecutarComoLiderAsync(clave, async ct =>
        {
            // Desde otra conexión, la del propietario: quién tiene de verdad el
            // advisory lock de esta clave mientras el trabajo corre.
            await using var propietario = new NpgsqlConnection(arnes.CadenaPropietario);
            await propietario.OpenAsync(ct);
            await using var consulta = new NpgsqlCommand(
                """
                SELECT a.usename FROM pg_locks l JOIN pg_stat_activity a ON a.pid = l.pid
                WHERE l.locktype = 'advisory' AND l.granted
                  AND ((l.classid::bigint << 32) | l.objid::bigint) = hashtextextended(@clave, 0)
                """, propietario);
            consulta.Parameters.AddWithValue("clave", clave);
            titularDelLock = (string?)await consulta.ExecuteScalarAsync(ct);
        }, CancellationToken.None);

        gano.Should().BeTrue("con solo la cadena de runtime el servicio tiene que poder liderar");
        titularDelLock.Should().Be("cae_app_runtime",
            "el lock se toma con la identidad del tráfico, no con el rol propietario que el contenedor app ya no tiene");
    }

    /// <summary>
    /// Fallo cerrado al construir, no en cada ciclo: el singleton lo resuelven
    /// los hosted services al arrancar el host, así que una configuración sin
    /// identidad utilizable tumba el arranque con un mensaje claro en vez de
    /// dejar un error en el log por cada vuelta de cada servicio.
    /// </summary>
    [Theory]
    [InlineData("Production", "")]
    [InlineData("Production", null)]
    [InlineData("Development", "")]
    [InlineData("Development", "   ")]
    public void Sin_identidad_utilizable_falla_al_construir_y_no_al_intentar_liderar(string entorno, string? cadenaRuntime)
    {
        var configuracion = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:CaeManagerDb"] = "",
                ["ConnectionStrings:CaeManagerDbRuntime"] = cadenaRuntime,
            })
            .Build();

        var construir = () => new EleccionLiderPostgresService(configuracion, new EntornoDePrueba(entorno));

        construir.Should().Throw<InvalidOperationException>()
            .WithMessage("*CaeManagerDbRuntime*", "el mensaje dice qué variable falta, no un error de Npgsql");
    }
}
