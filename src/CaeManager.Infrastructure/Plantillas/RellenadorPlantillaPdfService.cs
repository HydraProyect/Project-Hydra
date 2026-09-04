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
///
/// Resolución de opciones (DEC-32, REC-115): radio y checkbox NO comparten una
/// única lista de valores afirmativos — cada uno tiene su propio contrato
/// (<see cref="ResolverCheckbox"/>/<see cref="SeleccionarOpcion"/>). En
/// checkbox, el estado «on» que el propio PDF declara manda sobre el contrato
/// documentado — salvo los negativos reservados (<see cref="ValoresNegativosCheckbox"/>),
/// que son un veto que ni el propio formulario puede anular. En radio, si el
/// grupo declara <c>/Opt</c> manda EN EXCLUSIVA (sin caer al cotejo por nombre
/// de estado si no hay coincidencia); si no lo declara, el nombre de estado
/// decide. Un valor que no encaja en ningún sitio no marca nada ni calla:
/// vuelve en <see cref="ResultadoRellenoPlantilla.ValoresNoReconocidos"/>.
/// </summary>
public class RellenadorPlantillaPdfService : IRellenadorPlantillaPdfService
{
    private const string NombreFuente = "DejaVu Sans";
    private const string ClaveNecesitaApariencias = "/NeedAppearances";
    private const string ClaveEstadoApariencia = "/AS";
    private const string ClaveValor = "/V";
    private const string EstadoApagado = "/Off";
    private const string ClaveOpciones = "/Opt";

    /// <summary>
    /// Contrato documentado de checkbox (DEC-32, REC-115): el estado «on» que
    /// el propio widget declara en <c>/AP /N</c> manda sobre este conjunto (solo
    /// existe para el motor AcroForm — el motor por posición no tiene widget).
    /// Comparación sin distinguir mayúsculas: un gestor escribe "Sí" o "sí" con
    /// el mismo significado.
    /// </summary>
    private static readonly HashSet<string> ValoresAfirmativosCheckbox =
        new(StringComparer.OrdinalIgnoreCase) { "true", "1", "si", "sí", "yes", "on" };

    /// <summary>Ver <see cref="ValoresAfirmativosCheckbox"/>. "0" y "Off" viven aquí — DEC-32 los rechaza explícitamente como afirmativos.</summary>
    private static readonly HashSet<string> ValoresNegativosCheckbox =
        new(StringComparer.OrdinalIgnoreCase) { "false", "0", "no", "off" };

    /// <summary>
    /// REC-186: pdfOriginal es <c>version.ArchivoOriginalUrl</c> —el mismo
    /// PDF de plantilla subido en ConfigurarPlantilla.razor.cs, ver el
    /// doc-comment gemelo en ExtractorCamposAcroFormService. Este es el
    /// sitio MÁS caro de los ocho de REC-186 (ambos motores abren en
    /// <see cref="PdfDocumentOpenMode.Modify"/>, 711-754 ms sobre 20 000
    /// páginas frente a 335-380 ms de Import, medido) y el único con dos
    /// llamadas a <c>PdfReader.Open</c> tras el mismo método público — se
    /// comprueba UNA vez aquí, antes del switch, para cubrir los dos
    /// motores con una sola guarda.
    /// </summary>
    private const int MaximoPaginasDocumento = 2000;

    public ResultadoRellenoPlantilla Rellenar(byte[] pdfOriginal, FormatoOrigenPlantilla formato, IReadOnlyList<ElementoRellenoPlantilla> elementos)
    {
        // ANTES de abrir con PdfReader (los dos motores, más abajo) — ver el
        // doc-comment de MaximoPaginasDocumento. Abstención (null) no cambia
        // nada: cada PdfReader.Open sigue siendo la red de seguridad para lo
        // que este pre-escaneo no cubre.
        if (LectorRecuentoPaginasPdfSinAbrir.IntentarLeerRecuentoDePaginasSinAbrir(pdfOriginal) is { } paginasDeclaradas &&
            paginasDeclaradas > MaximoPaginasDocumento)
        {
            throw new InvalidDataException(
                $"El PDF de la plantilla declara más de {MaximoPaginasDocumento} páginas y no se puede procesar.");
        }

        return formato switch
        {
            FormatoOrigenPlantilla.PdfConCampos => RellenarAcroForm(pdfOriginal, elementos),
            FormatoOrigenPlantilla.PdfVisual => RellenarPorPosicion(pdfOriginal, elementos),
            _ => throw new NotSupportedException($"IRellenadorPlantillaPdfService no soporta el formato {formato}.")
        };
    }

