using System.Net;
using System.Net.Mail;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace CaeManager.Web.Features.Contacto;

/// <summary>
/// Cuerpo JSON del formulario «Pedir una demo» de la web pública (talveg.es,
/// <c>contacto.js</c>). Los códigos numéricos son los mismos índices que usa el
/// formulario (<c>perfil</c> 1-5, <c>tramo</c> 1-7, <c>intereses</c> 1-6); el
/// texto que acaba en el correo lo pone el servidor desde su propia tabla, no
/// el navegador (ASVS 4.0.3 V5.1.3: lista de valores permitidos).
/// </summary>
public sealed class SolicitudDemoPeticion
{
    [JsonPropertyName("nombre")] public string? Nombre { get; set; }

    [JsonPropertyName("correo")] public string? Correo { get; set; }

    [JsonPropertyName("telefono")] public string? Telefono { get; set; }

    [JsonPropertyName("perfil")] public int Perfil { get; set; }

    [JsonPropertyName("tramo")] public int Tramo { get; set; }

    [JsonPropertyName("intereses")] public int[]? Intereses { get; set; }

    /// <summary>Texto libre de «Otra» en el paso «Qué te preocupa». Solo cuenta si <see cref="Intereses"/> incluye el 6.</summary>
    [JsonPropertyName("otra")] public string? Otra { get; set; }

    /// <summary>
    /// Campo trampa: el formulario lo pinta oculto para personas, así que llega
    /// vacío salvo cuando lo rellena un robot. Con valor, la petición se descarta
    /// en silencio con la misma respuesta que un envío bueno, para no enseñarle
    /// al robot qué lo delató.
    /// </summary>
    [JsonPropertyName("sitioWeb")] public string? SitioWeb { get; set; }
}

/// <summary>Solicitud ya validada y normalizada (recortada); lo único que llega a componer el correo.</summary>
public sealed record SolicitudDemoValida(
    string Nombre,
    string Correo,
    string? Telefono,
    int Perfil,
    int Tramo,
    IReadOnlyList<int> Intereses,
    string? Otra);

/// <summary>
/// Validación en servidor del formulario de demo (ASVS 4.0.3 V5.1.3 y V5.1.4:
/// lista de permitidos para los códigos, longitud, juego de caracteres y patrón
/// para los textos). Función pura: no toca base de datos ni correo, y por eso
/// se prueba entera en unitarios.
/// </summary>
public static partial class ValidadorSolicitudDemo
{
    public const int NombreMinimo = 2;
    public const int NombreMaximo = 100;
    public const int CorreoMaximo = 254;
    public const int TelefonoMaximo = 30;
    public const int OtraMaximo = 300;

    /// <summary>Perfil 5: «Coordino a mis contratistas» (TALVEG Valida, a medida). Sin paso de intereses y con su propia escala de tramos.</summary>
    public const int PerfilValida = 5;

    /// <summary>Interés 6: «Otra», el único que admite texto libre.</summary>
    public const int InteresOtra = 6;

    private static readonly int[] TramosValida = [1, 2, 3, 7];

    public static IReadOnlyDictionary<int, string> Perfiles { get; } = new Dictionary<int, string>
    {
        [1] = "Empresa contratista",
        [2] = "Operador CAE externo",
        [3] = "Gestor CAE invitado",
        [4] = "Otro",
        [5] = "Coordino a mis contratistas (TALVEG Valida, a medida)",
    };

    private static readonly Dictionary<int, string> TramosEstandar = new()
    {
        [1] = "1 a 10",
        [2] = "11 a 40",
        [3] = "41 a 80",
        [4] = "81 a 150",
        [5] = "151 a 300",
        [6] = "Más de 300",
        [7] = "No lo sé",
    };

    private static readonly Dictionary<int, string> TramosDeValida = new()
    {
        [1] = "Menos de 50",
        [2] = "50 a 200",
        [3] = "Más de 200",
        [7] = "No lo sé",
    };

    public static IReadOnlyDictionary<int, string> InteresesConocidos { get; } = new Dictionary<int, string>
    {
        [1] = "Pasar las auditorías",
        [2] = "Las caducidades",
        [3] = "Lo que me piden mis Clientes",
        [4] = "Importar mis datos",
        [5] = "Ordenar a mi equipo o mi asesoría",
        [6] = "Otra",
    };

