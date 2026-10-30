using System.Diagnostics;
using Microsoft.Extensions.Options;

namespace CaeManager.Infrastructure.Importacion;

/// <summary>
/// Reparte los huecos de análisis de importación (OWASP API4:2023, ASVS 4.0.3 V12.1.1): un tope global para el proceso
/// y otro por Tenant propietario, para que un solo Tenant no ocupe todos los huecos. No reparte el global a partes
/// iguales: varios Tenants distintos, cada uno dentro de su tope, pueden seguir llenándolo. El Tenant lo pasa quien
/// llama, leído de <c>ITenantActual</c> (resuelto en el servidor desde la sesión), nunca de un dato que mande el
/// navegador. Sin Tenant resuelto se rechaza: no hay cubo compartido para las peticiones sin Tenant.
/// <para>
/// Se espera primero al cupo del Tenant y después al hueco global, las dos esperas dentro del mismo
/// <c>EsperaSegundos</c>: quien espera a su Tenant no ocupa un hueco global. Si una espera no lo consigue, se cancela o
/// falla, lo reservado se devuelve; el permiso devuelto libera las dos reservas una sola vez, aunque se descarte varias
/// veces o a la vez desde dos hilos. El cupo de un Tenant se retira del diccionario cuando nadie lo usa ni lo espera.
/// </para>
/// </summary>
public sealed class LimitadorAnalisisImportacion : IDisposable
{
    private readonly SemaphoreSlim _semaforo;
    private readonly TimeSpan _espera;
    private readonly int _maximoPorTenant;
    private readonly Lock _cerrojo = new();
    private readonly Dictionary<Guid, CupoTenant> _porTenant = [];

    public LimitadorAnalisisImportacion(IOptions<OpcionesLimitesImportacion> opciones)
    {
        opciones.Value.Validar();
        _semaforo = new SemaphoreSlim(opciones.Value.AnalisisConcurrentes);
        _espera = TimeSpan.FromSeconds(opciones.Value.EsperaSegundos);
        _maximoPorTenant = opciones.Value.AnalisisConcurrentesPorTenant;
    }

    public async Task<IDisposable?> IntentarEntrarAsync(Guid? tenantPropietarioId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (tenantPropietarioId is not { } tenantId || tenantId == Guid.Empty) return null;

        CupoTenant cupo;
        lock (_cerrojo)
        {
            if (!_porTenant.TryGetValue(tenantId, out var existente))
            {
                existente = new CupoTenant(_maximoPorTenant);
                _porTenant[tenantId] = existente;
            }
            existente.Referencias++;
            cupo = existente;
        }

        var inicio = Stopwatch.GetTimestamp();
        var cupoAdquirido = false;
        var globalAdquirido = false;
        try
        {
            cupoAdquirido = await cupo.Semaforo.WaitAsync(_espera, cancellationToken);
            if (!cupoAdquirido) return null;
            var restante = _espera - Stopwatch.GetElapsedTime(inicio);
            globalAdquirido = await _semaforo.WaitAsync(restante > TimeSpan.Zero ? restante : TimeSpan.Zero, cancellationToken);
            return globalAdquirido ? new Permiso(this, tenantId, cupo) : null;
        }
        finally
        {
            if (!globalAdquirido)
            {
                try
                {
                    if (cupoAdquirido) cupo.Semaforo.Release();
                }
                finally
                {
                    SoltarCupo(tenantId, cupo);
                }
            }
        }
    }

    private void SoltarCupo(Guid tenantId, CupoTenant cupo)
    {
        lock (_cerrojo)
        {
            if (--cupo.Referencias > 0) return;
            _porTenant.Remove(tenantId);
            cupo.Semaforo.Dispose();
        }
    }

    public void Dispose() => _semaforo.Dispose();

    /// <summary>
    /// Cupo de un Tenant. <see cref="Referencias"/> cuenta quien lo tiene y quien lo espera, siempre bajo el cerrojo,
    /// así que el semáforo no se descarta mientras alguien pueda usarlo.
    /// </summary>
    private sealed class CupoTenant(int maximo)
    {
        public SemaphoreSlim Semaforo { get; } = new(maximo);
        public int Referencias { get; set; }
    }

    private sealed class Permiso(LimitadorAnalisisImportacion limitador, Guid tenantId, CupoTenant cupo) : IDisposable
    {
        private LimitadorAnalisisImportacion? _limitador = limitador;
        public void Dispose()
        {
            var actual = Interlocked.Exchange(ref _limitador, null);
            if (actual is null) return;
            try
            {
                actual._semaforo.Release();
            }
            finally
            {
                try
                {
                    cupo.Semaforo.Release();
                }
                finally
                {
                    actual.SoltarCupo(tenantId, cupo);
                }
            }
        }
    }
}
