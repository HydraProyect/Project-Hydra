using System.Net;

namespace CaeManager.Application.Common;

/// <summary>
/// Llamada a la acción de un correo de TALVEG: un botón hecho solo con texto,
/// enlace y estilo en línea, seguido de la misma dirección escrita como texto.
///
/// <para>
/// <b>Por qué así.</b> Muchas organizaciones bloquean las imágenes de remitentes
/// externos y algunos clientes descartan el bloque <c>&lt;style&gt;</c> del
/// correo; un botón dibujado con una imagen, o cuyo aspecto dependa de una
/// clase, desaparece o pierde su jerarquía. Aquí el botón es un
/// <c>&lt;a&gt;</c> con su texto dentro de una celda con color de fondo (un
/// color, no una imagen) que lleva el relleno (Outlook de escritorio ignora el
/// relleno de un <c>&lt;a&gt;</c>, no el de una celda), y todo su estilo va en línea, de modo que se ve igual
/// con las imágenes bloqueadas y sin la hoja de estilos.
/// </para>
///
/// <para>
/// La dirección se repite debajo como texto porque hay clientes que reescriben
/// o desactivan los enlaces de remitentes externos, y porque en la parte de
/// texto plano del correo y en un lector de pantalla tiene que poder copiarse.
/// </para>
///
/// <para>
/// Los estilos en línea fijan <c>width</c>, <c>padding</c> y <c>border</c> de la
/// tabla y de la celda: la plantilla de marca (<c>SmtpEmailService</c>) da a
/// <c>.cuerpo table</c> y <c>.cuerpo td</c> ancho completo, relleno y borde
/// inferior, pensados para las tablas de datos, y el botón no debe heredarlos.
/// </para>
/// </summary>
public static class BotonCorreo
{
    /// <summary>
    /// HTML del botón y de la dirección visible. <paramref name="texto"/> y
    /// <paramref name="url"/> se codifican aquí: el llamador los pasa sin codificar.
    /// </summary>
    public static string Html(string texto, string url)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(texto);
        ArgumentException.ThrowIfNullOrWhiteSpace(url);

        // Defensa en profundidad: la codificación HTML no neutraliza un esquema peligroso
        // («javascript:», «data:»), así que solo se admiten direcciones absolutas http(s).
        if (!Uri.TryCreate(url, UriKind.Absolute, out var destino)
            || (destino.Scheme != Uri.UriSchemeHttps && destino.Scheme != Uri.UriSchemeHttp))
        {
            throw new ArgumentException("La dirección del botón debe ser una URL absoluta http o https.", nameof(url));
        }

        var textoHtml = WebUtility.HtmlEncode(texto);
        var urlHtml = WebUtility.HtmlEncode(url);

        return $"""
            <table role="presentation" cellpadding="0" cellspacing="0" border="0" style="width:auto;border-collapse:separate;margin:6px 0 14px;">
              <tr>
                <td bgcolor="#122A21" style="background-color:#122A21;border-radius:4px;padding:14px 24px;border-bottom:0;">
                  <a href="{urlHtml}" style="display:inline-block;font-family:Arial,Helvetica,sans-serif;font-size:16px;font-weight:bold;line-height:1.2;color:#FFFFFF;text-decoration:none;">{textoHtml}</a>
                </td>
              </tr>
            </table>
            <p style="margin:0 0 13px;font-family:Arial,Helvetica,sans-serif;font-size:12px;line-height:1.5;color:#4F5A54;">Si el botón no funciona, copia esta dirección en tu navegador:<br><a href="{urlHtml}" style="color:#122A21;text-decoration:underline;word-break:break-all;">{urlHtml}</a></p>
            """;
    }
}
