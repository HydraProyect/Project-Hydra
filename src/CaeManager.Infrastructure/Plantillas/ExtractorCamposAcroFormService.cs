using CaeManager.Application.Common;
using PdfSharp.Pdf.AcroForms;
using PdfSharp.Pdf.IO;

namespace CaeManager.Infrastructure.Plantillas;

/// <summary>
/// PdfSharp no expone página ni <c>/Rect</c> como propiedades tipadas en
/// <see cref="PdfAcroField"/> — se leen del diccionario crudo vía
/// <c>Elements.GetRectangle("/Rect")</c>, y la página se resuelve
/// comparando el <c>ObjectID</c> del campo contra las anotaciones de cada
/// página (confirmado por reflexión + un PDF de prueba construido a mano,
/// no hay una API directa "campo → página" en esta versión).
///
/// <c>/Rect</c> viene en coordenadas nativas de PDF (origen abajo-izquierda);
/// se convierte aquí a origen arriba-izquierda para que coincida con el
/// sistema que ya usa <see cref="RellenadorPlantillaPdfService"/> vía
/// <c>XGraphics</c> (verificado: <c>DrawString(x, y)</c> con y=10 escribe
/// cerca de la parte superior de la página, no de la inferior).
/// </summary>
public class ExtractorCamposAcroFormService : IExtractorCamposAcroFormService
{
    /// <summary>
    /// REC-186: pdfOriginal es el PDF de la plantilla, subido en
    /// ConfigurarPlantilla.razor.cs (InputFile, hasta 10 MB — un tope de
    /// bytes que un árbol de páginas compacto no toca, ver el mismo
    /// razonamiento en ConversorArchivosPdf) y luego releído del blob
    /// original en cada Extraer posterior (DetectarCamposPlantillaQuery,
    /// ConfirmarPlantillaDocumentoVersionCommand). No es contenido generado
    /// por TALVEG: "plantilla administrada por un Administrador" describe
    /// quién configura el formulario, no de dónde vienen estos bytes en
    /// concreto. Mismo umbral reutilizado que los sitios de DocumentosIa/Firmas.
    /// </summary>
    private const int MaximoPaginasDocumento = 2000;

    public IReadOnlyList<CampoAcroFormDetectado> Extraer(byte[] pdfOriginal)
    {
        // ANTES de abrir con PdfReader — ver el doc-comment de
        // MaximoPaginasDocumento. Mismo patrón que ConversorArchivosPdf
        // (REC-176): abstención (null) no cambia nada, PdfReader.Open sigue
        // siendo la red de seguridad para lo que este pre-escaneo no cubre.
        if (LectorRecuentoPaginasPdfSinAbrir.IntentarLeerRecuentoDePaginasSinAbrir(pdfOriginal) is { } paginasDeclaradas &&
            paginasDeclaradas > MaximoPaginasDocumento)
        {
            throw new InvalidDataException(
                $"El PDF de la plantilla declara más de {MaximoPaginasDocumento} páginas y no se puede procesar.");
        }

        using var flujo = new MemoryStream(pdfOriginal);
        using var documento = PdfReader.Open(flujo, PdfDocumentOpenMode.Import);

        // documento.AcroForm lanza InvalidOperationException si no hay
        // AcroForm en vez de devolver null (a pesar de estar anotado como
        // nullable) — comprobado por reflexión/prueba directa contra un PDF
        // sin campos. El diccionario crudo del catálogo sí se puede
        // consultar sin lanzar.
        if (!documento.Internals.Catalog.Elements.ContainsKey("/AcroForm"))
            return [];

        var acroForm = documento.AcroForm;

        var paginaPorObjectId = new Dictionary<string, (int Indice, double AlturaPagina)>();
        for (var p = 0; p < documento.PageCount; p++)
        {
            var pagina = documento.Pages[p];
            var anotaciones = pagina.Elements.GetArray("/Annots");
            if (anotaciones is null) continue;

            for (var a = 0; a < anotaciones.Elements.Count; a++)
            {
                var referencia = anotaciones.Elements.GetReference(a);
                if (referencia is not null)
                    paginaPorObjectId[referencia.ObjectID.ToString()] = (p + 1, pagina.Height.Point);
            }
        }

        var candidatos = new List<CampoAcroFormDetectado>();
        RecorridoCamposAcroFormSeguro.Recorrer(acroForm.Fields, campo =>
        {
            if (campo.HasKids) return; // solo los campos hoja son candidatos reales

            if (string.IsNullOrEmpty(campo.Name)) return;
            if (!paginaPorObjectId.TryGetValue(campo.Internals.ObjectID.ToString(), out var pagina)) return;

            var rect = campo.Elements.GetRectangle("/Rect");
            if (rect.Width <= 0 || rect.Height <= 0) return;

            candidatos.Add(new CampoAcroFormDetectado(
                NombreCampo: campo.Name,
                Pagina: pagina.Indice,
                X: rect.X1,
                Y: pagina.AlturaPagina - rect.Y2,
                Ancho: rect.Width,
                Alto: rect.Height));
        });
        return candidatos;
    }
}
