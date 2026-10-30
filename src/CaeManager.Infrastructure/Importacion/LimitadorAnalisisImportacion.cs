using Microsoft.Extensions.Options;

namespace CaeManager.Infrastructure.Importacion;

/// <summary>
/// Reparte los huecos de análisis de importación (OWASP API4:2023, ASVS 4.0.3 V12.1.1): un tope global para el proceso
/// y otro por Tenant propietario, para que un Tenant no agote los huecos de los demás. El Tenant lo pasa quien llama,
/// leído de <c>ITenantActual</c> (resuelto en el servidor desde la sesión), nunca de un dato que mande el navegador.
/// Sin Tenant resuelto se rechaza: no hay cubo compartido para las peticiones sin Tenant. La reserva por Tenant se toma
/// antes de esperar al hueco global y se devuelve si la espera no lo consigue, se cancela o falla; el permiso devuelto
/// libera las dos reservas una sola vez, aunque se descarte varias veces.
/// </summary>
public sealed class LimitadorAnalisisImportacion : IDisposable
{
    private readonly SemaphoreSlim _semaforo;
    private readonly TimeSpan _espera;
    private readonly int _maximoPorTenant;
    private readonly Lock _cerrojo = new();
    private readonly Dictionary<Guid, int> _porTenant = [];

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

        lock (_cerrojo)
        {
            _porTenant.TryGetValue(tenantId, out var ocupadas);
            if (ocupadas >= _maximoPorTenant) return null;
            _porTenant[tenantId] = ocupadas + 1;
        }

        var adquirida = false;
        try
        {
            adquirida = await _semaforo.WaitAsync(_espera, cancellationToken);
            return adquirida ? new Permiso(this, tenantId) : null;
        }
        finally
        {
            if (!adquirida) LiberarTenant(tenantId);
        }
    }

    private void LiberarTenant(Guid tenantId)
    {
        lock (_cerrojo)
        {
            if (_porTenant[tenantId] == 1) _porTenant.Remove(tenantId);
            else _porTenant[tenantId]--;
        }
    }

    public void Dispose() => _semaforo.Dispose();

    private sealed class Permiso(LimitadorAnalisisImportacion limitador, Guid tenantId) : IDisposable
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
                actual.LiberarTenant(tenantId);
            }
        }
    }
}
