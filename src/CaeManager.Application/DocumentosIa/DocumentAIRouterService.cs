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
/// Digital → texto local, sin OCR (Caso 1); Escaneado/Imagen/Mixto → OCR
/// nativo del archivo completo antes de estructurar (Casos 2-4). Con un
/// único proveedor todo-en-uno registrado hoy (Anthropic), el caso Mixto
/// se resuelve enviando el documento completo a OCR igual que Escaneado —
/// el aprovechamiento fino de solo aplicar OCR a las páginas realmente
/// escaneadas (para ahorrar coste con un proveedor de OCR especializado
/// como Mistral) exige rasterizar páginas, deliberadamente fuera de
/// alcance de esta fase (ver § 4.3 del documento de arquitectura).
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
    IDocumentAIProviderFactory proveedores,
    IExtraccionIaCacheRepository cacheRepositorio,
    IAuditoriaExtraccionIaRepository auditoriaRepositorio,
    IUnitOfWork unitOfWork,
    ILogger<DocumentAIRouterService> logger) : IDocumentAIRouterService
{
    /// <summary>Mismo umbral que VerificacionIaDocumentoService (Fase 38) — por debajo de esto, vale la pena un segundo intento si hay otro proveedor.</summary>
    private const int UmbralReintento = 70;

    private static readonly JsonSerializerOptions JsonOpciones = new(JsonSerializerDefaults.Web);

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
                    hash, tipoEsperado, "cache", cronometro.ElapsedMilliseconds, costeEstimado: 0m,
                    numeroPaginas: 0, resultadoCache.ConfianzaGeneral, "Resultado servido desde caché documental.", cancellationToken);
                return Result.Exito(resultadoCache);
            }
        }

        var clasificacion = await clasificador.ClasificarAsync(contenido, nombreArchivo, cancellationToken);
        if (clasificacion.EsFallido)
        {
            await RegistrarAuditoriaAsync(
                hash, tipoEsperado, "ninguno", cronometro.ElapsedMilliseconds, null, 0, 0, clasificacion.Error.Mensaje, cancellationToken);
            return Result.Fallo<ExtraccionEstructuradaDto>(clasificacion.Error);
        }

        var texto = await ObtenerTextoAsync(clasificacion.Valor, contenido, nombreArchivo, cancellationToken);
        if (texto.EsFallido)
        {
            await RegistrarAuditoriaAsync(
                hash, tipoEsperado, "ninguno", cronometro.ElapsedMilliseconds, null, clasificacion.Valor.TotalPaginas, 0, texto.Error.Mensaje, cancellationToken);
            return Result.Fallo<ExtraccionEstructuradaDto>(texto.Error);
        }

        var proveedoresEstructuracion = proveedores.ObtenerPorCapacidad(CapacidadesProveedorIa.ExtraccionEstructurada);
        if (proveedoresEstructuracion.Count == 0)
        {
            const string mensaje = "No hay ningún proveedor de IA disponible para procesar este documento.";
            await RegistrarAuditoriaAsync(
                hash, tipoEsperado, "ninguno", cronometro.ElapsedMilliseconds, null, clasificacion.Valor.TotalPaginas, 0, mensaje, cancellationToken);
            return Result.Fallo<ExtraccionEstructuradaDto>(Error.Crear("DocumentAIRouter.SinProveedor", mensaje));
        }

        var proveedorUsado = proveedoresEstructuracion[0];
        var resultado = await proveedorUsado.ExtraerEstructuradoAsync(texto.Valor, tipoEsperado, cancellationToken);

        (resultado, proveedorUsado) = await ReintentarSiHaceFaltaAsync(resultado, proveedorUsado, proveedoresEstructuracion, texto.Valor, tipoEsperado, cancellationToken);

        if (resultado.EsFallido)
        {
            await RegistrarAuditoriaAsync(
                hash, tipoEsperado, "ninguno", cronometro.ElapsedMilliseconds, null, clasificacion.Valor.TotalPaginas, 0, resultado.Error.Mensaje, cancellationToken);
            return resultado;
        }

        await GuardarEnCacheAsync(hash, resultado.Valor, cancellationToken);
        await RegistrarAuditoriaAsync(
            hash, tipoEsperado, proveedorUsado.Codigo, cronometro.ElapsedMilliseconds, resultado.Valor.CosteEstimado,
            clasificacion.Valor.TotalPaginas, resultado.Valor.ConfianzaGeneral, resultado.Valor.NotasValidacion, cancellationToken);

        return resultado;
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

    private async Task GuardarEnCacheAsync(string hash, ExtraccionEstructuradaDto extraccion, CancellationToken cancellationToken)
    {
        if (await cacheRepositorio.ObtenerPorHashAsync(hash, cancellationToken) is not null)
            return; // ya cacheado (p. ej. por una llamada concurrente) — no duplicar.

        var json = JsonSerializer.Serialize(extraccion, JsonOpciones);
        cacheRepositorio.Agregar(ExtraccionIaCache.Crear(hash, json));
    }

    private async Task RegistrarAuditoriaAsync(
        string hash, string tipoEsperado, string proveedorCodigo, long tiempoMs, decimal? costeEstimado,
        int numeroPaginas, int confianzaGeneral, string? incidencias, CancellationToken cancellationToken)
    {
        auditoriaRepositorio.Agregar(AuditoriaExtraccionIa.Crear(
            hash, tipoEsperado, proveedorCodigo, tiempoMs, costeEstimado, numeroPaginas, confianzaGeneral, incidencias));
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
