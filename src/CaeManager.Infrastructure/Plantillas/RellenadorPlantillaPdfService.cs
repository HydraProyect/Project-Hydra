using CaeManager.Application.Common;
using CaeManager.Domain.Plantillas;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
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
    private const string ClaveNecesitaApariencias = "/NeedAppearances";
    private const string ClaveEstadoApariencia = "/AS";
    private const string ClaveValor = "/V";
    private const string EstadoApagado = "/Off";

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

            EscribirValor(campo, elemento.Valor);
        }

        // La confirmación de la plantilla ya cotejó estos nombres contra el
        // PDF real (ConfirmarPlantillaDocumentoVersionCommandHandler) — si
        // de todos modos falta alguno aquí (p. ej. una versión confirmada
        // antes de esa validación, sin re-validar retroactivamente), mejor
        // un fallo explícito que un documento con campos en blanco sin aviso.
        if (camposFaltantes.Count > 0)
            throw new CamposAcroFormFaltantesException(camposFaltantes);

        // Red de seguridad, no el mecanismo principal (M4 § 3.2): /NeedAppearances
        // pide al visor que regenere las apariencias, pero hay visores que lo
        // ignoran — por eso EscribirValor ya deja /V y /AS coherentes por su cuenta.
        acroForm.Elements[ClaveNecesitaApariencias] = new PdfBoolean(true);

        using var salida = new MemoryStream();
        documento.Save(salida);
        return salida.ToArray();
    }

    /// <summary>
    /// Auditoría de seguridad del módulo, M4 § 3.2 (2026-08-31): antes, cualquier
    /// campo no textual recibía un <c>PdfString</c> genérico. Eso deja el valor
    /// escrito pero <c>/AS</c> intacto, así que un visor que no regenera
    /// apariencias dibuja la casilla sin marcar aunque el dato ya esté puesto.
    ///
    /// <c>PdfCheckBoxField.Checked</c> sí escribe <c>/V</c> y <c>/AS</c> con el
    /// nombre de estado real del PDF (el de <c>/AP /N</c>, p. ej. <c>/Yes</c>),
    /// que no tiene por qué ser el texto del valor resuelto.
    ///
    /// <c>PdfRadioButtonField.SelectedIndex</c> NO sirve: medido sobre PdfSharp
    /// 6.2.4, escribe <c>/V</c> como un nombre cuyo texto es la referencia
    /// indirecta del hijo (<c>/6 0 R</c>) y no toca el <c>/AS</c> de ninguno —
    /// el grupo queda sin marcar en cualquier visor. Por eso el grupo se
    /// resuelve aquí contra los estados declarados en <c>/AP /N</c>.
    /// </summary>
    private static void EscribirValor(PdfAcroField campo, string? valor)
    {
        switch (campo)
        {
            case PdfTextField campoTexto:
                campoTexto.Text = valor ?? string.Empty;
                break;

            case PdfCheckBoxField casilla:
                casilla.Checked = EsValorAfirmativo(valor);
                break;

            case PdfRadioButtonField grupo:
                SeleccionarOpcion(grupo, valor);
                break;

            default:
                campo.Value = new PdfString(valor ?? string.Empty);
                break;
        }
    }

    /// <summary>
    /// Marca el hijo cuyo <c>/AP /N</c> declara un estado con el nombre del
    /// valor resuelto, y apaga el resto. Un valor que no nombra ninguna opción
    /// del grupo deja el grupo sin marcar — igual que un valor vacío: aquí no
    /// se puede distinguir "no contestado" de "contestado con una opción que
    /// este PDF no tiene", y DEC-5 solo decide sobre el obligatorio vacío, que
    /// se comprueba en Application.
    /// </summary>
    private static void SeleccionarOpcion(PdfRadioButtonField grupo, string? valor)
    {
        var widgets = grupo.HasKids && grupo.Fields.Count > 0
            ? Enumerable.Range(0, grupo.Fields.Count).Select(i => (PdfDictionary)grupo.Fields[i]).ToList()
            : [grupo];

        var estadoElegido = EstadoApagado;
        foreach (var widget in widgets)
        {
            var estado = EstadosDeApariencia(widget)
                .FirstOrDefault(e => !string.Equals(e, EstadoApagado, StringComparison.Ordinal)
                    && string.Equals(e[1..], valor, StringComparison.Ordinal));

            if (estado is not null) estadoElegido = estado;
            widget.Elements[ClaveEstadoApariencia] = new PdfName(estado ?? EstadoApagado);
        }

        grupo.Elements[ClaveValor] = new PdfName(estadoElegido);
    }

    /// <summary>Nombres de estado (<c>/Off</c>, <c>/Yes</c>, <c>/Op1</c>…) que el widget declara en <c>/AP /N</c>.</summary>
    private static IEnumerable<string> EstadosDeApariencia(PdfDictionary widget) =>
        widget.Elements.GetDictionary("/AP")?.Elements.GetDictionary("/N")?.Elements.Keys ?? [];

    private static Dictionary<string, PdfAcroField> IndexarCampos(PdfAcroField.PdfAcroFieldCollection campos)
    {
        var indice = new Dictionary<string, PdfAcroField>(StringComparer.Ordinal);
        RecorridoCamposAcroFormSeguro.Recorrer(campos, campo =>
        {
            if (!string.IsNullOrEmpty(campo.Name))
                indice[campo.Name] = campo;
        });
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