    // Misma forma que mailOk() de contacto.js, más estricta en servidor: sin
    // espacios ni controles, y comprobada después con MailAddress.
    // \z y no $: en .NET, $ también casa antes de un \n final.
    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]{2,}\z",RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex PatronCorreo();

    [GeneratedRegex(@"^\+?[0-9 ().-]{6,30}\z", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex PatronTelefono();

    public static string TextoTramo(int perfil, int tramo) =>
        (perfil == PerfilValida ? TramosDeValida : TramosEstandar).GetValueOrDefault(tramo, string.Empty);

    /// <summary>
    /// Devuelve la solicitud normalizada, o la lista de campos que no pasan. Los
    /// nombres de campo son los del contrato JSON: es lo único que se le dice al
    /// navegador (ASVS 4.0.3 V7.4.1: sin detalle interno).
    /// </summary>
    public static (SolicitudDemoValida? Solicitud, IReadOnlyList<string> CamposNoValidos) Validar(SolicitudDemoPeticion peticion)
    {
        var errores = new List<string>();

        var nombre = peticion.Nombre?.Trim() ?? string.Empty;
        if (nombre.Length is < NombreMinimo or > NombreMaximo || TieneCaracteresNoPermitidos(nombre))
            errores.Add("nombre");

        var correo = peticion.Correo?.Trim() ?? string.Empty;
        if (!CorreoValido(correo))
            errores.Add("correo");

        var telefono = string.IsNullOrWhiteSpace(peticion.Telefono) ? null : peticion.Telefono.Trim();
        if (telefono is not null && (telefono.Length > TelefonoMaximo || !PatronTelefono().IsMatch(telefono)))
            errores.Add("telefono");

        var perfil = peticion.Perfil;
        if (!Perfiles.ContainsKey(perfil))
            errores.Add("perfil");

        var tramo = peticion.Tramo;
        var tramoValido = perfil == PerfilValida ? TramosValida.Contains(tramo) : TramosEstandar.ContainsKey(tramo);
        if (!tramoValido)
            errores.Add("tramo");

        // Quien coordina a sus contratistas se salta el paso de intereses (contacto.js,
        // pasos()): lo que llegue de una elección anterior en el navegador no cuenta.
        IReadOnlyList<int> intereses = [];
        string? otra = null;
        if (perfil != PerfilValida)
        {
            var recibidos = peticion.Intereses ?? [];
            if (recibidos.Length is 0 || recibidos.Length > InteresesConocidos.Count
                || recibidos.Distinct().Count() != recibidos.Length
                || !recibidos.All(InteresesConocidos.ContainsKey))
            {
                errores.Add("intereses");
            }
            else
            {
                intereses = [.. recibidos.Order()];
            }

            otra = string.IsNullOrWhiteSpace(peticion.Otra) ? null : peticion.Otra.Trim();
            if (otra is not null && (otra.Length > OtraMaximo || TieneCaracteresNoPermitidos(otra)))
                errores.Add("otra");
            if (!intereses.Contains(InteresOtra))
                otra = null;
        }

        if (errores.Count > 0)
            return (null, errores);

        return (new SolicitudDemoValida(nombre, correo, telefono, perfil, tramo, intereses, otra), errores);
    }

    /// <summary>
    /// Sin controles (CR, LF, tabulador...) ni caracteres de formato invisibles
    /// (marcas de dirección bidi, espacios de anchura cero): todos los campos de
    /// texto del formulario son de una línea. Cierra la inyección de cabeceras
    /// SMTP aunque ningún campo libre vaya a una cabecera (ASVS 4.0.3 V5.2.3).
    /// </summary>
    private static bool TieneCaracteresNoPermitidos(string texto) =>
        texto.Any(c => char.IsControl(c) || char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.Format);

    private static bool CorreoValido(string correo) =>
        correo.Length is > 0 and <= CorreoMaximo
        && !TieneCaracteresNoPermitidos(correo)
        && PatronCorreo().IsMatch(correo)
        // Dirección exacta: sin nombre visible ni formas que MailAddress reinterprete
        // («a@b.es, c@d.es», «"x" <a@b.es>»), porque va a la cabecera Reply-To.
        && MailAddress.TryCreate(correo, out var direccion)
        && direccion.Address == correo;
}

/// <summary>
/// Compone el correo interno que recibe info@talveg.es. Todo valor que llega
/// del navegador se codifica para HTML (ASVS 4.0.3 V5.3.1); el asunto es fijo y
/// solo lleva el texto del perfil, que sale de la tabla del servidor.
/// </summary>
public static class CorreoSolicitudDemo
{
    public static string Asunto(SolicitudDemoValida solicitud) =>
        $"Solicitud de demo desde talveg.es: {ValidadorSolicitudDemo.Perfiles[solicitud.Perfil]}";

    public static string CuerpoHtml(SolicitudDemoValida solicitud)
    {
        var html = new StringBuilder();
        html.Append("<p>Ha llegado una solicitud desde el formulario «Pedir una demo» de talveg.es.</p>");
        html.Append("<table cellpadding=\"4\" cellspacing=\"0\">");
        Fila(html, "Nombre", solicitud.Nombre);
        Fila(html, "Correo", solicitud.Correo);
        Fila(html, "Teléfono", solicitud.Telefono ?? "(no indicado)");
        Fila(html, "Perfil", ValidadorSolicitudDemo.Perfiles[solicitud.Perfil]);
        Fila(html,
            solicitud.Perfil == ValidadorSolicitudDemo.PerfilValida ? "Personas de contratas al mes" : "Trabajadores activos",
            ValidadorSolicitudDemo.TextoTramo(solicitud.Perfil, solicitud.Tramo));
        if (solicitud.Intereses.Count > 0)
        {
            Fila(html, "Qué le preocupa",
                string.Join(", ", solicitud.Intereses.Select(i => ValidadorSolicitudDemo.InteresesConocidos[i])));
        }

        if (solicitud.Otra is not null)
            Fila(html, "Lo que cuenta", solicitud.Otra);
        html.Append("</table>");
        html.Append("<p>Responder a este correo contesta directamente a quien lo ha enviado.</p>");
        return html.ToString();
    }

    private static void Fila(StringBuilder html, string etiqueta, string valor) =>
        html.Append("<tr><th align=\"left\">").Append(WebUtility.HtmlEncode(etiqueta))
            .Append("</th><td>").Append(WebUtility.HtmlEncode(valor)).Append("</td></tr>");
}
