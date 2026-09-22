namespace CaeManager.Web.Features.Notificaciones.Recursos;

/// <summary>
/// Marcador de <c>IStringLocalizer&lt;TextosNotificaciones&gt;</c>: los textos
/// propios del popup de notificaciones. <c>TextosNotificaciones.resx</c> es el
/// neutral (español) y <c>TextosNotificaciones.ca-ES.resx</c> el catalán, con
/// las mismas claves (<c>LocalizacionRecursosYRegistroTests</c>, Architecture.Tests).
///
/// <para>
/// Solo el marco del popup vive aquí. El título, el mensaje y el texto de la
/// acción de cada notificación llegan ya redactados en
/// <c>NotificacionDto</c>: son datos, no textos de la interfaz.
/// </para>
/// </summary>
public sealed class TextosNotificaciones;
