namespace CaeManager.Web.Services;

/// <summary>
/// Lo que la interfaz necesita saber de la ventana de soporte abierta sobre el
/// Workspace operativo derivado seleccionado. Solo informa: quien cierra la
/// ventana es la revalidación (<see cref="RevalidacionClienteActivoMiddleware"/>),
/// que no consulta esto.
/// </summary>
public interface IVentanaDeSoporteActual
{
    /// <summary>
    /// Hasta cuándo dura la ventana, o <c>null</c> si no hay sesión de soporte
    /// (o la delegación no caduca).
    /// </summary>
    Task<DateTime?> ObtenerExpiracionAsync(CancellationToken cancellationToken = default);
}