    private static ResultadoRellenoPlantilla RellenarAcroForm(byte[] pdfOriginal, IReadOnlyList<ElementoRellenoPlantilla> elementos)
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
        var avisos = new List<AvisoValorNoReconocido>();

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

            EscribirValor(campo, elemento, avisos);
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
        return new ResultadoRellenoPlantilla(salida.ToArray(), avisos);
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
    private static void EscribirValor(PdfAcroField campo, ElementoRellenoPlantilla elemento, List<AvisoValorNoReconocido> avisos)
    {
        switch (campo)
        {
            case PdfTextField campoTexto:
                campoTexto.Text = elemento.Valor ?? string.Empty;
                break;

            case PdfCheckBoxField casilla:
                MarcarCheckbox(casilla, elemento, avisos);
                break;

            case PdfRadioButtonField grupo:
                SeleccionarOpcion(grupo, elemento, avisos);
                break;

            default:
                campo.Value = new PdfString(elemento.Valor ?? string.Empty);
                break;
        }
    }

    /// <summary>
    /// Marca un solo hijo y apaga el resto, escribiendo <c>/AS</c> por widget y
    /// <c>/V</c> en el campo padre. Qué hijo se marca sale de <c>/Opt</c> si el
    /// grupo lo trae (ver <see cref="IndiceEnOpciones"/>), y si no del propio
    /// nombre de estado de <c>/AP /N</c>.
    ///
    /// DEC-32 (REC-115): un valor no vacío que no nombra ninguna opción del
    /// grupo no selecciona nada Y genera un <see cref="AvisoValorNoReconocido"/>
    /// — antes quedaba en silencio. Un valor vacío/solo-espacios sigue sin
    /// avisar: "no contestado" no es "contestado con una opción que este PDF
    /// no tiene", y DEC-5 (obligatorio vacío) ya cubre el primero en Application.
    /// </summary>
    private static void SeleccionarOpcion(PdfRadioButtonField grupo, ElementoRellenoPlantilla elemento, List<AvisoValorNoReconocido> avisos)
    {
        var valor = elemento.Valor;
        List<PdfDictionary> widgets = grupo.HasKids && grupo.Fields.Count > 0
            ? [.. Enumerable.Range(0, grupo.Fields.Count).Select(i => (PdfDictionary)grupo.Fields[i])]
            : [grupo];

        var (tieneOpt, indicePorOpciones) = IndiceEnOpciones(grupo, valor);

        var estadoElegido = EstadoApagado;
        var huboCoincidencia = false;
        for (var i = 0; i < widgets.Count; i++)
        {
            var encendidos = EstadosDeApariencia(widgets[i])
                .Where(e => !string.Equals(e, EstadoApagado, StringComparison.Ordinal))
                .ToList();

            // Con /Opt manda la posición EN EXCLUSIVA — hallazgo de revisión
            // adversarial (Codex, 2026-09-03): antes, un valor con /Opt que no
            // coincidía con ningún exportado caía al cotejo por nombre de
            // estado, y en un grupo cuyos estados se llaman literalmente /0,
            // /1… (el caso que /Opt existe justo para resolver) un valor como
            // "1" seleccionaba una opción arbitraria en silencio — justo lo que
            // DEC-32 prohíbe. Con /Opt declarado, "no coincide con ningún
            // exportado" ya no cae a nada: no selecciona, y avisa.
            var estado = tieneOpt
                ? (indicePorOpciones is { } indice && i == indice ? encendidos.FirstOrDefault() : null)
                : encendidos.FirstOrDefault(e => string.Equals(e[1..], valor, StringComparison.Ordinal));

            if (estado is not null)
            {
                estadoElegido = estado;
                huboCoincidencia = true;
            }
            widgets[i].Elements[ClaveEstadoApariencia] = new PdfName(estado ?? EstadoApagado);
        }

        grupo.Elements[ClaveValor] = new PdfName(estadoElegido);

        if (!huboCoincidencia && !string.IsNullOrWhiteSpace(valor))
            avisos.Add(new AvisoValorNoReconocido(elemento.ElementoId, valor, OpcionesDeRadio(grupo, widgets)));
    }

