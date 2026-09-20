namespace CaeManager.Application.Common;

/// <summary>
/// Qué se espera del destinatario al abrir el correo. Va en la franja de tipo
/// que el envoltorio pone bajo la cabecera, junto a la clase del aviso
/// (<see cref="TipoAvisoCorreo"/>) y su ámbito: el receptor tiene que saber de
/// un vistazo qué es el correo y si le toca hacer algo. La etiqueta es texto,
/// nunca solo color — el diseño no puede depender de que se distingan tonos.
/// </summary>
public enum AccionEsperada
{
    /// <summary>Solo informa: «No requiere acción».</summary>
    Ninguna,

    /// <summary>Hay un paso que dar, normalmente con un botón: «Acción requerida».</summary>
    Accion,

    /// <summary>Hay que mirar algo, sin un paso concreto: «Requiere revisión».</summary>
    Revision,

    /// <summary>Se espera una respuesta al correo: «Respuesta necesaria».</summary>
    Respuesta,
}

/// <summary>
/// Lo que el envoltorio de marca necesita saber de un correo además de su
/// contenido: si el destinatario tiene que hacer algo, de qué ámbito viene y
/// el texto oculto que los clientes muestran junto al asunto en la bandeja.
///
/// <para>
/// Es un parámetro obligatorio de <see cref="IEmailService.EnviarAsync"/> por
/// la misma razón que <see cref="TipoAvisoCorreo"/>: con un valor por defecto,
/// un llamador nuevo que no lo piense compila igual y sale con una franja
/// genérica que no le dice nada al receptor.
/// </para>
/// </summary>
/// <param name="Accion">Qué se espera del destinatario.</param>
/// <param name="Ambito">De dónde viene el aviso («Tu cuenta», «Documentación»…). Texto corto, sin HTML.</param>
/// <param name="Preheader">
/// Texto oculto de vista previa (≤ 90 caracteres). Completa el asunto sin
/// repetirlo; si queda corto, Gmail rellena con el principio del cuerpo. Sin HTML.
/// </param>
public sealed record EncabezadoCorreo(AccionEsperada Accion, string Ambito, string Preheader);
