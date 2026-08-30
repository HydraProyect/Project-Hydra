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
/// Un tenant a la vez, aislado: sin el try/catch por tenant que este test
/// falsa, una excepción en el tenant k (en <c>MedirProfundidadColaAsync</c> o
/// en <c>ProcesarPendientesDelTenantAsync</c>) se propagaba hasta
/// <c>ExecuteAsync</c> y abortaba el tick entero, dejando sin procesar a
/// k+1..N — y como el orden de tenants es estable, bloqueaba a los mismos
/// siguientes en todos los sondeos posteriores (cada 5 s), con un LogError a
/// stdout como única señal.
///
/// Contra Postgres real solo para <see cref="ITenantsQueryContext"/> —
/// <c>CaeManagerDbContext</c> es su única implementación (ver
/// InfrastructureServiceCollectionExtensions) y el fallo de la propia
/// consulta de tenants no es lo que se falsa aquí. <see
/// cref="ITrabajoAnalisisDocumentoRepository"/> y <see cref="IUnitOfWork"/>
/// van con fakes: son los dos puntos donde hay que inyectar el fallo de un
/// tenant concreto de forma determinista, sin depender de qué fila de
/// Postgres falle.
/// </summary>
public class ProcesadorAnalisisDocumentoHostedServiceAislamientoTests : IAsyncLifetime
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
    public async Task Un_tenant_que_falla_no_impide_sondear_ni_procesar_al_resto()
    {
        Tenant tenantQueFalla = new("Tenant que falla");
        Tenant tenantSano = new("Tenant sano");

        await using (var contexto = CrearContexto())
        {
            contexto.Tenants.AddRange(tenantQueFalla, tenantSano);
            await contexto.SaveChangesAsync();
        }

        var repositorio = new RepositorioFalsoConFalloPorTenant(tenantQueFalla.Id);

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
            // El primer tick de ExecuteAsync corre de inmediato (el do-while
            // ejecuta el cuerpo antes de esperar al PeriodicTimer de 5 s), así
            // que no hace falta esperar ningún intervalo real. Se sondea hasta
            // que el tenant sano acumule una llamada a
            // ObtenerSiguientePendienteAsync (MedirProfundidadColaAsync, que
            // corre antes de procesar, sigue usando la lectura de solo
            // observación) y una a ReclamarSiguientePendienteAsync (el reclamo
            // atómico del bucle de ProcesarPendientesDelTenantAsync) — la
            // prueba de que AMBOS bucles lo alcanzaron en el mismo tick que el
            // tenant que falla.
            var limite = DateTime.UtcNow.AddSeconds(10);
            while ((repositorio.LlamadasObtener(tenantSano.Id) < 1 || repositorio.LlamadasReclamar(tenantSano.Id) < 1)
                   && DateTime.UtcNow < limite)
                await Task.Delay(50);
        }
        finally
        {
            await hostedService.StopAsync(CancellationToken.None);
        }

        // tenantQueFalla: ContarActivosAsync revienta dentro de
        // MedirProfundidadColaAsync, así que esa misma iteración nunca llega a
        // llamar ObtenerSiguientePendienteAsync ahí (se corta antes) — cero
        // llamadas a Obtener, y una a Reclamar (la de
        // ProcesarPendientesDelTenantAsync, que también revienta y que antes
        // del fix se propagaba fuera de SondearTodosLosTenantsAsync).
        repositorio.LlamadasContarActivos(tenantQueFalla.Id).Should().Be(1,
            "MedirProfundidadColaAsync debe intentar este tenant aunque falle");
        repositorio.LlamadasObtener(tenantQueFalla.Id).Should().Be(0,
            "ContarActivosAsync revienta antes de llegar a la lectura de observación");
        repositorio.LlamadasReclamar(tenantQueFalla.Id).Should().Be(1,
            "ProcesarPendientesDelTenantAsync debe intentarse para este tenant en el mismo tick");

        // tenantSano: Medir y Procesar tienen éxito ambos, sin importar si
        // tenantQueFalla se procesó antes o después en el listado — esa es la
        // propiedad que el fix garantiza.
        repositorio.LlamadasContarActivos(tenantSano.Id).Should().Be(1);
        repositorio.LlamadasObtener(tenantSano.Id).Should().Be(1);
        repositorio.LlamadasReclamar(tenantSano.Id).Should().Be(1);
    }

    private sealed class RepositorioFalsoConFalloPorTenant(Guid tenantQueFalla) : ITrabajoAnalisisDocumentoRepository
    {
        private readonly ConcurrentDictionary<Guid, int> _llamadasObtener = new();
        private readonly ConcurrentDictionary<Guid, int> _llamadasReclamar = new();
        private readonly ConcurrentDictionary<Guid, int> _llamadasContarActivos = new();

        public int LlamadasObtener(Guid tenantId) => _llamadasObtener.GetValueOrDefault(tenantId);

        public int LlamadasReclamar(Guid tenantId) => _llamadasReclamar.GetValueOrDefault(tenantId);

        public int LlamadasContarActivos(Guid tenantId) => _llamadasContarActivos.GetValueOrDefault(tenantId);

        private Guid TenantActual => AmbitoTenantExplicito.TenantIdActual
            ?? throw new InvalidOperationException("El servicio bajo prueba debe llamar con un ámbito de tenant explícito establecido.");

        public void Agregar(TrabajoAnalisisDocumento trabajo)
        {
            // No lo llama ningún camino ejercitado por este test.
        }

        public Task<TrabajoAnalisisDocumento?> ObtenerSiguientePendienteAsync(CancellationToken cancellationToken = default)
        {
            var tenantId = TenantActual;
            _llamadasObtener.AddOrUpdate(tenantId, 1, (_, n) => n + 1);

            if (tenantId == tenantQueFalla)
                throw new InvalidOperationException("Fallo simulado del tenant bajo prueba.");

            return Task.FromResult<TrabajoAnalisisDocumento?>(null);
        }

        public Task<TrabajoAnalisisDocumento?> ReclamarSiguientePendienteAsync(CancellationToken cancellationToken = default)
        {
            var tenantId = TenantActual;
            _llamadasReclamar.AddOrUpdate(tenantId, 1, (_, n) => n + 1);

            if (tenantId == tenantQueFalla)
                throw new InvalidOperationException("Fallo simulado del tenant bajo prueba.");

            return Task.FromResult<TrabajoAnalisisDocumento?>(null);
        }

        public Task<IReadOnlyList<TrabajoAnalisisDocumento>> ObtenerEstancadosAsync(
            TimeSpan umbral, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<TrabajoAnalisisDocumento>>([]);

        public Task<int> ContarActivosAsync(CancellationToken cancellationToken = default)
        {
            var tenantId = TenantActual;
            _llamadasContarActivos.AddOrUpdate(tenantId, 1, (_, n) => n + 1);

            if (tenantId == tenantQueFalla)
                throw new InvalidOperationException("Fallo simulado del tenant bajo prueba.");

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