    /// <summary>
    /// Las opciones reales de un grupo de radio para el mensaje del aviso:
    /// los valores exportados de <c>/Opt</c> si el grupo los declara, y si no
    /// los propios nombres de estado de <c>/AP /N</c> (sin <c>/Off</c> ni la
    /// barra inicial) — el mismo criterio de prioridad que usa la resolución.
    /// </summary>
    private static IReadOnlyList<string> OpcionesDeRadio(PdfDictionary grupo, IReadOnlyList<PdfDictionary> widgets)
    {
        var opciones = grupo.Elements.GetArray(ClaveOpciones);
        if (opciones is not null)
        {
            var valoresExportados = new List<string>();
            for (var i = 0; i < opciones.Elements.Count; i++)
                if (opciones.Elements[i] is PdfString texto)
                    valoresExportados.Add(texto.Value);
            return valoresExportados;
        }

        return [.. widgets
            .SelectMany(EstadosDeApariencia)
            .Where(e => !string.Equals(e, EstadoApagado, StringComparison.Ordinal))
            .Select(e => e[1..])
            .Distinct(StringComparer.Ordinal)];
    }

    /// <summary>
    /// Contrato de checkbox (DEC-32, REC-115): los negativos reservados
    /// (<see cref="ValoresNegativosCheckbox"/>) son un veto que nada puede
    /// anular — ni el estado «on» del propio widget. Sin este veto por delante,
    /// un PDF cuyo estado «on» se llamase literalmente <c>/0</c> (nombre
    /// arbitrario, válido en la especificación) haría que la entrada
    /// <c>"0"</c> marcase la casilla — justo el criterio de aceptación 2 de
    /// DEC-32 ("0" y "Off" nunca marcan un checkbox), sin excepción. Hallazgo
    /// de revisión adversarial (Codex, 2026-09-03). Después del veto: el
    /// estado «on» real del widget manda sobre
    /// <see cref="ValoresAfirmativosCheckbox"/> — un valor vacío/solo-espacios
    /// se resuelve "no marcado" sin aviso (ver <see cref="SeleccionarOpcion"/>,
    /// mismo criterio para radio); cualquier otra cosa que no case con nada
    /// genera <see cref="AvisoValorNoReconocido"/> y se deja SIN marcar —
    /// nunca se interpreta a ciegas como afirmativo (el bug que cerró REC-115).
    /// </summary>
    private static void MarcarCheckbox(PdfCheckBoxField casilla, ElementoRellenoPlantilla elemento, List<AvisoValorNoReconocido> avisos)
    {
        var estadoOn = NombreEstadoOn(casilla);
        var marcado = ResolverCheckbox(elemento.Valor, estadoOn?[1..]);

        if (marcado is null)
        {
            avisos.Add(new AvisoValorNoReconocido(elemento.ElementoId, elemento.Valor, OpcionesDeCheckbox(estadoOn?[1..])));
            marcado = false;
        }

        casilla.Checked = marcado.Value;
    }

    /// <summary>
    /// <c>true</c>/<c>false</c> si el valor cae en el contrato (veto de
    /// negativos reservados, o estado «on» del widget si se conoce, o las
    /// listas documentadas); <c>null</c> si no cae en ninguno — "no
    /// reconocido", nunca "afirmativo por descarte" como hacía la heurística
    /// anterior.
    /// </summary>
    private static bool? ResolverCheckbox(string? valor, string? estadoOnSinBarra)
    {
        if (string.IsNullOrWhiteSpace(valor)) return false;

        var normalizado = valor.Trim();

        if (ValoresNegativosCheckbox.Contains(normalizado)) return false;

        if (estadoOnSinBarra is not null && string.Equals(normalizado, estadoOnSinBarra, StringComparison.OrdinalIgnoreCase))
            return true;

        if (ValoresAfirmativosCheckbox.Contains(normalizado)) return true;

        return null;
    }

    /// <summary>Opciones documentadas para el mensaje del aviso de checkbox — el estado propio del widget (si lo hay) más el contrato general.</summary>
    private static IReadOnlyList<string> OpcionesDeCheckbox(string? estadoOnSinBarra) =>
        estadoOnSinBarra is null
            ? [.. ValoresAfirmativosCheckbox, .. ValoresNegativosCheckbox]
            : [estadoOnSinBarra, .. ValoresAfirmativosCheckbox, .. ValoresNegativosCheckbox];

    /// <summary>El único nombre de estado de <c>/AP /N</c> distinto de <c>/Off</c> — lo que el widget considera "marcado". <c>FirstOrDefault</c>: un checkbox real solo declara un estado on; si declarase varios (malformado), se toma el primero.</summary>
    private static string? NombreEstadoOn(PdfDictionary widget) =>
        EstadosDeApariencia(widget).FirstOrDefault(e => !string.Equals(e, EstadoApagado, StringComparison.Ordinal));

