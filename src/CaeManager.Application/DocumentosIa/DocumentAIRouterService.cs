using CaeManager.Application.Common;
using CaeManager.Application.DocumentosIa.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.DocumentosIa;
using Microsoft.Extensions.Logging;

namespace CaeManager.Application.DocumentosIa;

/// <summary>
/// Implementa los 4 casos de docs/ARQUITECTURA-IA-DOCUMENTAL.md § 2.3:
/// Digital → texto local, sin OCR (Caso 1); Escaneado/Imagen/Mixto → OCR
/// nativo del archivo completo antes de estructurar (Casos 2-4). Con un
/// único proveedor todo-en-uno registrado hoy (Anthropic), el caso Mixto
/// se resuelve enviando el documento completo a OCR igual que Escaneado —
/// el aprovechamiento fino de solo aplicar OCR a las páginas realmente
/// escaneadas (para ahorrar coste con un proveedor de OCR especializado
/// como Mistral) exige rasterizar páginas, deliberadamente fuera de
/// alcance de esta fase (ver § 4.3 del documento de arquitectura).
/// </summary>
public class DocumentAIRouterService(
    IClasificadorDocumentoService clasificador,
    IExtractorTextoDigitalService extractorTextoDigital,
    IDocumentAIProviderFactory proveedores,
    ILogger<DocumentAIRouterService> logger) : IDocumentAIRouterService
{
    /// <summary>Mismo umbral que VerificacionIaDocumentoService (Fase 38) — por debajo de esto, vale la pena un segundo intento si hay otro proveedor.</summary>
    private const int UmbralReintento = 70;

    public async Task<Result<ExtraccionEstructuradaDto>> ProcesarAsync(
        byte[] contenido, string nombreArchivo, string tipoEsperado, CancellationToken cancellationToken = default)
    {
        var clasificacion = await clasificador.ClasificarAsync(contenido, nombreArchivo, cancellationToken);
        if (clasificacion.EsFallido)
            return Result.Fallo<ExtraccionEstructuradaDto>(clasificacion.Error);

        var texto = await ObtenerTextoAsync(clasificacion.Valor, contenido, nombreArchivo, cancellationToken);
        if (texto.EsFallido)
            return Result.Fallo<ExtraccionEstructuradaDto>(texto.Error);

        var proveedoresEstructuracion = proveedores.ObtenerPorCapacidad(CapacidadesProveedorIa.ExtraccionEstructurada);
        if (proveedoresEstructuracion.Count == 0)
        {
            return Result.Fallo<ExtraccionEstructuradaDto>(Error.Crear(
                "DocumentAIRouter.SinProveedor", "No hay ningún proveedor de IA disponible para procesar este documento."));
        }

        var resultado = await proveedoresEstructuracion[0].ExtraerEstructuradoAsync(texto.Valor, tipoEsperado, cancellationToken);

        return await ReintentarSiHaceFaltaAsync(resultado, proveedoresEstructuracion, texto.Valor, tipoEsperado, cancellationToken);
    }

    private async Task<Result<ExtraccionEstructuradaDto>> ReintentarSiHaceFaltaAsync(
        Result<ExtraccionEstructuradaDto> resultado, IReadOnlyList<IDocumentAIProvider> proveedoresEstructuracion,
        string texto, string tipoEsperado, CancellationToken cancellationToken)
    {
        if (resultado.EsFallido || resultado.Valor.ConfianzaGeneral >= UmbralReintento || proveedoresEstructuracion.Count < 2)
            return resultado;

        var segundoProveedor = proveedoresEstructuracion[1];
        var segundoResultado = await segundoProveedor.ExtraerEstructuradoAsync(texto, tipoEsperado, cancellationToken);

        if (segundoResultado.EsFallido || segundoResultado.Valor.ConfianzaGeneral <= resultado.Valor.ConfianzaGeneral)
            return resultado;

        logger.LogInformation(
            "Reintento con {Proveedor} mejoró la confianza de {ConfianzaAnterior}% a {ConfianzaNueva}%.",
            segundoProveedor.Codigo, resultado.Valor.ConfianzaGeneral, segundoResultado.Valor.ConfianzaGeneral);

        return segundoResultado;
    }

    private async Task<Result<string>> ObtenerTextoAsync(
        ClasificacionDocumentoDto clasificacion, byte[] contenido, string nombreArchivo, CancellationToken cancellationToken)
    {
        if (clasificacion.Tipo == TipoContenidoDocumento.Digital)
            return extractorTextoDigital.ExtraerTexto(contenido);

        var proveedoresOcr = proveedores.ObtenerPorCapacidad(CapacidadesProveedorIa.OcrImagenAEscaneado);
        if (proveedoresOcr.Count == 0)
        {
            return Result.Fallo<string>(Error.Crear(
                "DocumentAIRouter.SinProveedorOcr", "No hay ningún proveedor de OCR disponible para procesar este documento."));
        }

        return await proveedoresOcr[0].ExtraerTextoAsync(contenido, nombreArchivo, cancellationToken);
    }
}
