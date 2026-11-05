namespace CaeManager.Application.Plataforma;

/// <summary>
/// Valor por defecto de <see cref="ICoordenadaSesionPrivilegiada"/> para hosts sin circuito ni
/// cookie: nunca hay una Sesión Privilegiada seleccionada. Mismo motivo que
/// <see cref="SesionPrivilegiadaAusente"/>: <c>RegistroLecturaSesionPrivilegiadaBehavior</c> la
/// necesita y MediatR construye todos los behaviors para cualquier request, también en los
/// fixtures mínimos que solo llaman a <c>AddApplication()</c>. La implementación real de
/// Infrastructure la sustituye.
///
/// No debilita nada: un host sin la implementación real tampoco tiene la que adopta
/// <c>cae_app_soporte</c> en la conexión, así que no hay lectura bajo identidad de soporte que
/// registrar.
/// </summary>
public sealed class CoordenadaSesionPrivilegiadaAusente : ICoordenadaSesionPrivilegiada
{
    public bool HaySeleccionada => false;
}