    /// <summary>
    /// Posición del valor en <c>/Opt</c> (PDF 32000-1, tabla 231): el array de
    /// valores exportados de un grupo de radio, en el orden de <c>/Kids</c>.
    /// Existe justo para los PDF cuyos nombres de estado de <c>/AP /N</c> son
    /// índices (<c>/0</c>, <c>/1</c>…) o se repiten entre hijos; sin leerlo,
    /// ninguno de esos grupos casaría nunca y todos quedarían en <c>/Off</c>.
    ///
    /// Devuelve <c>(tieneOpt, índice)</c> — no basta un <c>int?</c>: "/Opt no
    /// existe" y "/Opt existe pero el valor no está en él" tenían el mismo
    /// <c>null</c>, y <see cref="SeleccionarOpcion"/> los trataba igual,
    /// cayendo al cotejo por nombre de estado en el segundo caso — el hallazgo
    /// de revisión adversarial que este tipo corrige.
    ///
    /// Solo entradas directas: un <c>/Opt</c> con referencias indirectas a
    /// cadenas no se resuelve aquí (limitación conocida, sin cambios en este
    /// incremento) — ese grupo declara <c>tieneOpt = true</c> pero ningún
    /// valor casará nunca por posición, así que con la separación de arriba
    /// termina avisando siempre en vez de acertar por casualidad vía el
    /// cotejo de nombres — más acorde con DEC-32 (nunca silencioso) que el
    /// comportamiento anterior, aunque más estricto para ese caso concreto.
    /// </summary>
    private static (bool TieneOpt, int? Indice) IndiceEnOpciones(PdfDictionary grupo, string? valor)
    {
        var opciones = grupo.Elements.GetArray(ClaveOpciones);
        if (opciones is null) return (false, null);
        if (string.IsNullOrEmpty(valor)) return (true, null);

        for (var i = 0; i < opciones.Elements.Count; i++)
            if (opciones.Elements[i] is PdfString texto && string.Equals(texto.Value, valor, StringComparison.Ordinal))
                return (true, i);

        return (true, null);
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

    private static ResultadoRellenoPlantilla RellenarPorPosicion(byte[] pdfOriginal, IReadOnlyList<ElementoRellenoPlantilla> elementos)
    {
        using var flujo = new MemoryStream(pdfOriginal);
        using var documento = PdfReader.Open(flujo, PdfDocumentOpenMode.Modify);

        var fuente = new XFont(NombreFuente, 10, XFontStyleEx.Regular);
        var avisos = new List<AvisoValorNoReconocido>();

        foreach (var grupo in elementos.Where(e => e.Tipo != TipoElementoPlantilla.Firma).GroupBy(e => e.Pagina))
        {
            var indicePagina = grupo.Key - 1;
            if (indicePagina < 0 || indicePagina >= documento.PageCount) continue;

            var pagina = documento.Pages[indicePagina];
            using var graficos = XGraphics.FromPdfPage(pagina, XGraphicsPdfPageOptions.Append);

            foreach (var elemento in grupo)
                DibujarElemento(graficos, fuente, elemento, avisos);
        }

        using var salida = new MemoryStream();
        documento.Save(salida);
        return new ResultadoRellenoPlantilla(salida.ToArray(), avisos);
    }

    private static void DibujarElemento(XGraphics graficos, XFont fuente, ElementoRellenoPlantilla elemento, List<AvisoValorNoReconocido> avisos)
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
                // Sin widget AcroForm aquí — no hay estado «on» que consultar,
                // solo el contrato documentado (mismo criterio que el motor
                // AcroForm, ver ResolverCheckbox).
                var marcado = ResolverCheckbox(elemento.Valor, estadoOnSinBarra: null);
                if (marcado is null)
                {
                    avisos.Add(new AvisoValorNoReconocido(elemento.ElementoId, elemento.Valor, OpcionesDeCheckbox(null)));
                    marcado = false;
                }
                if (marcado.Value)
                    graficos.DrawString("X", fuente, XBrushes.Black, caja, XStringFormats.TopLeft);
                break;

            default:
                if (!string.IsNullOrEmpty(elemento.Valor))
                    graficos.DrawString(elemento.Valor, fuente, XBrushes.Black, caja, XStringFormats.TopLeft);
                break;
        }
    }
}
