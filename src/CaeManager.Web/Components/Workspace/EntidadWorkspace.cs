namespace CaeManager.Web.Components.Workspace;

/// <summary>
/// Entidades de primer nivel que el Context Workspace sabe mostrar. El
/// nombre de cada valor coincide a propósito con el nombre simple de la
/// clase de Domain correspondiente (Cliente, Empresa, Subcontrata, Centro,
/// Trabajador, Vehiculo, Documento) — es el mismo texto que
/// AuditoriaInterceptor guarda en RegistroAuditoria.EntidadTipo, así que
/// <see cref="EntidadWorkspace"/> se puede usar directamente para filtrar
/// la pestaña "Historial"/"Actividad" sin una tabla de mapeo aparte.
/// </summary>
public enum EntidadWorkspace
{
    Cliente,
    Empresa,
    Subcontrata,
    Centro,
    Trabajador,
    Vehiculo,
    Documento
}
