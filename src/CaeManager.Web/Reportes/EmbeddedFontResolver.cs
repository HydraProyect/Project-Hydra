using System.Reflection;
using PdfSharp.Fonts;

namespace CaeManager.Web.Reportes;

/// <summary>
/// PDFsharp 6.x no depende de GDI+ ni de las fuentes del sistema operativo
/// (cross-platform), pero por eso mismo necesita que se le entregue el
/// archivo de fuente explícitamente. Se embebe DejaVu Sans (licencia
/// Bitstream Vera, ver Resources/Fonts/LICENSE-DejaVuFonts.txt) como
/// recurso del ensamblado para no depender de que el servidor de
/// despliegue tenga fuentes instaladas.
/// </summary>
public class EmbeddedFontResolver : IFontResolver
{
    public const string NombreFuente = "DejaVu Sans";

    public byte[] GetFont(string faceName)
    {
        var nombreRecurso = faceName == NombreFuente + "#b"
            ? "CaeManager.Web.Resources.Fonts.DejaVuSans-Bold.ttf"
            : "CaeManager.Web.Resources.Fonts.DejaVuSans.ttf";

        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(nombreRecurso)
            ?? throw new InvalidOperationException($"No se encontró el recurso embebido '{nombreRecurso}'.");

        using var memoria = new MemoryStream();
        stream.CopyTo(memoria);
        return memoria.ToArray();
    }

    public FontResolverInfo ResolveTypeface(string familyName, bool isBold, bool isItalic)
    {
        var esNegrita = isBold ? "#b" : string.Empty;
        return new FontResolverInfo(NombreFuente + esNegrita);
    }
}
