using CaeManager.Application.Common;
using Ganss.Xss;

namespace CaeManager.Infrastructure.Comunicaciones;

/// <summary>
/// <see cref="ISanitizadorHtmlService"/> sobre HtmlSanitizer (Ganss.Xss), que
/// parsea el marcado con AngleSharp y reconstruye la salida a partir de la
/// lista blanca — no es un filtro por expresiones regulares, que es lo que
/// hace evitable la larga lista de evasiones conocidas.
///
/// Singleton a propósito: <see cref="HtmlSanitizer"/> se configura una vez y
/// sus llamadas no guardan estado entre invocaciones.
/// </summary>
public class GanssSanitizadorHtmlService : ISanitizadorHtmlService
{
    private readonly HtmlSanitizer _sanitizador;

    public GanssSanitizadorHtmlService()
    {
        _sanitizador = new HtmlSanitizer();

        // Se parte de la lista blanca por defecto (ya sin script/iframe/object
        // ni atributos on*) y se recorta lo que un cuerpo de correo no
        // necesita para leerse:
        //
        //   - form/input/button: un correo entrante no debe poder pintar un
        //     formulario dentro de una pantalla autenticada — es la vía
        //     directa al phishing con la marca del propio producto.
        //   - style y class: sin ellos, el marcado no puede superponerse al
        //     resto de la interfaz ni pescar clases del design system para
        //     disfrazarse de UI de la aplicación.
        //   - img: hoy solo serviría para el rastreo por píxel invisible del
        //     remitente. Cuando la ingesta Graph pida fidelidad visual, la vía
        //     es el iframe aislado que propone el informe, no reabrir esto.
        foreach (var etiqueta in new[] { "form", "input", "button", "select", "textarea", "img", "style" })
            _sanitizador.AllowedTags.Remove(etiqueta);

        foreach (var atributo in new[] { "style", "class", "id" })
            _sanitizador.AllowedAttributes.Remove(atributo);

        _sanitizador.AllowedSchemes.Clear();
        _sanitizador.AllowedSchemes.Add("http");
        _sanitizador.AllowedSchemes.Add("https");
        _sanitizador.AllowedSchemes.Add("mailto");

        // Un enlace de correo lleva a un sitio ajeno: se abre fuera y sin
        // pasar el Referer ni dar acceso a window.opener.
        _sanitizador.PostProcessNode += (_, e) =>
        {
            if (e.Node is AngleSharp.Html.Dom.IHtmlAnchorElement ancla)
            {
                ancla.SetAttribute("target", "_blank");
                ancla.SetAttribute("rel", "noopener noreferrer nofollow");
            }
        };
    }

    public string Sanear(string? html) =>
        string.IsNullOrWhiteSpace(html) ? string.Empty : _sanitizador.Sanitize(html);
}
