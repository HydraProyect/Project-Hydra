using System.Text;
using CaeManager.Application.Common;
using CaeManager.Domain.Common;
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
public class PdfSharpExtractorTextoDigitalService : IExtractorTextoDigitalService
{
    private static readonly HashSet<string> OperadoresDeTexto = new(StringComparer.Ordinal) { "Tj", "TJ" };

    public Result<string> ExtraerTexto(byte[] contenidoPdf)
    {
        try
        {
            using var flujo = new MemoryStream(contenidoPdf);
            using var documento = PdfReader.Open(flujo, PdfDocumentOpenMode.Import);

            var constructor = new StringBuilder();
            foreach (var pagina in documento.Pages.Cast<PdfPage>())
            {
                var textoPagina = ExtraerTextoDePagina(pagina);
                if (textoPagina.Length > 0)
                    constructor.AppendLine(textoPagina);
            }

            return Result.Exito(constructor.ToString());
        }
        catch (Exception)
        {
            return Result.Fallo<string>(Error.Crear(
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
