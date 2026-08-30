using CaeManager.Application.Common;
using CaeManager.Domain.Plantillas;
using PdfSharp.Drawing;
using PdfSharp.Pdf.AcroForms;
using PdfSharp.Pdf.IO;

namespace CaeManager.Infrastructure.Plantillas;

/// <summary>
/// Dos motores de relleno, elegidos por <see cref="FormatoOrigenPlantilla"/>
/// (ADR-010 § E): AcroForm se rellena con la API tipada de PdfSharp
/// (<c>PdfDocument.AcroForm</c>, no usada hasta ahora en el repo — el resto
/// del código solo lee firmas por diccionario crudo, ver
/// <c>VerificadorFirmaPdfService</c>); PDF visual se estampa por posición
/// con el mismo patrón <c>XGraphics</c> que ya usan
/// <c>ConversorArchivosPdf</c>/<c>GeneradorPdfReporteDocumentos</c>/
/// <c>EstampadoFirmaEnCampoPdfService</c>, pero sobre las páginas
/// existentes del PDF original, no sobre una página nueva.
///
/// "DejaVu Sans" resuelve por el mismo motivo que en
/// <c>EstampadoFirmaEnCampoPdfService</c>: <c>GlobalFontSettings.FontResolver</c>
/// se fija una vez en CaeManager.Web/Program.cs.
/// </summary>
public class RellenadorPlantillaPdfService : IRellenadorPlantillaPdfService
{
    private const string NombreFuente = "DejaVu Sans";

    public byte[] Rellenar(byte[] pdfOriginal, FormatoOrigenPlantilla formato, IReadOnlyList<ElementoRellenoPlantilla> elementos) =>
        formato switch
        {
            FormatoOrigenPlantilla.PdfConCampos => RellenarAcroForm(pdfOriginal, elementos),
            FormatoOrigenPlantilla.PdfVisual => RellenarPorPosicion(pdfOriginal, elementos),
            _ => throw new NotSupportedException($"IRellenadorPlantillaPdfService no soporta el formato {formato}.")
        };

    private static byte[] RellenarAcroForm(byte[] pdfOriginal, IReadOnlyList<ElementoRellenoPlantilla> elementos)
    {
        using var flujo = new MemoryStream(pdfOriginal);
        using var documento = PdfReader.Open(flujo, PdfDocumentOpenMode.Modify);

        // documento.AcroForm lanza su propia InvalidOperationException si no
        // hay AcroForm en vez de devolver null (pese a estar anotado como
        // nullable) — el "?? throw" de abajo nunca llegaría a ejecutarse sin
        // esta comprobación previa contra el diccionario crudo del catálogo
        // (ver ExtractorCamposAcroFormService, mismo hallazgo).
        if (!documento.Internals.Catalog.Elements.ContainsKey("/AcroForm"))
            throw new InvalidOperationException("El PDF no tiene un AcroForm — no se puede rellenar por nombre de campo.");

        var acroForm = documento.AcroForm;

        var camposPorNombre = IndexarCampos(acroForm.Fields);
        var camposFaltantes = new List<string>();

        foreach (var elemento in elementos)
        {
            if (elemento.Tipo == TipoElementoPlantilla.Firma) continue;

            if (string.IsNullOrWhiteSpace(elemento.NombreCampoAcroForm)
                || !camposPorNombre.TryGetValue(elemento.NombreCampoAcroForm, out var campo))
            {
                camposFaltantes.Add(string.IsNullOrWhiteSpace(elemento.NombreCampoAcroForm)
                    ? "(elemento sin nombre de campo)" : elemento.NombreCampoAcroForm);
                continue;
            }

            if (campo is PdfTextField campoTexto)
                campoTexto.Text = elemento.Valor ?? string.Empty;
            else
                campo.Value = new PdfSharp.Pdf.PdfString(elemento.Valor ?? string.Empty);
        }

        // La confirmación de la plantilla ya cotejó estos nombres contra el
        // PDF real (ConfirmarPlantillaDocumentoVersionCommandHandler) — si
        // de todos modos falta alguno aquí (p. ej. una versión confirmada
        // antes de esa validación, sin re-validar retroactivamente), mejor
        // un fallo explícito que un documento con campos en blanco sin aviso.
        if (camposFaltantes.Count > 0)
            throw new CamposAcroFormFaltantesException(camposFaltantes);

        using var salida = new MemoryStream();
        documento.Save(salida);
        return salida.ToArray();
    }

