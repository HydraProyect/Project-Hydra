using System.Globalization;
using System.Text.Json;

namespace CaeManager.Infrastructure.Integraciones.ConectoresPlataformaCae.Dokify;

/// <summary>
/// De la respuesta JSON de dokify a los modelos de TALVEG. Cada función devuelve <c>null</c> si falta lo que identifica
/// la respuesta (el cliente HTTP lo trata como inesperado) y descarta las filas sin identificador, salvo
/// <see cref="Documentos"/>: un tipo o una solicitud sin <c>uid</c> hace inesperada la respuesta entera. Lee con
/// <c>TryGetProperty</c> y acepta un número donde llega un texto y al revés: la wiki no fija los tipos y el backend es
/// PHP, que serializa un mapa vacío como <c>[]</c>. Ningún texto del tercero pasa de <see cref="LongitudMaximaTexto"/>.
/// </summary>
internal static partial class DokifyMapeo
{
    public const int LongitudMaximaTexto = 256;

    /// <summary>Filas por página que se piden y que, como mucho, se aceptan en una página.</summary>
    public const int MaximoDeFilasPorPagina = 50;

    public static EstadoDocumentoDokify EstadoDesdeCodigo(int? codigo) => codigo switch
    {
        0 => EstadoDocumentoDokify.SinAnexar,
        2 => EstadoDocumentoDokify.Validado,
        3 => EstadoDocumentoDokify.Caducado,
        4 => EstadoDocumentoDokify.Anulado,
        _ => EstadoDocumentoDokify.Desconocido,
    };

    public static PaginaDokify<EmpresaDokify>? Empresas(JsonElement raiz) =>
        Pagina(raiz, f => Texto(f, "vat") is { } vat ? new EmpresaDokify(vat, Texto(f, "name") ?? string.Empty) : null);

    public static PaginaDokify<TrabajadorDokify>? Trabajadores(JsonElement raiz) =>
        Pagina(raiz, f => Texto(f, "vat") is { } vat
            ? new TrabajadorDokify(vat, Texto(f, "firstname") ?? string.Empty, Texto(f, "surname") ?? string.Empty)
            : null);

    public static MaquinaDokify? Maquina(JsonElement raiz) =>
        Texto(raiz, "plate") is { } matricula
            ? new MaquinaDokify(
                matricula, Texto(raiz, "name") ?? string.Empty, Texto(raiz, "model") ?? string.Empty,
                Texto(raiz, "manufacture") ?? string.Empty, Texto(raiz, "serial") ?? string.Empty)
            : null;

    public static EstadoGlobalDokify? EstadoGlobal(JsonElement raiz) =>
        raiz.ValueKind == JsonValueKind.Object && raiz.TryGetProperty("status", out var s)
        && s.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? new EstadoGlobalDokify(s.GetBoolean())
            : null;

    /// <summary>
    /// Un objeto por tipo de documento, y dentro de él las solicitudes de ese tipo: una fila por solicitud. El
    /// identificador es siempre el <c>uid</c>, nunca la clave del mapa: en la forma de lista esa clave es el índice que
    /// pone PHP, y un tipo o una solicitud sin <c>uid</c> hace inesperada la respuesta entera, porque descartar la fila
    /// callaría un documento que sí existe.
    /// </summary>
    public static IReadOnlyList<DocumentoDokify>? Documentos(JsonElement raiz)
    {
        if (!EsColeccion(raiz))
            return null;

        var documentos = new List<DocumentoDokify>();
        foreach (var (_, tipo) in Entradas(raiz))
        {
            if (!tipo.TryGetProperty("requests", out var solicitudes) || !EsColeccion(solicitudes))
                continue;

            if (Texto(tipo, "uid") is not { } tipoId)
                return null;

            var tipoNombre = Texto(tipo, "name") ?? string.Empty;
            foreach (var (_, solicitud) in Entradas(solicitudes))
            {
                if (Texto(solicitud, "uid") is not { } solicitudId)
                    return null;

                var codigo = Entero(solicitud, "status_code");
                documentos.Add(new DocumentoDokify(tipoId, tipoNombre, solicitudId, codigo, EstadoDesdeCodigo(codigo)));
            }
        }

        return documentos;
    }

    public static IReadOnlyList<AsignacionDokify>? Asignaciones(JsonElement raiz)
    {
        if (!EsColeccion(raiz))
            return null;

        var asignaciones = new List<AsignacionDokify>();
        foreach (var (_, fila) in Entradas(raiz))
        {
            if (Texto(fila, "uid") is not { } uid)
                continue;

            var grupo = fila.TryGetProperty("group", out var g) && g.ValueKind == JsonValueKind.Object ? g : default;
            asignaciones.Add(new AsignacionDokify(
                uid, Texto(grupo, "name") ?? string.Empty, Entero(grupo, "requests_attached"), Entero(grupo, "requests_validated"),
                Entero(grupo, "requests_expired"), Entero(grupo, "requests_rejected"), Entero(grupo, "requests_pending")));
        }

        return asignaciones;
    }

