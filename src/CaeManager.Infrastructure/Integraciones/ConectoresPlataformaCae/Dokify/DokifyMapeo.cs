using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CaeManager.Infrastructure.Integraciones.ConectoresPlataformaCae.Dokify;

/// <summary>
/// De la respuesta JSON de dokify a los modelos de TALVEG. Cada función devuelve <c>null</c> si falta lo que identifica
/// la respuesta (el cliente lo trata como inesperado) y descarta las filas sin identificador. Lee con
/// <c>TryGetProperty</c> y acepta un número donde llega un texto y al revés: la wiki no fija los tipos y el backend es
/// PHP, que serializa un mapa vacío como <c>[]</c>. Ningún texto del tercero pasa de <see cref="LongitudMaximaTexto"/>.
/// </summary>
internal static partial class DokifyMapeo
{
    public const int LongitudMaximaTexto = 256;

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

    /// <summary>Un objeto por tipo de documento, y dentro de él las solicitudes de ese tipo: una fila por solicitud.</summary>
    public static IReadOnlyList<DocumentoDokify>? Documentos(JsonElement raiz)
    {
        if (!EsColeccion(raiz))
            return null;

        var documentos = new List<DocumentoDokify>();
        foreach (var (claveTipo, tipo) in Entradas(raiz))
        {
            if (!tipo.TryGetProperty("requests", out var solicitudes) || !EsColeccion(solicitudes))
                continue;

            var tipoId = Texto(tipo, "uid") ?? claveTipo;
            var tipoNombre = Texto(tipo, "name") ?? string.Empty;
            foreach (var (claveSolicitud, solicitud) in Entradas(solicitudes))
            {
                var codigo = Entero(solicitud, "status_code");
                documentos.Add(new DocumentoDokify(
                    tipoId, tipoNombre, Texto(solicitud, "uid") ?? claveSolicitud, codigo, EstadoDesdeCodigo(codigo)));
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
    /// tramo y sin caracteres de ruta, y un tipo de contenido que no parezca uno se sustituye por el genérico.
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

        var tipo = Texto(raiz, "contentType");
        var nombre = Path.GetFileName((Texto(raiz, "fileName") ?? string.Empty).Replace('\\', '/'));
        foreach (var invalido in Path.GetInvalidFileNameChars())
            nombre = nombre.Replace(invalido, '_');

        return new DocumentoDescargadoDokify(
            nombre.Length == 0 ? "documento" : nombre,
            tipo is not null && TipoContenido().IsMatch(tipo) ? tipo : "application/octet-stream",
            contenido);
    }

    [GeneratedRegex(@"^[A-Za-z0-9][\w.+-]*/[A-Za-z0-9][\w.+-]*\z", RegexOptions.CultureInvariant)]
    private static partial Regex TipoContenido();

    private static PaginaDokify<T>? Pagina<T>(JsonElement raiz, Func<JsonElement, T?> fila)
        where T : class
    {
        if (raiz.ValueKind != JsonValueKind.Object
            || !raiz.TryGetProperty("data", out var datos) || datos.ValueKind != JsonValueKind.Array
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
