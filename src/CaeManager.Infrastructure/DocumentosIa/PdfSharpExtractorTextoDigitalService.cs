using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using Microsoft.Extensions.Logging;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Content;
using PdfSharp.Pdf.Content.Objects;
using PdfSharp.Pdf.IO;

namespace CaeManager.Infrastructure.DocumentosIa;

/// <summary>
/// Reutiliza el mismo <see cref="ContentReader"/> de PdfSharp que
/// <see cref="PdfSharpClasificadorDocumentoService"/> usa para detectar
/// texto, pero esta vez extrayendo el valor real de los operandos de los
/// operadores <c>Tj</c>/<c>TJ</c> — <c>CString.Value</c> ya viene decodificado
/// por PdfSharp a texto legible. Nota honesta: la fidelidad depende de que
/// el PDF de origen tenga una codificación de fuente que PdfSharp sepa
/// decodificar (normal en la mayoría de PDFs generados por herramientas
/// estándar); no es una librería de extracción de texto dedicada como
/// PdfPig/iText, pero basta para alimentar un modelo de IA tolerante a
/// pequeños artefactos, no para una copia exacta carácter a carácter.
/// </summary>
public class PdfSharpExtractorTextoDigitalService(
    ILogger<PdfSharpExtractorTextoDigitalService> logger) : IExtractorTextoDigitalService
{
    private static readonly HashSet<string> OperadoresDeTexto = new(StringComparer.Ordinal) { "Tj", "TJ" };

    /// <summary>
    /// REC-186: mismo vector que PdfSharpClasificadorDocumentoService (ver
    /// su doc-comment) — este método se llama desde DocumentAIRouterService
    /// antes de cualquier tope de tamaño, y también desde
    /// ValidacionDocumentoOficialService sobre Documento.ArchivoUrl, un PDF
    /// que un tenant subió o renovó (CrearDocumentoCommand,
    /// RenovarDocumentoCommand) — nunca contenido generado internamente.
    /// Mismo umbral y mismo razonamiento que allí: reutiliza el orden de
    /// magnitud ya documentado de MaximoPaginasCombinadas (REC-176), no una
    /// cota medida contra un punto de ruptura de PdfSharp.
    /// </summary>
    private const int MaximoPaginasDocumento = 2000;

    public Result<IReadOnlyList<string>> ExtraerTextoPorPagina(byte[] contenidoPdf)
    {
        // ANTES de abrir con PdfReader — ver el doc-comment de
        // MaximoPaginasDocumento. Abstención (null) cae al try/catch de
        // siempre, sin cambiar la decisión de aceptar o rechazar.
        if (LectorRecuentoPaginasPdfSinAbrir.IntentarLeerRecuentoDePaginasSinAbrir(contenidoPdf) is { } paginasDeclaradas &&
            paginasDeclaradas > MaximoPaginasDocumento)
        {
            logger.LogWarning(
                "PDF rechazado antes de abrir: declara {PaginasDeclaradas} páginas, por encima del máximo de {MaximoPaginas} ({Bytes} bytes).",
                paginasDeclaradas, MaximoPaginasDocumento, contenidoPdf.Length);

            return Result.Fallo<IReadOnlyList<string>>(Error.Crear(
                "ExtractorTextoDigital.DemasiadasPaginas",
                $"Este archivo declara más de {MaximoPaginasDocumento} páginas y no se puede procesar."));
        }

        try
        {
            using var flujo = new MemoryStream(contenidoPdf);
            using var documento = PdfReader.Open(flujo, PdfDocumentOpenMode.Import);

            IReadOnlyList<string> paginas = documento.Pages.Cast<PdfPage>()
                .Select(ExtraerTextoDePagina)
                .ToList();

            return Result.Exito(paginas);
        }
        catch (Exception ex)
        {
            // Mismo motivo que en PdfSharpClasificadorDocumentoService: el
            // microcopy del Result no sirve para diagnosticar, y esta es la
            // única oportunidad de saber por qué falló la extracción.
            logger.LogError(ex, "No se pudo extraer el texto digital del PDF ({Bytes} bytes).", contenidoPdf.Length);

            return Result.Fallo<IReadOnlyList<string>>(Error.Crear(
                "ExtractorTextoDigital.ArchivoInvalido", "No pudimos leer el texto de este archivo."));
        }
    }

    private static string ExtraerTextoDePagina(PdfPage pagina)
    {
        var secuencia = ContentReader.ReadContent(pagina);
        // TJ trae sus operandos anidados en un CArray (texto + ajustes de
        // kerning intercalados) en vez de un CString suelto como Tj — se
        // aplana también el propio operando para cubrir ambos casos.
        var textos = Aplanar(secuencia)
            .OfType<COperator>()
            .Where(operador => OperadoresDeTexto.Contains(operador.OpCode.Name))
            .SelectMany(operador => Aplanar(operador.Operands).OfType<CString>().Select(cadena => cadena.Value));

        return string.Join(" ", textos);
    }

    private static IEnumerable<CObject> Aplanar(CSequence secuencia)
    {
        foreach (var objeto in secuencia)
        {
            yield return objeto;
            if (objeto is CSequence interna)
            {
                foreach (var hijo in Aplanar(interna))
                    yield return hijo;
            }
        }
    }
}
