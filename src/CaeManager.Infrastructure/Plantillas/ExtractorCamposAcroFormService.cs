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
    public IReadOnlyList<CampoAcroFormDetectado> Extraer(byte[] pdfOriginal)
    {
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
        RecorrerCampos(acroForm.Fields, paginaPorObjectId, candidatos);
        return candidatos;
    }

    private static void RecorrerCampos(
        PdfAcroField.PdfAcroFieldCollection campos,
        Dictionary<string, (int Indice, double AlturaPagina)> paginaPorObjectId,
        List<CampoAcroFormDetectado> candidatos)
    {
        for (var i = 0; i < campos.Count; i++)
        {
            var campo = campos[i];
            if (campo is null) continue;

            if (campo.HasKids)
            {
                RecorrerCampos(campo.Fields, paginaPorObjectId, candidatos);
                continue;
            }

            if (string.IsNullOrEmpty(campo.Name)) continue;
            if (!paginaPorObjectId.TryGetValue(campo.Internals.ObjectID.ToString(), out var pagina)) continue;

            var rect = campo.Elements.GetRectangle("/Rect");
            if (rect.Width <= 0 || rect.Height <= 0) continue;

            candidatos.Add(new CampoAcroFormDetectado(
                NombreCampo: campo.Name,
                Pagina: pagina.Indice,
                X: rect.X1,
                Y: pagina.AlturaPagina - rect.Y2,
                Ancho: rect.Width,
                Alto: rect.Height));
        }
    }
}
