using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace CaeManager.Web.Features.Documentos.Components;

/// <summary>Lo que el panel avisa al visor al resolver un adjunto: queda asignado, rechazado o (al revisarlo de nuevo) pendiente.</summary>
public sealed record ResolucionAdjuntoVisor(Guid AdjuntoId, EstadoAdjuntoVisor Estado);

/// <summary>
/// Lo que <c>VisorDocumentoPantallaCompleta</c> da al panel de su derecha (el parámetro <c>Panel</c>): el adjunto actual con su estado
/// vigente, cuántos quedan sin resolver (contando el actual), el total de la cola y las salidas del visor. El visor es del Núcleo y no
/// conoce quién pone el panel (el de revisión de adjuntos es de Comunicaciones): solo recibe lo que el panel le devuelve por aquí.
/// <paramref name="Resolver"/> avisa de que el adjunto queda asignado, rechazado o pendiente de nuevo (el visor mueve la cola y avisa a quien lo abrió);
/// <paramref name="Dejar"/> pasa al siguiente pendiente; <paramref name="Tecla"/> recibe las teclas que no son de un campo, que el visor trata
/// como de un panel desplazable (las flechas desplazan; Escape, « [ », « ] », « + », « = » y « - » siguen valiendo).
/// </summary>
public sealed record ContextoPanelVisor(
    AdjuntoVisor Adjunto,
    int Pendientes,
    int Total,
    EventCallback<ResolucionAdjuntoVisor> Resolver,
    EventCallback Dejar,
    EventCallback<KeyboardEventArgs> Tecla);