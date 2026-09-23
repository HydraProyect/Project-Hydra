namespace CaeManager.Application.Operaciones.IncorporacionCartera;

/// <summary>
/// Ruta de la bandeja de solicitudes de incorporación a cartera. Vive en
/// Application porque la usan las notificaciones que generan los Commands; la
/// página de Web la referencia en su <c>@page</c> para que no se desalineen.
/// </summary>
public static class RutasIncorporacionCartera
{
    public const string Bandeja = "/cartera/solicitudes";
}
