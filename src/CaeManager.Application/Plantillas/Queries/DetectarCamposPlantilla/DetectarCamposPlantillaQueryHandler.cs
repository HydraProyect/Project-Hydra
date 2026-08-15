using CaeManager.Application.Common;
using CaeManager.Application.DocumentosIa;
using CaeManager.Domain.Common;
using CaeManager.Domain.Plantillas;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CaeManager.Application.Plantillas.Queries.DetectarCamposPlantilla;

public class DetectarCamposPlantillaQueryHandler(
    IExtractorCamposAcroFormService extractorAcroForm,
    IDocumentAIRouterService router,
    IPlantillasQueryContext plantillasContext,
    IOptions<DeteccionPreviaDocumentoOptions> opciones)
    : IRequestHandler<DetectarCamposPlantillaQuery, Result<IReadOnlyList<PlantillaElementoCandidatoDto>>>
{
    private const string TipoEsperadoPlantilla = "formulario/plantilla PRL-CAE en blanco entregado por un centro (aún no hay datos de ningún trabajador)";

    private const double XPorDefecto = 50;
    private const double AnchoPorDefecto = 200;
    private const double AltoPorDefecto = 20;
    private const double YInicial = 50;
    private const double EspaciadoVertical = 30;

    /// <summary>Coincidencia exacta de etiqueta normalizada — el nombre de campo ya viene limpio de la propia estructura del PDF.</summary>
    private const int ConfianzaCoincidenciaExacta = 100;

    /// <summary>Coincidencia por subcadena (p. ej. "txtCIF" contiene "cif") — heurística, menos fiable que la exacta.</summary>
    private const int ConfianzaCoincidenciaParcial = 60;

    public async Task<Result<IReadOnlyList<PlantillaElementoCandidatoDto>>> Handle(
        DetectarCamposPlantillaQuery request, CancellationToken cancellationToken)
    {
        var conocimiento = await CargarConocimientoAsync(cancellationToken);

        return request.Formato switch
        {
            FormatoOrigenPlantilla.PdfConCampos =>
                Result.Exito(DetectarPorAcroForm(request.Contenido, conocimiento)),
            FormatoOrigenPlantilla.PdfVisual =>
                await DetectarPorTextoAsync(request.Contenido, conocimiento, cancellationToken),
            _ => Result.Fallo<IReadOnlyList<PlantillaElementoCandidatoDto>>(Error.Crear(
                "Plantilla.FormatoNoSoportadoParaDeteccion",
                $"La detección inicial no soporta el formato {request.Formato} (HtmlCss es exclusivo de plantillas TalvegPropio, sin detección — ADR-010 § 2.2).")),
        };
    }

    private async Task<IReadOnlyDictionary<string, FuenteDatoPlantilla>> CargarConocimientoAsync(CancellationToken cancellationToken)
    {
        var filas = await plantillasContext.ConocimientosDeteccionCampo
            .Select(c => new { c.EtiquetaNormalizada, c.FuenteDatoCandidata, c.Prioridad })
            .ToListAsync(cancellationToken);

        return filas
            .GroupBy(f => f.EtiquetaNormalizada)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(f => f.Prioridad).First().FuenteDatoCandidata);
    }

    private IReadOnlyList<PlantillaElementoCandidatoDto> DetectarPorAcroForm(
        byte[] contenido, IReadOnlyDictionary<string, FuenteDatoPlantilla> conocimiento)
    {
        var campos = extractorAcroForm.Extraer(contenido);

        return campos
            .Select(campo =>
            {
                var (fuente, confianza) = BuscarCoincidencia(NormalizadorEtiquetaCampo.Normalizar(campo.NombreCampo), conocimiento);
                return new PlantillaElementoCandidatoDto(
                    Tipo: TipoElementoPlantilla.Texto,
                    Pagina: campo.Pagina,
                    X: campo.X,
                    Y: campo.Y,
                    Ancho: campo.Ancho,
                    Alto: campo.Alto,
                    EtiquetaVisible: campo.NombreCampo,
                    NombreCampoAcroForm: campo.NombreCampo,
                    FuenteDatoSugerida: fuente,
                    Confianza: confianza);
            })
            .ToList();
    }

    /// <summary>
    /// Sin AcroForm no hay posición real que leer del PDF — el gestor la
    /// ajusta a mano en el editor visual (ADR-010 § 2.3); aquí solo se
    /// propone una columna por defecto, apilada, para no dejar los
    /// candidatos superpuestos en (0,0).
    /// </summary>
    private async Task<Result<IReadOnlyList<PlantillaElementoCandidatoDto>>> DetectarPorTextoAsync(
        byte[] contenido, IReadOnlyDictionary<string, FuenteDatoPlantilla> conocimiento, CancellationToken cancellationToken)
    {
        // Mismo kill switch que DetectarCamposDocumentoQuery (DeteccionPreviaDocumentoOptions):
        // esta rama también envía el PDF completo a un proveedor de IA
        // externo. Aquí es una plantilla en blanco, no un documento de un
        // trabajador concreto, pero el DPA que cubre ese envío es el mismo —
        // reutilizar el interruptor evita un segundo punto de control que
        // alguien podría dejar activo por error.
        if (!opciones.Value.Activa)
            return Result.Exito<IReadOnlyList<PlantillaElementoCandidatoDto>>([]);

        // Sin documentoId: una plantilla en blanco no es un Documento, nunca lo será
        // (ver el propio doc-comment de ProcesarAsync — ese enlace es para verificar
        // un Documento ya creado, VerificacionIaDocumentoService).
        var resultado = await router.ProcesarAsync(contenido, "plantilla.pdf", TipoEsperadoPlantilla, cancellationToken: cancellationToken);
        if (resultado.EsFallido)
            return Result.Fallo<IReadOnlyList<PlantillaElementoCandidatoDto>>(resultado.Error);

        var extraccion = resultado.Valor;
        var candidatos = extraccion.Campos
            .Select((par, indice) =>
            {
                var (fuente, confianzaCoincidencia) = BuscarCoincidencia(NormalizadorEtiquetaCampo.Normalizar(par.Key), conocimiento);
                var confianza = fuente is null ? 0 : Math.Min(confianzaCoincidencia, extraccion.ConfianzaGeneral);
                return new PlantillaElementoCandidatoDto(
                    Tipo: TipoElementoPlantilla.Texto,
                    Pagina: 1,
                    X: XPorDefecto,
                    Y: YInicial + indice * EspaciadoVertical,
                    Ancho: AnchoPorDefecto,
                    Alto: AltoPorDefecto,
                    EtiquetaVisible: par.Key,
                    NombreCampoAcroForm: null,
                    FuenteDatoSugerida: fuente,
                    Confianza: confianza);
            })
            .ToList();

        return Result.Exito<IReadOnlyList<PlantillaElementoCandidatoDto>>(candidatos);
    }

    private static (FuenteDatoPlantilla? Fuente, int Confianza) BuscarCoincidencia(
        string etiquetaNormalizada, IReadOnlyDictionary<string, FuenteDatoPlantilla> conocimiento)
    {
        if (etiquetaNormalizada.Length == 0)
            return (null, 0);

        if (conocimiento.TryGetValue(etiquetaNormalizada, out var exacta))
            return (exacta, ConfianzaCoincidenciaExacta);

        // Heurística para nombres de campo pegados sin separador ("txtCIF" → "txtcif" contiene "cif"):
        // se queda con la clave conocida más larga que aparece como subcadena, para preferir
        // la coincidencia más específica ("cif empresa" sobre "cif") cuando ambas encajan.
        var mejorParcial = conocimiento
            .Where(kv => etiquetaNormalizada.Contains(kv.Key, StringComparison.Ordinal))
            .OrderByDescending(kv => kv.Key.Length)
            .Select(kv => (FuenteDatoPlantilla?)kv.Value)
            .FirstOrDefault();

        return mejorParcial is null ? (null, 0) : (mejorParcial, ConfianzaCoincidenciaParcial);
    }
}
