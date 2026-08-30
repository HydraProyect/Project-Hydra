using System.Collections.Concurrent;
using CaeManager.Application.Common;
using CaeManager.Application.Tenants;
using CaeManager.Domain.DocumentosIa;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.Coordinacion;
using CaeManager.Infrastructure.DocumentosIa;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CaeManager.IntegrationTests.DocumentosIa;

/// <summary>
/// La medición de "profundidad de cola de IA" (<c>ContarActivosAsync</c> +
/// <c>ObtenerSiguientePendienteAsync</c> por cada tenant activo) pagaba ~2N
/// consultas puramente de observación en CADA sondeo de 5 s — sin límite con
/// el número de tenants y sin que ninguna alerta (umbral de 30 min) necesitara
/// esa resolución. Este test comprueba, sin depender del reloj de pared (ver
/// el comentario dentro del test sobre PostgreSQL compartido), que el reclamo
/// de trabajo real (<c>ReclamarSiguientePendienteAsync</c>) sigue corriendo en
/// todos los sondeos mientras la medición de profundidad corre en menos —
/// antes del fix los dos iban siempre juntos, uno por tick.
/// </summary>
public class ProcesadorAnalisisDocumentoHostedServiceCadenciaMedicionTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto();
        await contexto.Database.MigrateAsync();
    }

    public async Task DisposeAsync() => await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    private CaeManagerDbContext CrearContexto()
    {
        var tenantActual = new TenantActualAmbiental { TenantId = null };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual))
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
    }

    [Fact]
    public async Task La_medicion_de_profundidad_no_se_repite_en_cada_sondeo_de_5_segundos()
    {
        Tenant tenant = new("Tenant único");

        await using (var contexto = CrearContexto())
        {
            contexto.Tenants.Add(tenant);
            await contexto.SaveChangesAsync();
        }

        var repositorio = new RepositorioFalsoContador();

        var servicios = new ServiceCollection();
        servicios.AddScoped(_ => CrearContexto());
        servicios.AddScoped<ITenantsQueryContext>(sp => sp.GetRequiredService<CaeManagerDbContext>());
        servicios.AddSingleton<ITrabajoAnalisisDocumentoRepository>(repositorio);
        servicios.AddSingleton<IUnitOfWork>(new UnitOfWorkFalso());
        await using var proveedor = servicios.BuildServiceProvider();

        var hostedService = new ProcesadorAnalisisDocumentoHostedService(
            proveedor.GetRequiredService<IServiceScopeFactory>(),
            new SiempreLiderFalso(),
            new AlertaOperativaFalsa(),
            NullLogger<ProcesadorAnalisisDocumentoHostedService>.Instance);

        using var cts = new CancellationTokenSource();
        await hostedService.StartAsync(cts.Token);
        try
        {
            // El primer tick corre de inmediato; a partir de ahí el
            // PeriodicTimer dispara cada 5 s (ver IntervaloSondeo) — así que
            // 6 reclamos exigen al menos 5 ticks completos del bucle de
            // procesamiento real. No se afirma una cuenta EXACTA de
            // mediciones ni un tiempo real concreto: esta máquina comparte
            // PostgreSQL con otras sesiones (ver
            // hydra-postgres-cluster-compartido-worktrees) y un tick puede
            // tardar mucho más de 5 s bajo contención, lo que haría fallar
            // en falso cualquier aserción que asuma cuántos ticks caben en
            // una ventana de reloj. Lo que sí es una propiedad del fix,
            // independiente de la velocidad real de los ticks: que la
            // medición corre en MENOS ticks que el reclamo — antes del fix
            // corrían siempre juntos, uno por tick, así que ambos contadores
            // habrían quedado iguales.
            var limite = DateTime.UtcNow.AddSeconds(50);
            while (repositorio.LlamadasReclamar < 6 && DateTime.UtcNow < limite)
                await Task.Delay(100);
        }
        finally
        {
            await hostedService.StopAsync(CancellationToken.None);
        }

        repositorio.LlamadasReclamar.Should().BeGreaterThanOrEqualTo(6,
            "el bucle de procesamiento real no cambia de cadencia con este fix");
        repositorio.LlamadasContarActivos.Should().BeLessThan(repositorio.LlamadasReclamar,
            "la medición de profundidad ya no corre en cada tick — antes de este fix los dos contadores habrían sido iguales");
        repositorio.LlamadasObtener.Should().Be(repositorio.LlamadasContarActivos,
            "misma cadencia que ContarActivosAsync: las dos llamadas de MedirProfundidadColaAsync van siempre juntas");
    }

    private sealed class RepositorioFalsoContador : ITrabajoAnalisisDocumentoRepository
    {
        private int _llamadasObtener;
        private int _llamadasReclamar;
        private int _llamadasContarActivos;

        public int LlamadasObtener => Volatile.Read(ref _llamadasObtener);
        public int LlamadasReclamar => Volatile.Read(ref _llamadasReclamar);
        public int LlamadasContarActivos => Volatile.Read(ref _llamadasContarActivos);

        public void Agregar(TrabajoAnalisisDocumento trabajo)
        {
            // No lo llama ningún camino ejercitado por este test.
        }

        public Task<TrabajoAnalisisDocumento?> ObtenerSiguientePendienteAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _llamadasObtener);
            return Task.FromResult<TrabajoAnalisisDocumento?>(null);
        }

        public Task<TrabajoAnalisisDocumento?> ReclamarSiguientePendienteAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _llamadasReclamar);
            return Task.FromResult<TrabajoAnalisisDocumento?>(null);
        }

        public Task<IReadOnlyList<TrabajoAnalisisDocumento>> ObtenerEstancadosAsync(
            TimeSpan umbral, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<TrabajoAnalisisDocumento>>([]);

        public Task<int> ContarActivosAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _llamadasContarActivos);
            return Task.FromResult(0);
        }
    }

    private sealed class UnitOfWorkFalso : IUnitOfWork
    {
        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) => Task.FromResult(0);
    }

    private sealed class SiempreLiderFalso : IEleccionLiderService
    {
        public async Task<bool> IntentarEjecutarComoLiderAsync(
            string clave, Func<CancellationToken, Task> trabajo, CancellationToken cancellationToken)
        {
            await trabajo(cancellationToken);
            return true;
        }
    }

    private sealed class AlertaOperativaFalsa : IAlertaOperativa
    {
        public void Emitir(string mensaje, NivelAlertaOperativa nivel)
        {
        }

        public void CapturarExcepcion(Exception excepcion)
        {
        }

        public void DejarMigaDePan(string mensaje)
        {
        }

        public IDisposable IniciarAmbitoDeCaptura() => NoOp.Instancia;

        private sealed class NoOp : IDisposable
        {
            public static readonly NoOp Instancia = new();
            public void Dispose()
            {
            }
        }
    }
}
