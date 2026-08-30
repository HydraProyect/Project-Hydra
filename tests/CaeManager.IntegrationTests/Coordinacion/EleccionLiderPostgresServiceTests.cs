using CaeManager.Infrastructure.Coordinacion;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace CaeManager.IntegrationTests.Coordinacion;

/// <summary>
/// P3-30 de docs/business/MATURITY_REVIEW.md: la elección de líder es lo que
/// evita que dos réplicas de <c>ProcesadorAnalisisDocumentoHostedService</c>
/// hagan el mismo trabajo a la vez. Contra PostgreSQL real y no una base de datos de prueba con
/// esquema propio — <c>pg_try_advisory_lock</c> no necesita ninguna tabla, el
/// espacio de claves es del clúster, no de una base de datos concreta.
/// </summary>
public class EleccionLiderPostgresServiceTests
{
    private static readonly string CadenaConexion =
        $"{Environment.GetEnvironmentVariable("CAEMANAGER_TESTS_PG") ?? "Host=localhost;Username=postgres;Password=postgres"};Database=postgres";

    private static EleccionLiderPostgresService CrearServicio() =>
        new(new ConfigurationBuilder()
            .AddInMemoryCollection([new("ConnectionStrings:CaeManagerDb", CadenaConexion)])
            .Build());

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
}
