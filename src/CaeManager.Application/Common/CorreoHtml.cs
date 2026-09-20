using System.Net;

namespace CaeManager.Application.Common;

/// <summary>
/// Piezas de HTML de los correos de TALVEG: lo que un llamador necesita para
/// componer el <c>cuerpoHtml</c> sin conocer el envoltorio (cabecera, franja
/// de tipo, pie) ni repetir estilos. El reparto sale del diseño de correo v3:
/// el envoltorio pone lo común; el llamador pone un título, párrafos, como
/// mucho un botón y, si hace falta, una caja o una tabla.
///
/// <para>
/// Todo va en <b>tablas y estilos en línea</b> y con el color en
/// <c>bgcolor</c> además de <c>background</c>: Outlook de escritorio no
/// aplica hojas de estilo ni <c>border-radius</c>, y los clientes que invierten
/// colores por su cuenta (Gmail móvil) respetan mejor un fondo declarado en
/// el atributo. Las clases (<c>tit</c>, <c>txt</c>, <c>cta-td</c>…) solo las
/// usa el bloque <c>&lt;style&gt;</c> del envoltorio para el modo noche: si
/// el cliente lo descarta, el diseño base queda íntegro.
/// </para>
///
/// <para>
/// <b>Qué se escapa y qué no.</b> Los parámetros de texto plano
/// (<c>texto</c>, <c>url</c>, <c>titulo</c>) se codifican aquí. Los parámetros
/// <c>html</c> se insertan tal cual: el llamador es responsable de haber
/// codificado lo que venga de datos (nombres, razones sociales, documentos).
/// </para>
/// </summary>
public static class CorreoHtml
{
    private const string Sans = "font-family:Arial,Helvetica,sans-serif;";

    /// <summary>
    /// Codifica solo lo que rompe el HTML (<c>&amp; &lt; &gt; "</c>). <see cref="WebUtility.HtmlEncode(string?)"/>
    /// además convierte cada carácter no ASCII en una entidad numérica (<c>ó</c> → <c>&amp;#243;</c>): el correo
    /// va en UTF-8, así que no hace falta, y en español infla el mensaje —Gmail recorta a ~102 KB— y
    /// ensucia el texto que los clientes muestran como vista previa.
    /// </summary>
    public static string Codificar(string? texto) =>
        (texto ?? string.Empty).Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal);

    /// <summary>Título del correo (Georgia, 25 px). Uno por correo.</summary>
    public static string Titulo(string titulo) =>
        $"""<h1 class="tit" style="margin:0 0 14px;font-family:Georgia,'Times New Roman',serif;font-weight:400;font-size:25px;line-height:1.25;color:#122A21">{Codificar(titulo)}</h1>""";

    /// <summary>Párrafo de cuerpo (15 px). Admite <c>&lt;b&gt;</c> como único formato.</summary>
    public static string Parrafo(string html) =>
        $"""<p class="txt" style="margin:0 0 16px;{Sans}font-size:15px;line-height:1.62;color:#2A322E">{html}</p>""";

    /// <summary>
    /// Botón de acción: bloque de ancho completo, 48 px de alto mínimo, fondo
    /// y borde explícitos. El borde celeste es lo que salva el botón cuando el
    /// cliente invierte los colores por su cuenta. Outlook de escritorio no
    /// dibuja <c>border-radius</c>: allí sale con esquinas rectas, y se acepta.
    ///
    /// <para>
    /// Va siempre seguido del enlace en texto (<see cref="EnlaceDeRespaldo"/>):
    /// si el botón no carga, el destinatario aún puede copiar el enlace. Solo
    /// hay un botón por correo; un segundo enlace en el cuerpo compite con él.
    /// </para>
    /// </summary>
    public static string BotonCta(string texto, string url) =>
        $"""
         <table role="presentation" width="100%" cellpadding="0" cellspacing="0" border="0" style="border-collapse:separate;margin:2px 0 12px"><tr><td class="cta-td" align="center" bgcolor="#122A21" style="background:#122A21;border:2px solid #8FC7BC;border-radius:4px"><a class="cta-a" href="{Codificar(url)}" style="display:block;padding:15px 24px;{Sans}font-size:16px;font-weight:bold;line-height:20px;color:#F2EEE1;text-decoration:none;text-align:center">{Codificar(texto)}</a></td></tr></table>
         """;

    /// <summary>El enlace en texto que acompaña al botón, por si este no carga.</summary>
    public static string EnlaceDeRespaldo(string url) =>
        $"""<p class="suave" style="margin:0 0 20px;{Sans}font-size:13px;line-height:1.55;color:#5A6862;text-align:center">¿No funciona el botón? Copia este enlace:<br><a class="lnk" href="{Codificar(url)}" style="color:#122A21;word-break:break-all">{Codificar(url)}</a></p>""";

    /// <summary>
    /// Caja de acento: <b>informa</b> (caducidad, contexto). Barra celeste de
    /// 4 px. El ámbar es para corregir (<see cref="CajaAviso"/>); no se mezclan.
    /// </summary>
    public static string CajaAcento(string html) =>
        $"""
         <table role="presentation" width="100%" cellpadding="0" cellspacing="0" border="0" style="border-collapse:collapse;margin:0 0 22px"><tr><td width="4" bgcolor="#8FC7BC" style="width:4px;background:#8FC7BC;font-size:0;line-height:0">&nbsp;</td><td class="caja txt" bgcolor="#E9F3F0" style="background:#E9F3F0;padding:13px 16px;{Sans}font-size:14px;line-height:1.55;color:#2A322E">{html}</td></tr></table>
         """;

    /// <summary>
    /// Caja de aviso: <b>corrige</b> (enlace caducado, falta un dato). Borde
    /// ámbar completo, con texto: el estado nunca se apoya solo en el color.
    /// </summary>
    public static string CajaAviso(string html) =>
        $"""
         <table role="presentation" width="100%" cellpadding="0" cellspacing="0" border="0" style="border-collapse:collapse;margin:0 0 22px"><tr><td class="aviso" bgcolor="#FCF3D8" style="background:#FCF3D8;border:2px solid #B37F00;padding:13px 16px;{Sans}font-size:14px;line-height:1.55;color:#463400">{html}</td></tr></table>
         """;

    /// <summary>Estado de un documento en una tabla o resumen: palabra + símbolo + color, nunca solo color.</summary>
    public enum EstadoVisual
    {
        Vencido,
        Urgente,
        Proximo,
        SinSubir,
    }

    /// <summary>
    /// Etiqueta de estado («▲ Vencido»). Rojo para Vencido y ámbar para
    /// Urgente; Próximo y Sin subir van en gris. <b>Nunca</b> verde ni celeste:
    /// dentro de la app el verde es «Vigente», y usarlo aquí para otro estado
    /// sería una trampa. El símbolo es Unicode y puede salir como cuadro en un
    /// cliente sin la fuente: por eso la palabra acompaña siempre.
    /// </summary>
    public static string Estado(EstadoVisual estado)
    {
        var (simbolo, palabra, clase, color) = estado switch
        {
            EstadoVisual.Vencido => ("▲", "Vencido", "venc", "#A81E17"),
            EstadoVisual.Urgente => ("▲", "Urgente", "urg", "#7A5000"),
            EstadoVisual.Proximo => ("△", "Próximo", "suave", "#4F5C56"),
            _ => ("○", "Sin subir", "suave", "#4F5C56"),
        };
        return $"""<span class="{clase}" style="font-weight:bold;color:{color}">{simbolo} {palabra}</span>""";
    }
}
