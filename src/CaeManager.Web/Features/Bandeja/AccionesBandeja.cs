using CaeManager.Application.Bandeja.Queries.ObtenerBandejaGestor;
using CaeManager.Web.Components.Workspace;
using Microsoft.AspNetCore.Components;

namespace CaeManager.Web.Features.Bandeja;

/// <summary>
/// La acción primaria de cada tarjeta de la Bandeja del gestor — compartida
/// entre <c>/bandeja</c> y el panel montado en <c>/alertas</c>, para que
/// ambos lugares abran exactamente el mismo sitio ante el mismo ítem.
/// </summary>
public static class AccionesBandeja
{
    public static Task AbrirAsync(ItemBandejaDto item, NavigationManager navigationManager, ContextWorkspaceService workspaceService) =>
        item.Tipo == TipoItemBandeja.RequisitoPendiente
            ? AbrirRequisitoAsync(item, workspaceService)
            : Navegar(navigationManager, ResolverUrl(item));

    /// <summary>
    /// La misma URL a la que <see cref="AbrirAsync"/> navegaría para este
    /// ítem, sin navegar — la usa Mi trabajo Gen2 (multi-Tenant) para
    /// componer el <c>returnUrl</c> exacto del POST cross-Tenant a
    /// <c>/cuenta/cliente-activo</c> (contrato
    /// CONTRATO-MI-TRABAJO-GEN2-MULTI-TENANT-2026-09-22.md § 8: "pantalla
    /// exacta", no un aterrizaje genérico). <c>RequisitoPendiente</c> no
    /// tiene URL propia — abre un <c>ContextWorkspacePanel</c> in-situ, sin
    /// ruta por Id — <c>null</c> documenta ese hueco en vez de inventar una
    /// URL que no lleva a ningún sitio real; el llamador cross-Tenant cae a
    /// <c>/bandeja</c> del Tenant destino como fallback (misma decisión).
    /// </summary>
    public static string? ResolverUrl(ItemBandejaDto item) => item.Tipo switch
    {
        // Falta/Vencido/Urgente vienen de ObtenerAlertasQuery — mismo destino
        // que ya usa GestionarAlerta en Alertas.razor.cs.
        TipoItemBandeja.RevisionIa => "/documentos/revision-ia",
        TipoItemBandeja.RequisitoPendiente => null,
        // Mismo destino que ya usa el botón "Crear visita" de la Bandeja de
        // Comunicaciones — el Drawer de /visitas prellena los datos.
        TipoItemBandeja.SugerenciaVisitaUrgente => $"/visitas?sugerenciaId={item.SugerenciaVisitaId}",
        // Sin deep-link a una Visita concreta todavía (el Drawer de detalle
        // es estado interno de /visitas, no hay ruta por Id) — abre la lista
        // ya filtrada por urgencia es lo más cercano disponible hoy.
        TipoItemBandeja.VisitaUrgente => "/visitas",
        TipoItemBandeja.DeteccionPendiente => $"/empresas/{item.EmpresaId}/deteccion-trabajadores",
        // La acción real (MarcarAcreditacionSubidaCommand) vive en la pestaña
        // Plataforma de /documentos, no en DocumentoWorkspacePanel (el
        // fallback genérico de abajo) — ese panel no tiene ningún control de
        // acreditación por plataforma.
        TipoItemBandeja.PlataformaPendiente or TipoItemBandeja.PlataformaRechazada or TipoItemBandeja.EnPlataformaSeguimiento
            or TipoItemBandeja.PlataformaVencida => "/documentos?pestana=plataforma",
        _ => item.DocumentoId is { } documentoId
            ? $"/documentos?documentoId={documentoId}"
            : $"/documentos?trabajadorId={item.TrabajadorId}&tipoDocumentoId={item.TipoDocumentoId}"
    };

    private static Task AbrirRequisitoAsync(ItemBandejaDto item, ContextWorkspaceService workspaceService) =>
        item.CentroId is { } centroId
            ? workspaceService.AbrirAsync(EntidadWorkspace.Centro, centroId, item.Subtitulo, "requisitos")
            : Task.CompletedTask;

    private static Task Navegar(NavigationManager navigationManager, string? url)
    {
        if (url is not null)
            navigationManager.NavigateTo(url);
        return Task.CompletedTask;
    }
}
