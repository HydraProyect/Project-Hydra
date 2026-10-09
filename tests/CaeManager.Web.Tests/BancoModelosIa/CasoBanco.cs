using System.Globalization;
using CaeManager.Application.Common;
using CaeManager.Application.Comunicaciones.Deteccion;
using CaeManager.Application.DocumentosIa.Common;

namespace CaeManager.Web.Tests.BancoModelosIa;

/// <summary>
/// Las siete llamadas a la API de Anthropic que hace el producto, una por
/// método público de los servicios <c>Anthropic*</c> de Infrastructure. El
/// proveedor documental cuenta dos veces porque sus dos métodos son dos
/// prompts distintos (transcripción y extracción estructurada).
/// </summary>
public enum RutaIa
{
    RelevanciaCaeDeConversacion,
    GestionDocumentalEnCorreo,
    VisitaEnCorreo,
    TranscripcionDeDocumento,
    ExtraccionEstructurada,
    ListadoDeTrabajadores,
    ChatDelAsistente,
}

/// <summary>Complejidad del caso. Todo caso adversarial pone a prueba una regla de «no inventes» o de aislamiento del prompt.</summary>
public enum NivelCaso
{
    Sencillo,
    Medio,
    Dificil,
    Adversarial,
}

/// <summary>Una comprobación objetiva sobre la salida de la ruta: o se cumple o no.</summary>
public sealed record Comprobacion(string Nombre, bool Correcta, string? Detalle = null);

/// <summary>
/// Lo que el banco concluye de un caso. <paramref name="ErrorServicio"/> no
/// nulo significa que la ruta devolvió un <c>Result</c> fallido (respuesta
/// que el producto no supo interpretar, error de la API…): el caso puntúa
/// cero, porque en producción ese documento o ese correo se queda sin tratar.
/// </summary>
public sealed record Evaluacion(
    IReadOnlyList<Comprobacion> Comprobaciones,
    IReadOnlyDictionary<string, double> Medidas,
    string? ErrorServicio = null)
{
    public static Evaluacion Fallida(string codigoError) =>
        new([], new Dictionary<string, double>(), codigoError);

    public double Puntuacion =>
        ErrorServicio is not null || Comprobaciones.Count == 0
            ? 0
            : (double)Comprobaciones.Count(c => c.Correcta) / Comprobaciones.Count;
}

/// <summary>Los servicios reales del producto, construidos sobre el <see cref="HttpClient"/> que el banco instrumenta.</summary>
public sealed record ServiciosAnthropic(
    IDeteccionRelevanciaCaeService RelevanciaCae,
    IDeteccionGestionCorreoService GestionCorreo,
    IDeteccionVisitaCorreoService VisitaCorreo,
    IDocumentAIProvider Documental,
    IExtraccionTrabajadoresIaService ListadoTrabajadores,
    IAsistenteIaService Chat);

/// <summary>
/// Un caso del banco: la entrada sintética, lo que se espera y cómo se
/// puntúa. <see cref="RespuestaIdeal"/> y <see cref="RespuestaErronea"/> son
/// el texto que devolvería un modelo perfecto y uno plausible pero
/// equivocado: alimentan al modelo simulado con el que se demuestra, sin
/// clave, que cada métrica puede dar el máximo y puede dar menos.
/// </summary>
public abstract record CasoBanco(string Id, NivelCaso Nivel, string Descripcion)
{
    public abstract RutaIa Ruta { get; }

    public abstract Task<Evaluacion> EjecutarAsync(ServiciosAnthropic servicios, CancellationToken cancellationToken);

    public abstract string RespuestaIdeal();

    public abstract string RespuestaErronea();

    protected static Dictionary<string, double> SinMedidas() => [];
}

/// <summary>Comparación de texto que no distingue mayúsculas ni tildes: lo que se mide es el contenido, no la ortografía del modelo.</summary>
public static class TextoBanco
{
    private static readonly CompareInfo Comparador = CultureInfo.InvariantCulture.CompareInfo;

    private const CompareOptions Opciones = CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace;

    public static bool Contiene(string? texto, string fragmento) =>
        texto is not null && Comparador.IndexOf(texto, fragmento, Opciones) >= 0;

    /// <summary>Un identificador (DNI, NIE, CIF) sin espacios, puntos ni guiones y en mayúsculas.</summary>
    public static string Identificador(string? valor) =>
        new string((valor ?? string.Empty).Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();

    public static string SinTildes(string texto) =>
        new(texto.Normalize(System.Text.NormalizationForm.FormD)
            .Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            .ToArray());

    public static string[] Palabras(string texto) =>
        texto.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
}
