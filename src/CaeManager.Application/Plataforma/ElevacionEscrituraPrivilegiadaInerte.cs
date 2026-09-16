namespace CaeManager.Application.Plataforma;

/// <summary>
/// Valor por defecto de <see cref="IElevacionEscrituraPrivilegiada"/>: no
/// toca ninguna conexión — correcto para un host sin Infrastructure real
/// (fixtures mínimos de <c>CaeManager.IntegrationTests</c> que solo montan
/// <c>AddApplication()</c>, mismo motivo que <see cref="SesionPrivilegiadaAusente"/>).
/// El <see cref="AmbitoEscrituraPrivilegiada"/> lo abre y cierra el propio
/// <c>ElevacionEscrituraAprovisionamientoBehavior</c>, no esta pieza — ver el
/// porqué en <see cref="IElevacionEscrituraPrivilegiada"/>. La implementación
/// real, que mueve el rol de PostgreSQL, la registra Infrastructure y la
/// sustituye en la aplicación de verdad.
/// </summary>
public sealed class ElevacionEscrituraPrivilegiadaInerte : IElevacionEscrituraPrivilegiada
{
    public Task ElevarSiConexionAbiertaAsync(CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task DevolverSiConexionAbiertaAsync(CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}
