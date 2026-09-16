namespace CaeManager.Application.Plataforma;

/// <summary>
/// Valor por defecto de <see cref="IElevacionEscrituraPrivilegiada"/>: abre y
/// cierra el <see cref="AmbitoEscrituraPrivilegiada"/> sin tocar ninguna
/// conexión — correcto para un host sin Infrastructure real (fixtures mínimos
/// de <c>CaeManager.IntegrationTests</c> que solo montan <c>AddApplication()</c>,
/// mismo motivo que <see cref="SesionPrivilegiadaAusente"/>). La implementación
/// real, que además mueve el rol de PostgreSQL, la registra Infrastructure y
/// la sustituye en la aplicación de verdad.
/// </summary>
public sealed class ElevacionEscrituraPrivilegiadaInerte : IElevacionEscrituraPrivilegiada
{
    public Task<IAsyncDisposable> EstablecerAsync(
        Guid sesionId, Guid tenantObjetivoId, CancellationToken cancellationToken = default)
    {
        var restaurador = AmbitoEscrituraPrivilegiada.Establecer(sesionId, tenantObjetivoId);
        return Task.FromResult<IAsyncDisposable>(new Cierre(restaurador));
    }

    private sealed class Cierre(IDisposable restaurador) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            restaurador.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
