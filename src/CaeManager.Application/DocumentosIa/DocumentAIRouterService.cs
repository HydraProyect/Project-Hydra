using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using CaeManager.Application.Common;
using CaeManager.Application.DocumentosIa.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.DocumentosIa;
using Microsoft.Extensions.Logging;

namespace CaeManager.Application.DocumentosIa;

/// <summary>
/// Implementa los 4 casos de docs/ARQUITECTURA-IA-DOCUMENTAL.md § 2.3:
/// Digital → texto local, sin OCR (Caso 1); Escaneado/Imagen → OCR del
/// archivo completo (Casos 2-3); Mixto → texto digital de las páginas con
/// texto + OCR de las páginas escaneadas (Caso 4, con rasterización).
///
/// Antes de llamar a ningún proveedor comprueba la caché documental por
/// SHA256 (§ 3) — si ya se procesó exactamente este archivo, reutiliza el
/// resultado sin pagar de nuevo. Registra una <see cref="AuditoriaExtraccionIa"/>
/// en todos los casos (éxito, fallo o caché), nunca usada para decidir
/// enrutado — solo para poder ver qué se procesó, con qué proveedor,
/// cuánto tardó, cuánto costó y con qué confianza (§ 4.2).
/// </summary>
public class DocumentAIRouterService(
    IClasificadorDocumentoService clasificador,
    IExtractorTextoDigitalService extractorTextoDigital,
    ILocalizadorPaginasRelevantesService localizadorPaginas,
    IRasterizadorPaginasPdfService rasterizador,
    IDocumentAIProviderFactory proveedores,
    IExtraccionIaCacheRepository cacheRepositorio,
    IAuditoriaExtraccionIaRepository auditoriaRepositorio,
    IUnitOfWork unitOfWork,
    ILogger<DocumentAIRouterService> logger) : IDocumentAIRouterService
{
    /// <summary>Mismo umbral que VerificacionIaDocumentoService (Fase 38) — por debajo de esto, vale la pena un segundo intento si hay otro proveedor.</summary>
    private const int UmbralReintento = 70;

    /// <summary>Por debajo de esto no vale la pena el coste de localizar páginas — se manda el documento completo tal cual (ver § 4.3).</summary>
    private const int UmbralPaginasParaLocalizar = 15;

    private static readonly JsonSerializerOptions JsonOpciones = new(JsonSerializerDefaults.Web);

    /// <summary>Texto ya listo para estructurar, con nota de localización (si se descartaron páginas) y coste OCR (si se usó un proveedor de OCR).</summary>
    private sealed record TextoExtraidoDto(string Texto, string? NotaLocalizacion, decimal? CosteEstimadoOcr = null);

    public async Task<Result<ExtraccionEstructuradaDto>> ProcesarAsync(
        byte[] contenido, string nombreArchivo, string tipoEsperado, CancellationToken cancellationToken = default)
    {
        var cronometro = Stopwatch.StartNew();
        var hash = CalcularHash(contenido);

        var cacheado = await cacheRepositorio.ObtenerPorHashAsync(hash, cancellationToken);
        if (cacheado is not null)
        {
            var resultadoCache = DeserializarDesdeCache(cacheado.ExtraccionJson);
            if (resultadoCache is not null)
            {
                await RegistrarAuditoriaAsync(
                    hash, tipoEsperado, "cache", cronometro.ElapsedMilliseconds,
                    costeEstimadoOcr: null, costeEstimado: 0m,
                    numeroPaginas: 0, resultadoCache.ConfianzaGeneral, "Resultado servido desde caché documental.", cancellationToken);
                return Result.Exito(resultadoCache);
            }
        }

        var clasificacion = await clasificador.ClasificarAsync(contenido, nombreArchivo, cancellationToken);
        if (clasificacion.EsFallido)
        {
            await RegistrarAuditoriaAsync(
                hash, tipoEsperado, "ninguno", cronometro.ElapsedMilliseconds,
                costeEstimadoOcr: null, costeEstimado: null, 0, 0, clasificacion.Error.Mensaje, cancellationToken);
            return Result.Fallo<ExtraccionEstructuradaDto>(clasificacion.Error);
        }

        var texto = await ObtenerTextoAsync(clasificacion.Valor, contenido, nombreArchivo, cancellationToken);
        if (texto.EsFallido)
        {
            await RegistrarAuditoriaAsync(
                hash, tipoEsperado, "ninguno", cronometro.ElapsedMilliseconds,
                costeEstimadoOcr: null, costeEstimado: null, clasificacion.Valor.TotalPaginas, 0, texto.Error.Mensaje, cancellationToken);
            return Result.Fallo<ExtraccionEstructuradaDto>(texto.Error);
        }

        var proveedoresEstructuracion = proveedores.ObtenerPorCapacidad(CapacidadesProveedorIa.ExtraccionEstructurada);
        if (proveedoresEstructuracion.Count == 0)
        {
            const string mensaje = "No hay ningún proveedor de IA disponible para procesar este documento.";
            await RegistrarAuditoriaAsync(
                hash, tipoEsperado, "ninguno", cronometro.ElapsedMilliseconds,
                texto.Valor.CosteEstimadoOcr, costeEstimado: null, clasificacion.Valor.TotalPaginas, 0, mensaje, cancellationToken);
            return Result.Fallo<ExtraccionEstructuradaDto>(Error.Crear("DocumentAIRouter.SinProveedor", mensaje));
        }

        var proveedorUsado = proveedoresEstructuracion[0];
        var resultado = await proveedorUsado.ExtraerEstructuradoAsync(texto.Valor.Texto, tipoEsperado, cancellationToken);

        (resultado, proveedorUsado) = await ReintentarSiHaceFaltaAsync(resultado, proveedorUsado, proveedoresEstructuracion, texto.Valor.Texto, tipoEsperado, cancellationToken);

        if (resultado.EsFallido)
        {
            await RegistrarAuditoriaAsync(
                hash, tipoEsperado, "ninguno", cronometro.ElapsedMilliseconds,
                texto.Valor.CosteEstimadoOcr, costeEstimado: null, clasificacion.Valor.TotalPaginas, 0, resultado.Error.Mensaje, cancellationToken);
            return resultado;
        }

        await GuardarEnCacheAsync(hash, resultado.Valor, cancellationToken);
        var incidencias = CombinarIncidencias(texto.Valor.NotaLocalizacion, resultado.Valor.NotasValidacion);
        await RegistrarAuditoriaAsync(
            hash, tipoEsperado, proveedorUsado.Codigo, cronometro.ElapsedMilliseconds,
            texto.Valor.CosteEstimadoOcr, resultado.Valor.CosteEstimado,
            clasificacion.Valor.TotalPaginas, resultado.Valor.ConfianzaGeneral, incidencias, cancellationToken);

        return resultado;
    }

    private static string? CombinarIncidencias(string? notaLocalizacion, string? notasValidacion)
    {
        var partes = new[] { notaLocalizacion, notasValidacion }.Where(parte => !string.IsNullOrWhiteSpace(parte)).ToList();
        return partes.Count == 0 ? null : string.Join(" ", partes);
    }

    private async Task<(Result<ExtraccionEstructuradaDto> Resultado, IDocumentAIProvider Proveedor)> ReintentarSiHaceFaltaAsync(
        Result<ExtraccionEstructuradaDto> resultado, IDocumentAIProvider proveedorUsado, IReadOnlyList<IDocumentAIProvider> proveedoresEstructuracion,
        string texto, string tipoEsperado, CancellationToken cancellationToken)
    {
        if (resultado.EsFallido || resultado.Valor.ConfianzaGeneral >= UmbralReintento || proveedoresEstructuracion.Count < 2)
            return (resultado, proveedorUsado);

        var segundoProveedor = proveedoresEstructuracion[1];
        var segundoResultado = await segundoProveedor.ExtraerEstructuradoAsync(texto, tipoEsperado, cancellationToken);

        if (segundoResultado.EsFallido || segundoResultado.Valor.ConfianzaGeneral <= resultado.Valor.ConfianzaGeneral)
            return (resultado, proveedorUsado);

        logger.LogInformation(
            "Reintento con {Proveedor} mejoró la confianza de {ConfianzaAnterior}% a {ConfianzaNueva}%.",
            segundoProveedor.Codigo, resultado.Valor.ConfianzaGeneral, segundoResultado.Valor.ConfianzaGeneral);

        return (segundoResultado, segundoProveedor);
    }

    private async Task<Result<TextoExtraidoDto>> ObtenerTextoAsync(
        ClasificacionDocumentoDto clasificacion, byte[] contenido, string nombreArchivo, CancellationToken cancellationToken)
    {
        if (clasificacion.Tipo == TipoContenidoDocumento.Digital)
        {
            var paginas = extractorTextoDigital.ExtraerTextoPorPagina(contenido);
            if (paginas.EsFallido)
                return Result.Fallo<TextoExtraidoDto>(paginas.Error);

            return Result.Exito(ConstruirTextoDigital(paginas.Valor));
        }

        if (clasificacion.Tipo == TipoContenidoDocumento.Mixto)
            return await ObtenerTextoMixtoAsync(clasificacion, contenido, cancellationToken);

        // Escaneado / Imagen: el archivo completo va al proveedor de OCR.
        var proveedoresOcr = proveedores.ObtenerPorCapacidad(CapacidadesProveedorIa.OcrImagenAEscaneado);
        if (proveedoresOcr.Count == 0)
        {
            return Result.Fallo<TextoExtraidoDto>(Error.Crear(
                "DocumentAIRouter.SinProveedorOcr", "No hay ningún proveedor de OCR disponible para procesar este documento."));
        }

        var textoOcr = await proveedoresOcr[0].ExtraerTextoAsync(contenido, nombreArchivo, cancellationToken);
        if (textoOcr.EsFallido)
            return Result.Fallo<TextoExtraidoDto>(textoOcr.Error);

        return Result.Exito(new TextoExtraidoDto(textoOcr.Valor.Texto, null, textoOcr.Valor.CosteEstimado));
    }

    /// <summary>
    /// Caso 4 (Mixto): extrae texto de páginas digitales sin coste (OCR = 0)
    /// y rasteriza + hace OCR solo en las páginas escaneadas — evita pagar
    /// OCR por páginas que ya tienen texto embebido cuando el proveedor
    /// factura por página (Mistral OCR).
    /// </summary>
    private async Task<Result<TextoExtraidoDto>> ObtenerTextoMixtoAsync(
        ClasificacionDocumentoDto clasificacion, byte[] contenido, CancellationToken cancellationToken)
    {
        var textoPorPagina = extractorTextoDigital.ExtraerTextoPorPagina(contenido);
        if (textoPorPagina.EsFallido)
            return Result.Fallo<TextoExtraidoDto>(textoPorPagina.Error);

        var indicesEscaneadas = clasificacion.PaginasConTextoDigital
            .Select((esDigital, idx) => (esDigital, idx))
            .Where(p => !p.esDigital)
            .Select(p => p.idx)
            .ToList();

        if (indicesEscaneadas.Count == 0)
            return Result.Exito(ConstruirTextoDigital(textoPorPagina.Valor));

        var proveedoresOcr = proveedores.ObtenerPorCapacidad(CapacidadesProveedorIa.OcrImagenAEscaneado);
        if (proveedoresOcr.Count == 0)
        {
            return Result.Fallo<TextoExtraidoDto>(Error.Crear(
                "DocumentAIRouter.SinProveedorOcr", "No hay ningún proveedor de OCR disponible para procesar este documento."));
        }

        var imagenes = rasterizador.RasterizarPaginas(contenido, indicesEscaneadas);
        if (imagenes.EsFallido)
            return Result.Fallo<TextoExtraidoDto>(imagenes.Error);

        var proveedorOcr = proveedoresOcr[0];
        var textosPorIndicePagina = new Dictionary<int, string>(indicesEscaneadas.Count);
        decimal? costeOcr = null;

        for (var i = 0; i < indicesEscaneadas.Count; i++)
        {
            var nombrePagina = $"pagina-{indicesEscaneadas[i] + 1}.png";
            var ocr = await proveedorOcr.ExtraerTextoAsync(imagenes.Valor[i], nombrePagina, cancellationToken);
            if (ocr.EsFallido)
                return Result.Fallo<TextoExtraidoDto>(ocr.Error);

            textosPorIndicePagina[indicesEscaneadas[i]] = ocr.Valor.Texto;
            if (ocr.Valor.CosteEstimado.HasValue)
                costeOcr = (costeOcr ?? 0m) + ocr.Valor.CosteEstimado.Value;
        }

        var partes = Enumerable.Range(0, clasificacion.TotalPaginas)
            .Select(i => clasificacion.PaginasConTextoDigital[i]
                ? textoPorPagina.Valor[i]
                : textosPorIndicePagina[i]);

        return Result.Exito(new TextoExtraidoDto(string.Join("\n\n", partes), null, costeOcr));
    }

    /// <summary>Por debajo del umbral, se manda el documento completo — localizar páginas solo compensa en documentos grandes (§ 4.3).</summary>
    private TextoExtraidoDto ConstruirTextoDigital(IReadOnlyList<string> paginas)
    {
        if (paginas.Count <= UmbralPaginasParaLocalizar)
            return new TextoExtraidoDto(string.Join("\n\n", paginas), null);

        var indicesRelevantes = localizadorPaginas.Localizar(paginas);
        var texto = string.Join("\n\n", indicesRelevantes.Select(indice => paginas[indice]));
        var nota = $"Documento grande ({paginas.Count} páginas): se seleccionaron {indicesRelevantes.Count} páginas relevantes antes de enviar a IA.";

        return new TextoExtraidoDto(texto, nota);
    }

    private async Task GuardarEnCacheAsync(string hash, ExtraccionEstructuradaDto extraccion, CancellationToken cancellationToken)
    {
        if (await cacheRepositorio.ObtenerPorHashAsync(hash, cancellationToken) is not null)
            return; // ya cacheado (p. ej. por una llamada concurrente) — no duplicar.

        var json = JsonSerializer.Serialize(extraccion, JsonOpciones);
        cacheRepositorio.Agregar(ExtraccionIaCache.Crear(hash, json));
    }

    private async Task RegistrarAuditoriaAsync(
        string hash, string tipoEsperado, string proveedorCodigo, long tiempoMs, decimal? costeEstimadoOcr,
        decimal? costeEstimado, int numeroPaginas, int confianzaGeneral, string? incidencias, CancellationToken cancellationToken)
    {
        auditoriaRepositorio.Agregar(AuditoriaExtraccionIa.Crear(
            hash, tipoEsperado, proveedorCodigo, tiempoMs, costeEstimadoOcr, costeEstimado, numeroPaginas, confianzaGeneral, incidencias));
        await unitOfWork.SaveChangesAsync(cancellationToken);
    }

    private ExtraccionEstructuradaDto? DeserializarDesdeCache(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<ExtraccionEstructuradaDto>(json, JsonOpciones);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "No se pudo deserializar una entrada de la caché documental — se ignora y se reprocesa.");
            return null;
        }
    }

    private static string CalcularHash(byte[] contenido) => Convert.ToHexStringLower(SHA256.HashData(contenido));
}