    private static Dictionary<string, PdfAcroField> IndexarCampos(PdfAcroField.PdfAcroFieldCollection campos)
    {
        // PdfAcroFieldCollection.GetEnumerator() recorre los PdfReference sin
        // resolver del array /Fields — solo el indizador (campos[i]) los
        // resuelve a PdfAcroField, así que hay que recorrer por índice.
        var indice = new Dictionary<string, PdfAcroField>(StringComparer.Ordinal);

        void Recorrer(PdfAcroField.PdfAcroFieldCollection nivel)
        {
            for (var i = 0; i < nivel.Count; i++)
            {
                var campo = nivel[i];
                if (campo is null) continue;

                if (!string.IsNullOrEmpty(campo.Name))
                    indice[campo.Name] = campo;
                if (campo.HasKids)
                    Recorrer(campo.Fields);
            }
        }

        Recorrer(campos);
        return indice;
    }

    private static byte[] RellenarPorPosicion(byte[] pdfOriginal, IReadOnlyList<ElementoRellenoPlantilla> elementos)
    {
        using var flujo = new MemoryStream(pdfOriginal);
        using var documento = PdfReader.Open(flujo, PdfDocumentOpenMode.Modify);

        var fuente = new XFont(NombreFuente, 10, XFontStyleEx.Regular);

        foreach (var grupo in elementos.Where(e => e.Tipo != TipoElementoPlantilla.Firma).GroupBy(e => e.Pagina))
        {
            var indicePagina = grupo.Key - 1;
            if (indicePagina < 0 || indicePagina >= documento.PageCount) continue;

            var pagina = documento.Pages[indicePagina];
            using var graficos = XGraphics.FromPdfPage(pagina, XGraphicsPdfPageOptions.Append);

            foreach (var elemento in grupo)
                DibujarElemento(graficos, fuente, elemento);
        }

        using var salida = new MemoryStream();
        documento.Save(salida);
        return salida.ToArray();
    }

    private static void DibujarElemento(XGraphics graficos, XFont fuente, ElementoRellenoPlantilla elemento)
    {
        // La caja del editor visual (elemento.X/Y/Ancho/Alto) tiene su origen en la
        // esquina superior izquierda, igual que el div CSS que el usuario arrastra.
        // DrawString(texto, fuente, brocha, XPoint) ancla el punto en la LÍNEA BASE del
        // texto, no en su parte superior — pasar elemento.Y directamente dibujaba el
        // texto por encima de la caja. El overlay con XRect + XStringFormats.TopLeft sí
        // ancla al borde superior, igual que ve el usuario en el editor.
        var caja = new XRect(elemento.X, elemento.Y, elemento.Ancho, elemento.Alto);

        switch (elemento.Tipo)
        {
            case TipoElementoPlantilla.Imagen when elemento.ValorImagen is not null:
                using (var flujoImagen = new MemoryStream(elemento.ValorImagen))
                using (var xImagen = XImage.FromStream(flujoImagen))
                    graficos.DrawImage(xImagen, elemento.X, elemento.Y, elemento.Ancho, elemento.Alto);
                break;

            case TipoElementoPlantilla.Checkbox:
                if (EsValorAfirmativo(elemento.Valor))
                    graficos.DrawString("X", fuente, XBrushes.Black, caja, XStringFormats.TopLeft);
                break;

            default:
                if (!string.IsNullOrEmpty(elemento.Valor))
                    graficos.DrawString(elemento.Valor, fuente, XBrushes.Black, caja, XStringFormats.TopLeft);
                break;
        }
    }

    private static bool EsValorAfirmativo(string? valor) =>
        !string.IsNullOrWhiteSpace(valor)
        && !valor.Equals("false", StringComparison.OrdinalIgnoreCase)
        && !valor.Equals("no", StringComparison.OrdinalIgnoreCase);
}
