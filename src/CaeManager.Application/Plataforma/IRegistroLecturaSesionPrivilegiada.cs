namespace CaeManager.Application.Plataforma;

/// <summary>
/// Registra en <c>RegistrosActividadSoporte</c> una lectura hecha bajo una Sesión Privilegiada
/// (issue #2001). La conexión de esa sesión lleva <c>cae_app_soporte</c>, sin <c>INSERT</c> sobre
/// la tabla: la implementación real (Infrastructure) pasa por la función SECURITY DEFINER
/// <c>app_registrar_actividad_soporte_por_sesion</c>, que pone ella misma el Actor real, el Tenant
/// objetivo y la sesión desde el contexto RLS firmado, y antes comprueba que la sesión está
/// abierta, vigente, sobre ese Tenant y amparada por una concesión de ese actor.
/// </summary>
public interface IRegistroLecturaSesionPrivilegiada
{
    /// <summary>
    /// Devuelve <c>true</c> solo si la fila quedó escrita. Con <c>false</c>, quien llama no sirve
    /// los datos (fallo cerrado). <paramref name="operacion"/> nombra lo que se leyó (el request),
    /// nunca los datos leídos.
    /// </summary>
    Task<bool> RegistrarAsync(Guid sesionPrivilegiadaId, string operacion, CancellationToken cancellationToken = default);
}
