namespace CaeManager.Web.Components.Workspace;

/// <summary>
/// Un nivel de la pila del Context Workspace — la pila completa ES el
/// breadcrumb (ver ContextWorkspaceService). TituloVisible se cachea aquí
/// para poder pintar el breadcrumb sin volver a pedir la entidad al
/// navegar entre niveles ya visitados.
/// </summary>
public record WorkspaceFrame(EntidadWorkspace Tipo, Guid EntidadId, string TituloVisible, string PestanaActiva);