    /// <summary>
    /// La descarga trae el fichero en base64 dentro del JSON. El nombre es texto del tercero: se queda con su último
    /// tramo y solo con letras, cifras, espacio y <c>. - _ ( )</c>, con la misma lista en cualquier sistema operativo
    /// (<c>Path.GetInvalidFileNameChars</c> en Linux solo excluye <c>/</c> y el nulo, y dejaría pasar CR, LF, los
    /// controles, U+202E o <c>: * "</c>). El tipo de contenido solo pasa si es uno de los de
    /// <see cref="TiposDeContenidoAdmitidos"/>; cualquier otro, <c>text/html</c> o <c>image/svg+xml</c> incluidos, llega
    /// como <c>application/octet-stream</c>.
    /// </summary>
    public static DocumentoDescargadoDokify? Descarga(JsonElement raiz)
    {
        if (Texto(raiz, "document", int.MaxValue) is not { Length: > 0 } base64)
            return null;

        byte[] contenido;
        try
        {
            contenido = Convert.FromBase64String(base64);
        }
        catch (FormatException)
        {
            return null;
        }

        var tipo = Texto(raiz, "contentType")?.ToLowerInvariant();
        var nombre = NombreDeFicheroSeguro(Texto(raiz, "fileName") ?? string.Empty);

        return new DocumentoDescargadoDokify(
            nombre.Length == 0 ? "documento" : nombre,
            tipo is not null && TiposDeContenidoAdmitidos.Contains(tipo) ? tipo : "application/octet-stream",
            contenido);
    }

    /// <summary>Los tipos de un documento de acreditación que se dejan pasar tal cual; ninguno lo ejecuta un navegador.</summary>
    public static readonly IReadOnlySet<string> TiposDeContenidoAdmitidos = new HashSet<string>(StringComparer.Ordinal)
    {
        "application/pdf",
        "image/jpeg",
        "image/png",
        "image/tiff",
        "application/msword",
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        "application/vnd.ms-excel",
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        "application/vnd.oasis.opendocument.text",
        "application/zip",
    };

    private static string NombreDeFicheroSeguro(string original)
    {
        var ultimoTramo = original[(original.LastIndexOfAny(['/', '\\']) + 1)..];
        var limpio = string.Create(ultimoTramo.Length, ultimoTramo, static (destino, origen) =>
        {
            for (var i = 0; i < origen.Length; i++)
            {
                var c = origen[i];
                destino[i] = char.IsAsciiLetterOrDigit(c) || (char.IsLetter(c) && c > (char)0x7F) || c is ' ' or '.' or '-' or '_' or '(' or ')'
                    ? c
                    : '_';
            }
        });

        // Sin puntos ni espacios en los extremos: «..» no es un nombre, y Windows recorta los finales.
        return limpio.Trim(' ', '.');
    }

    private static PaginaDokify<T>? Pagina<T>(JsonElement raiz, Func<JsonElement, T?> fila)
        where T : class
    {
        // Una página con más filas de las que se piden nunca es la que se pidió: se cuenta antes de descartar las que no
        // sirven, para que el tope acote también lo que se recorre.
        if (raiz.ValueKind != JsonValueKind.Object
            || !raiz.TryGetProperty("data", out var datos) || datos.ValueKind != JsonValueKind.Array
            || datos.GetArrayLength() > MaximoDeFilasPorPagina
            || !raiz.TryGetProperty("page", out var pagina) || pagina.ValueKind != JsonValueKind.Object
            || Entero(pagina, "number") is not { } numero
            || Entero(pagina, "totalPages") is not { } totalPaginas)
            return null;

        var filas = datos.EnumerateArray().Where(f => f.ValueKind == JsonValueKind.Object).Select(fila).OfType<T>().ToList();
        return new PaginaDokify<T>(filas, numero, totalPaginas, Entero(pagina, "totalElements") ?? filas.Count);
    }

    // PHP serializa un mapa vacío como [] y uno con claves consecutivas desde 0 como una lista: se aceptan las dos formas.
    private static bool EsColeccion(JsonElement e) => e.ValueKind is JsonValueKind.Object or JsonValueKind.Array;

    private static IEnumerable<(string Clave, JsonElement Valor)> Entradas(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.Object)
            return e.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.Object).Select(p => (p.Name, p.Value));

        return e.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.Object).Select((v, i) => (i.ToString(CultureInfo.InvariantCulture), v));
    }

    private static string? Texto(JsonElement e, string nombre, int longitudMaxima = LongitudMaximaTexto)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(nombre, out var v))
            return null;

        var texto = v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            _ => null,
        };

        texto = texto?.Trim();
        return string.IsNullOrEmpty(texto) ? null : texto[..Math.Min(texto.Length, longitudMaxima)];
    }

    private static int? Entero(JsonElement e, string nombre)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(nombre, out var v))
            return null;

        return v.ValueKind switch
        {
            JsonValueKind.Number when v.TryGetInt32(out var n) => n,
            JsonValueKind.String when int.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) => n,
            _ => null,
        };
    }
}
