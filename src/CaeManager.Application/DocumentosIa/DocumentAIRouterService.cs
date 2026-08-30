using CaeManager.Application.Common;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using CaeManager.Application.DocumentosIa.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.DocumentosIa;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CaeManager.Application.DocumentosIa;

/// <summary>
/// Implementa los 4 casos de docs/ARQUITECTURA-IA-DOCUMENTAL.md § 2.3:
/// Digital → texto local, sin OCR (Caso 1); Escaneado/Imagen → OCR del
/// archivo completo (Casos 2-3); Mixto → texto digital de las páginas con
/// texto + OCR de las páginas escaneadas (Caso 4, con rasterización).
///
/// Antes de llamar a ningún proveedor comprueba la caché documental (§ 3) —
/// si ya se procesó este mismo archivo bajo el mismo tipo esperado y con la
/// misma versión del pipeline, reutiliza el resultado sin pagar de nuevo. La
/// clave incluye el tipo a propósito: lo que se guarda es una interpretación
/// del archivo, no una transcripción, y el tipo esperado entra en el prompt
/// (ver ExtraccionIaCache). Registra una <see cref="AuditoriaExtraccionIa"/>
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

    /// <summary>
    /// Máximo de páginas escaneadas que se envían a OCR de un mismo documento.
    ///
    /// El bucle de OCR del Caso Mixto es el único punto del pipeline donde el
    /// gasto crece linealmente con el tamaño del archivo: el proveedor factura
    /// por página, así que un PDF escaneado grande se traduce directamente en
    /// factura, y además en tantas rasterizaciones nativas como páginas. Sin
    /// tope, un solo documento podía consumir cuota de todos los tenants.
    ///
    /// Es una guarda de seguridad, no una política de producto: está puesto
    /// alto para que no moleste a los documentos reales (una póliza larga ronda
    /// las 280 páginas, pero llega como digital y pasa por el localizador, no
    /// por aquí) y solo dispare ante lo que no tiene sentido procesar sin que
    /// una persona lo mire antes. Cuando exista presupuesto por tenant, este
    /// número debería salir de ahí.
    /// </summary>
    private const int MaximoPaginasEscaneadasPorDocumento = 100;

    private static readonly JsonSerializerOptions JsonOpciones = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Texto ya listo para estructurar, con nota de localización (si se
    /// descartaron páginas), coste OCR (si se usó un proveedor de OCR) y el
    /// código de ese proveedor de OCR (para <see cref="AuditoriaExtraccionIa.ProveedoresInvocados"/>
    /// — solo se intenta uno por documento, nunca hay fallback en el paso de
    /// OCR, así que basta con un único código, no una lista).
    /// </summary>
    private sealed record TextoExtraidoDto(string Texto, string? NotaLocalizacion, decimal? CosteEstimadoOcr = null, string? ProveedorOcrCodigo = null);

    public async Task<Result<ExtraccionEstructuradaDto>> ProcesarAsync(
        byte[] contenido, string nombreArchivo, string tipoEsperado, Guid? documentoId = null, CancellationToken cancellationToken = default)
    {
        var cronometro = Stopwatch.StartNew();
        var hash = CalcularHash(contenido);

        var cacheado = await cacheRepositorio.ObtenerAsync(hash, tipoEsperado, cancellationToken);
        if (cacheado is not null)
        {
            var resultadoCache = DeserializarDesdeCache(cacheado.ExtraccionJson);
            if (resultadoCache is not null)
            {
                await RegistrarAuditoriaAsync(
                    hash, tipoEsperado, "cache", cronometro.ElapsedMilliseconds,
                    costeEstimadoOcr: null, costeEstimado: 0m,
                    numeroPaginas: 0, resultadoCache.ConfianzaGeneral, "Resultado servido desde caché documental.", documentoId, cancellationToken);
                return Result.Exito(resultadoCache);
            }
        }

        var clasificacion = await clasificador.ClasificarAsync(contenido, nombreArchivo, cancellationToken);
        if (clasificacion.EsFallido)
        {
            await RegistrarAuditoriaAsync(
                hash, tipoEsperado, "ninguno", cronometro.ElapsedMilliseconds,
                costeEstimadoOcr: null, costeEstimado: null, 0, 0, clasificacion.Error.Mensaje, documentoId, cancellationToken);
            return Result.Fallo<ExtraccionEstructuradaDto>(clasificacion.Error);
        }

        var texto = await ObtenerTextoAsync(clasificacion.Valor, contenido, nombreArchivo, cancellationToken);
        if (texto.EsFallido)
        {
            await RegistrarAuditoriaAsync(
                hash, tipoEsperado, "ninguno", cronometro.ElapsedMilliseconds,
                costeEstimadoOcr: null, costeEstimado: null, clasificacion.Valor.TotalPaginas, 0, texto.Error.Mensaje, documentoId, cancellationToken);
            return Result.Fallo<ExtraccionEstructuradaDto>(texto.Error);
        }

        var proveedoresEstructuracion = proveedores.ObtenerPorCapacidad(CapacidadesProveedorIa.ExtraccionEstructurada);
        if (proveedoresEstructuracion.Count == 0)
        {
            const string mensaje = "No hay ningún proveedor de IA disponible para procesar este documento.";
            await RegistrarAuditoriaAsync(
                hash, tipoEsperado, "ninguno", cronometro.ElapsedMilliseconds,
                texto.Valor.CosteEstimadoOcr, costeEstimado: null, clasificacion.Valor.TotalPaginas, 0, mensaje, documentoId, cancellationToken,
                proveedoresInvocados: CombinarProveedoresInvocados(texto.Valor.ProveedorOcrCodigo));
            return Result.Fallo<ExtraccionEstructuradaDto>(Error.Crear("DocumentAIRouter.SinProveedor", mensaje));
        }

        var estructuracion = await EstructurarAsync(
            proveedoresEstructuracion, texto.Valor.Texto, tipoEsperado, cancellationToken);
        var resultado = estructuracion.Resultado;

        if (resultado.EsFallido)
        {
            await RegistrarAuditoriaAsync(
                hash, tipoEsperado, "ninguno", cronometro.ElapsedMilliseconds,
                texto.Valor.CosteEstimadoOcr, estructuracion.CosteAcumulado, clasificacion.Valor.TotalPaginas, 0,
                CombinarIncidencias(resultado.Error.Mensaje, estructuracion.NotaIntentos), documentoId, cancellationToken,
                proveedoresInvocados: CombinarProveedoresInvocados(texto.Valor.ProveedorOcrCodigo, estructuracion.ProveedoresInvocados));
            return resultado;
        }

        var entradaCachePendiente = await GuardarEnCacheAsync(hash, tipoEsperado, resultado.Valor, cancellationToken);
        var incidencias = CombinarIncidencias(
            texto.Valor.NotaLocalizacion, resultado.Valor.NotasValidacion, estructuracion.NotaIntentos);
        await RegistrarAuditoriaAsync(
            hash, tipoEsperado, estructuracion.ProveedorCodigo, cronometro.ElapsedMilliseconds,
            texto.Valor.CosteEstimadoOcr, estructuracion.CosteAcumulado,
            clasificacion.Valor.TotalPaginas, resultado.Valor.ConfianzaGeneral, incidencias, documentoId, cancellationToken,
            entradaCachePendiente, estructuracion.ModeloExacto, estructuracion.RequestId,
            CombinarProveedoresInvocados(texto.Valor.ProveedorOcrCodigo, estructuracion.ProveedoresInvocados));

        return resultado;
    }

    private static string? CombinarIncidencias(params string?[] partes)
    {
        var utiles = partes.Where(parte => !string.IsNullOrWhiteSpace(parte)).ToList();
        return utiles.Count == 0 ? null : string.Join(" ", utiles);
    }

    /// <summary>
    /// Une el código del proveedor de OCR (si el texto vino de OCR) con los
    /// códigos de los candidatos de estructuración probados, sin duplicados
    /// — un mismo proveedor puede cubrir OCR y estructuración a la vez (p.
    /// ej. Anthropic) y aparecer en las dos partes.
    /// </summary>
    private static string? CombinarProveedoresInvocados(params string?[] partes)
    {
        var codigos = partes
            .Where(parte => !string.IsNullOrWhiteSpace(parte))
            .SelectMany(parte => parte!.Split(',', StringSplitOptions.RemoveEmptyEntries))
            .Distinct()
            .ToList();

        return codigos.Count == 0 ? null : string.Join(",", codigos);
    }

    /// <summary>
    /// Lo que dejó una vuelta completa por los candidatos de estructuración:
    /// quién ganó, cuánto costó TODO (no solo el ganador) y qué se intentó.
    /// <see cref="ModeloExacto"/>/<see cref="RequestId"/> son de la llamada
    /// GANADORA (la que produjo <see cref="Resultado"/>) — null si ninguna
    /// ganó. <see cref="ProveedoresInvocados"/> es la lista de códigos de
    /// TODOS los candidatos probados, ganaran o no — distinto de
    /// <see cref="NotaIntentos"/>, que es el mismo recorrido pero en texto
    /// legible con confianza/error de cada uno, pensado para
    /// <c>Incidencias</c>, no para consultar.
    /// </summary>
    private sealed record ResultadoEstructuracion(
        Result<ExtraccionEstructuradaDto> Resultado, string ProveedorCodigo, decimal? CosteAcumulado, string? NotaIntentos,
        string? ModeloExacto, string? RequestId, string? ProveedoresInvocados);

    /// <summary>
    /// Recorre los candidatos en el orden declarado por
    /// <see cref="IDocumentAIProviderFactory.ObtenerPorCapacidad"/> y se queda
    /// con el mejor resultado, con dos diferencias frente a lo que hacía antes.
    ///
    /// <b>Fallback ante fallo</b>: antes el segundo proveedor solo entraba si
    /// el primero devolvía <em>éxito</em> con confianza baja. Un timeout, un
    /// 429, una credencial ausente o un JSON inválido en el primero abortaban
    /// el análisis entero aunque hubiera alternativas configuradas y sanas —
    /// un solo proveedor mal configurado tumbaba la lectura IA de todos los
    /// tenants. Ahora un fallo pasa al siguiente candidato; solo se devuelve
    /// error cuando se agotan todos, y el que se devuelve es el del último
    /// intento.
    ///
    /// <b>El coste no se pierde</b>: antes, si el segundo modelo mejoraba al
    /// primero, la auditoría registraba solo el coste del ganador y el del
    /// descartado desaparecía — el gasto real quedaba infravalorado justo en
    /// los documentos que más llamadas consumen.
    /// <see cref="ResultadoEstructuracion.CosteAcumulado"/> suma todos los
    /// intentos que devolvieron coste.
    ///
    /// Lo que <b>no</b> cambia es cuándo se paga un segundo modelo: si el
    /// primero responde con confianza suficiente
    /// (<see cref="UmbralReintento"/>), se corta ahí y no se llama a nadie más.
    ///
    /// Sigue sin haber ninguna comprobación de si a este tenant se le pueden
    /// enviar estos datos a este proveedor: el fallback recorre la lista
    /// entera sin preguntar por región ni base jurídica. No amplía el conjunto
    /// de destinatarios respecto a lo que ya hacía el reintento por confianza
    /// baja — pero sí hace más probable llegar al segundo. Es deuda conocida,
    /// no un descuido.
    /// </summary>
    private async Task<ResultadoEstructuracion> EstructurarAsync(
        IReadOnlyList<IDocumentAIProvider> candidatos, string texto, string tipoEsperado, CancellationToken cancellationToken)
    {
        decimal? costeAcumulado = null;
        var intentos = new List<string>(candidatos.Count);
        var codigosIntentados = new List<string>(candidatos.Count);

        Result<ExtraccionEstructuradaDto>? mejor = null;
        var proveedorMejor = "ninguno";
        Result<ExtraccionEstructuradaDto>? ultimoFallo = null;

        foreach (var candidato in candidatos)
        {
            codigosIntentados.Add(candidato.Codigo);
            var actual = await candidato.ExtraerEstructuradoAsync(texto, tipoEsperado, cancellationToken);

            if (actual.EsFallido)
            {
                intentos.Add($"{candidato.Codigo}: {actual.Error.Codigo}");
                ultimoFallo = actual;
                continue;
            }

            if (actual.Valor.CosteEstimado is { } coste)
                costeAcumulado = (costeAcumulado ?? 0m) + coste;

            intentos.Add($"{candidato.Codigo}: {actual.Valor.ConfianzaGeneral}%");

            if (mejor is null || actual.Valor.ConfianzaGeneral > mejor.Valor.ConfianzaGeneral)
            {
                if (mejor is not null)
                {
                    logger.LogInformation(
                        "Reintento con {Proveedor} mejoró la confianza de {ConfianzaAnterior}% a {ConfianzaNueva}%.",
                        candidato.Codigo, mejor.Valor.ConfianzaGeneral, actual.Valor.ConfianzaGeneral);
                }

                mejor = actual;
                proveedorMejor = candidato.Codigo;
            }

            // Confianza suficiente: no se paga un modelo más solo por tenerlo.
            if (mejor.Valor.ConfianzaGeneral >= UmbralReintento)
                break;
        }

        var nota = intentos.Count > 1 ? $"Proveedores invocados: {string.Join(", ", intentos)}." : null;
        var proveedoresInvocados = codigosIntentados.Count > 0 ? string.Join(",", codigosIntentados) : null;

        if (mejor is { } ganador)
        {
            return new ResultadoEstructuracion(
                ganador, proveedorMejor, costeAcumulado, nota, ganador.Valor.ModeloExacto, ganador.Valor.RequestId, proveedoresInvocados);
        }

        var error = ultimoFallo ?? Result.Fallo<ExtraccionEstructuradaDto>(Error.Crear(
            "DocumentAIRouter.SinProveedor", "No hay ningún proveedor de IA disponible para procesar este documento."));

        return new ResultadoEstructuracion(error, "ninguno", costeAcumulado, nota, null, null, proveedoresInvocados);
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

        var proveedorOcr = proveedoresOcr[0];
        var textoOcr = await proveedorOcr.ExtraerTextoAsync(contenido, nombreArchivo, cancellationToken);
        if (textoOcr.EsFallido)
            return Result.Fallo<TextoExtraidoDto>(textoOcr.Error);

        return Result.Exito(new TextoExtraidoDto(textoOcr.Valor.Texto, null, textoOcr.Valor.CosteEstimado, proveedorOcr.Codigo));
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

        // Tope de páginas escaneadas por documento. Es a la vez una guarda de
        // coste (el proveedor de OCR factura por página, así que este bucle es
        // el único sitio del pipeline donde el gasto crece sin límite con el
        // tamaño del archivo) y de memoria. Se falla en vez de truncar a
        // propósito: recortar en silencio devolvería una extracción incompleta
        // presentada como completa, y las decisiones que cuelgan de ella —
        // aprobar un documento, dar de baja a un trabajador que "no aparece" —
        // se tomarían sobre un texto al que le faltan páginas sin que nadie lo
        // sepa.
        if (indicesEscaneadas.Count > MaximoPaginasEscaneadasPorDocumento)
        {
            return Result.Fallo<TextoExtraidoDto>(Error.Crear(
                "DocumentAIRouter.DemasiadasPaginasEscaneadas",
                $"El documento tiene {indicesEscaneadas.Count} páginas escaneadas, por encima del máximo de {MaximoPaginasEscaneadasPorDocumento} que se procesan automáticamente."));
        }

        var proveedorOcr = proveedoresOcr[0];
        var textosPorIndicePagina = new Dictionary<int, string>(indicesEscaneadas.Count);
        decimal? costeOcr = null;

        // Se rasteriza y se envía una página cada vez. Antes se rasterizaban
        // TODAS antes del primer OCR y la lista entera de PNG vivía en memoria
        // hasta terminar el documento; aquí cada imagen queda sin referencias en
        // cuanto se ha enviado, así que el pico deja de crecer con el número de
        // páginas.
        foreach (var indicePagina in indicesEscaneadas)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var imagen = rasterizador.RasterizarPagina(contenido, indicePagina, cancellationToken);
            if (imagen.EsFallido)
                return Result.Fallo<TextoExtraidoDto>(imagen.Error);

            var nombrePagina = $"pagina-{indicePagina + 1}.png";
            var ocr = await proveedorOcr.ExtraerTextoAsync(imagen.Valor, nombrePagina, cancellationToken);
            if (ocr.EsFallido)
                return Result.Fallo<TextoExtraidoDto>(ocr.Error);

            textosPorIndicePagina[indicePagina] = ocr.Valor.Texto;
            if (ocr.Valor.CosteEstimado.HasValue)
                costeOcr = (costeOcr ?? 0m) + ocr.Valor.CosteEstimado.Value;
        }

        var partes = Enumerable.Range(0, clasificacion.TotalPaginas)
            .Select(i => clasificacion.PaginasConTextoDigital[i]
                ? textoPorPagina.Valor[i]
                : textosPorIndicePagina[i]);

        return Result.Exito(new TextoExtraidoDto(string.Join("\n\n", partes), null, costeOcr, proveedorOcr.Codigo));
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

    /// <summary>
    /// Añade la entrada de caché al seguimiento del contexto sin guardarla
    /// todavía — se persiste junto con la auditoría en el mismo
    /// <c>SaveChangesAsync</c> de <see cref="RegistrarAuditoriaAsync"/>, no
    /// aquí. Devuelve la entidad recién añadida (o null si ya había una,
    /// p. ej. porque otra llamada concurrente terminó primero) para que el
    /// llamador pueda retirarla del tracker si el guardado conjunto choca
    /// contra el índice único — ver el comentario de
    /// <see cref="RegistrarAuditoriaAsync"/> sobre la carrera de caché.
    /// </summary>
    private async Task<ExtraccionIaCache?> GuardarEnCacheAsync(
        string hash, string tipoEsperado, ExtraccionEstructuradaDto extraccion, CancellationToken cancellationToken)
    {
        if (await cacheRepositorio.ObtenerAsync(hash, tipoEsperado, cancellationToken) is not null)
            return null; // ya cacheado (p. ej. por una llamada concurrente que ya guardó) — no duplicar.

        var json = JsonSerializer.Serialize(extraccion, JsonOpciones);
        var entrada = ExtraccionIaCache.Crear(hash, tipoEsperado, json);
        cacheRepositorio.Agregar(entrada);
        return entrada;
    }

    /// <summary>
    /// La comprobación de <see cref="GuardarEnCacheAsync"/> antes de
    /// <c>Agregar</c> reduce la carrera de caché documental (dos
    /// ejecuciones concurrentes procesando el mismo archivo bajo el mismo
    /// tipo esperado) pero no la cierra: entre ese SELECT y el
    /// <c>SaveChangesAsync</c> de aquí abajo, la otra ejecución puede
    /// colar su propio guardado, y entonces este choca contra el índice
    /// único de <see cref="ExtraccionIaCache"/>.
    ///
    /// Antes eso propagaba el <c>DbUpdateException</c> entero y con él se
    /// perdía la fila de <see cref="AuditoriaExtraccionIa"/> — una
    /// extracción que sí tuvo éxito (pagó al proveedor, obtuvo un
    /// resultado válido) quedaba sin rastro en la auditoría solo por
    /// haber perdido la carrera de escritura de caché, que es un detalle
    /// de optimización, no del resultado.
    ///
    /// La solución no es tragarse el error sin más: un <c>SaveChangesAsync</c>
    /// fallido no revierte el estado "Added" que <see cref="GuardarEnCacheAsync"/>
    /// dejó en el tracker, así que un reintento con la entidad todavía
    /// enganchada repetiría el mismo choque indefinidamente. Por eso se
    /// retira explícitamente con <see cref="IExtraccionIaCacheRepository.DescartarTrasConflicto"/>
    /// antes de reintentar — la auditoría se guarda igual, y el caché
    /// queda con la fila que escribió quien ganó la carrera, que es
    /// exactamente el mismo resultado que se hubiera devuelto desde este
    /// lado.
    /// </summary>
    private async Task RegistrarAuditoriaAsync(
        string hash, string tipoEsperado, string proveedorCodigo, long tiempoMs, decimal? costeEstimadoOcr,
        decimal? costeEstimado, int numeroPaginas, int confianzaGeneral, string? incidencias, Guid? documentoId,
        CancellationToken cancellationToken, ExtraccionIaCache? entradaCachePendiente = null,
        string? modeloExacto = null, string? requestId = null, string? proveedoresInvocados = null)
    {
        auditoriaRepositorio.Agregar(AuditoriaExtraccionIa.Crear(
            hash, tipoEsperado, proveedorCodigo, tiempoMs, costeEstimadoOcr, costeEstimado, numeroPaginas, confianzaGeneral, incidencias, documentoId,
            modeloExacto, requestId, proveedoresInvocados));

        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException) when (entradaCachePendiente is not null)
        {
            cacheRepositorio.DescartarTrasConflicto(entradaCachePendiente);
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
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
