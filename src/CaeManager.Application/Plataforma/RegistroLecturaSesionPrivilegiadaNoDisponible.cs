namespace CaeManager.Application.Plataforma;

/// <summary>
/// Valor por defecto de <see cref="IRegistroLecturaSesionPrivilegiada"/> para un host sin
/// Infrastructure real (fixtures mínimos de <c>CaeManager.IntegrationTests</c> que solo montan
/// <c>AddApplication()</c>; MediatR construye todos los behaviors para cualquier request, mismo
/// motivo que <see cref="SesionPrivilegiadaAusente"/>). Dice siempre «no registrado»: con el
/// <see cref="SesionPrivilegiadaAusente"/> por defecto nunca se le pregunta, y un host que resuelva
/// una Sesión Privilegiada de verdad sin registrar la implementación real no sirve la lectura
/// (fallo cerrado), en vez de servirla sin rastro. Infrastructure la sustituye.
/// </summary>
public sealed class RegistroLecturaSesionPrivilegiadaNoDisponible : IRegistroLecturaSesionPrivilegiada
{
    public Task<bool> RegistrarAsync(Guid sesionPrivilegiadaId, string operacion, CancellationToken cancellationToken = default) =>
        Task.FromResult(false);
}
