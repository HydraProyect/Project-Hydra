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
/// Implementa los 4 casos de Project-Hydra-Negocio/tecnico/docs/ARQUITECTURA-IA-DOCUMENTAL.md § 2.3:
/// Digital → texto local, sin OCR (Caso 1); Escaneado/Imagen → OCR del
/// archivo completo (Casos 2-3); Mixto → texto digital de las páginas con
/// texto + OCR de las páginas escaneadas (Caso 4, con rasterización). Un PDF
/// escaneado de más de 4 páginas se lee por páginas (las tres primeras y la
/// última; el resto solo si falta lo mínimo para decidir) con un tope de 100
/// páginas por documento.
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
    /// El bucle de OCR por páginas (Caso Mixto y PDF escaneado de más de
    /// <see cref="PaginasDeLecturaCompleta"/> páginas) es el único punto del
    /// pipeline donde el gasto crece linealmente con el tamaño del archivo: el
    /// proveedor factura por página, así que un PDF escaneado grande se traduce
    /// directamente en factura, y además en tantas rasterizaciones nativas como
    /// páginas. Sin tope, un solo documento podía consumir cuota de todos los
    /// tenants (OWASP API4, consumo de recursos sin límite).
    ///
    /// Es una guarda de seguridad, no una política de producto: está puesto
    /// alto para que no moleste a los documentos reales (una póliza larga ronda
    /// las 280 páginas, pero llega como digital y pasa por el localizador, no
    /// por aquí) y solo dispare ante lo que no tiene sentido procesar sin que
    /// una persona lo mire antes. Cuando exista presupuesto por tenant, este
    /// número debería salir de ahí.
    ///
    /// En el Caso Mixto se rechaza el documento que lo supera; en el escaneado
    /// puro se lee hasta el tope y la nota de Incidencias deja constancia de las
    /// páginas leídas frente a las totales.
    /// </summary>
    private const int MaximoPaginasEscaneadasPorDocumento = 100;

    /// <summary>
    /// Un PDF escaneado de este número de páginas o menos se manda entero al
    /// OCR: el ahorro de leer menos páginas no compensa rasterizar una a una.
    /// </summary>
    private const int PaginasDeLecturaCompleta = 4;

    /// <summary>Páginas del principio que se leen primero en un PDF escaneado largo (además de la última).</summary>
    private const int PaginasInicialesEscaneado = 3;

    private static readonly JsonSerializerOptions JsonOpciones = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Texto ya listo para estructurar, con nota de localización (si se
    /// descartaron páginas), coste OCR (si se usó un proveedor de OCR) y el
    /// código de ese proveedor de OCR (para <see cref="AuditoriaExtraccionIa.ProveedoresInvocados"/>
    /// — solo se intenta uno por documento, nunca hay fallback en el paso de
    /// OCR, así que basta con un único código, no una lista).
    /// <see cref="PaginasLeidas"/> solo se informa cuando se leyó una parte del
    /// documento (escaneado largo): null significa «todas las páginas», y la
    /// auditoría usa entonces el total clasificado. <see cref="LecturaProgresiva"/>
    /// permite ampliar la lectura si la estructuración no encuentra lo mínimo.
    /// </summary>
    private sealed record TextoExtraidoDto(
        string Texto, string? NotaLocalizacion, decimal? CosteEstimadoOcr = null, string? ProveedorOcrCodigo = null, string? TextoCompleto = null,
        int? PaginasLeidas = null, LecturaEscaneadaPorPaginas? LecturaProgresiva = null);

    /// <summary>
    /// Estado de la lectura por páginas de un PDF escaneado largo: qué páginas
    /// llevan OCR, cuánto costaron y si todavía se puede leer más sin pasar del
    /// tope. Se crea por llamada a <see cref="ProcesarAsync"/>; no se comparte.
    /// </summary>
    private sealed class LecturaEscaneadaPorPaginas(byte[] contenido, IDocumentAIProvider proveedorOcr, int totalPaginas)
    {
        public byte[] Contenido { get; } = contenido;
        public IDocumentAIProvider ProveedorOcr { get; } = proveedorOcr;
        public int TotalPaginas { get; } = totalPaginas;
        public SortedDictionary<int, string> TextosPorPagina { get; } = new();
        public decimal? CosteOcr { get; set; }
        public bool Ampliada { get; set; }

        public bool PuedeAmpliar => !Ampliada
            && TextosPorPagina.Count < TotalPaginas
            && TextosPorPagina.Count < MaximoPaginasEscaneadasPorDocumento;
    }

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
                // Vínculo REC-036/DEC-34: un acierto de caché sobre un
                // Documento ya existente (VerificacionIaDocumentoService) es
                // precisamente el caso que dejaba la entrada sin vínculo si
                // se creó antes por un triage sin documentoId — ver el
                // comentario de ExtraccionIaCacheDocumento.
                var vinculoPendiente = documentoId is { } docIdCache
                    ? await cacheRepositorio.VincularDocumentoAsync(cacheado.Id, docIdCache, cancellationToken)
                    : null;

                await RegistrarAuditoriaAsync(
                    hash, tipoEsperado, "cache", cronometro.ElapsedMilliseconds,
                    costeEstimadoOcr: null, costeEstimado: 0m,
                    numeroPaginas: 0, resultadoCache.ConfianzaGeneral, "Resultado servido desde caché documental.", documentoId, cancellationToken,
                    vinculoPendiente: vinculoPendiente);
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
                texto.Valor.CosteEstimadoOcr, costeEstimado: null, texto.Valor.PaginasLeidas ?? clasificacion.Valor.TotalPaginas, 0, mensaje, documentoId, cancellationToken,
                proveedoresInvocados: CombinarProveedoresInvocados(texto.Valor.ProveedorOcrCodigo));
            return Result.Fallo<ExtraccionEstructuradaDto>(Error.Crear("DocumentAIRouter.SinProveedor", mensaje));
        }

        var textoFinal = texto.Valor;
        var estructuracion = await EstructurarAsync(
            proveedoresEstructuracion, textoFinal.Texto, tipoEsperado, cancellationToken);
        var resultado = ConContraste(estructuracion.Resultado, textoFinal);
        var ampliacionFallida = false;

        // PDF escaneado largo: se leyeron solo las primeras páginas y la última.
        // Si de ahí no salió lo mínimo para decidir, se amplía el OCR al resto
        // (hasta el tope por documento) y se estructura una segunda vez. Si no
        // falta nada, el resto del documento no se lee ni se paga.
        if (resultado.EsExitoso
            && textoFinal.LecturaProgresiva is { PuedeAmpliar: true } lectura
            && EvidenciaMinimaLectura.Faltan(resultado.Valor))
        {
            var textoInicial = textoFinal;
            var ampliado = await AmpliarLecturaEscaneadaAsync(lectura, cancellationToken);

            if (ampliado.EsExitoso)
            {
                var segunda = await EstructurarAsync(
                    proveedoresEstructuracion, ampliado.Valor.Texto, tipoEsperado, cancellationToken);
                var resultadoSegunda = ConContraste(segunda.Resultado, ampliado.Valor);

                // Gana la segunda solo si no trae menos que la primera: con más
                // texto el modelo puede dispersarse y perder un campo que sí tenía.
                var usarSegunda = resultadoSegunda.EsExitoso
                    && EvidenciaMinimaLectura.Faltantes(resultadoSegunda.Valor).Count <= EvidenciaMinimaLectura.Faltantes(resultado.Valor).Count;

                if (usarSegunda)
                {
                    textoFinal = ampliado.Valor;
                    resultado = resultadoSegunda;
                }
                else
                {
                    // La ampliación se pagó pero no mejoró (o no estructuró): se
                    // conserva la primera lectura, válida, y se deja constancia. Su
                    // coste OCR es el de todo lo pagado, aunque el texto sea el primero.
                    ampliacionFallida = resultadoSegunda.EsFallido;
                    textoFinal = textoInicial with
                    {
                        CosteEstimadoOcr = ampliado.Valor.CosteEstimadoOcr,
                        NotaLocalizacion = CombinarIncidencias(
                            textoInicial.NotaLocalizacion,
                            resultadoSegunda.EsFallido
                                ? "Se intentó ampliar la lectura a más páginas y no se pudo estructurar; se conserva la lectura de las primeras."
                                : "La lectura ampliada a más páginas no mejoró la de las primeras; se conserva esta."),
                    };
                }

                estructuracion = FusionarRondas(estructuracion, segunda, usarSegunda);
            }
            else
            {
                // El OCR del resto falló a medias: lo ya pagado cuenta en el coste.
                ampliacionFallida = true;
                textoFinal = textoInicial with
                {
                    CosteEstimadoOcr = lectura.CosteOcr,
                    NotaLocalizacion = CombinarIncidencias(
                        textoInicial.NotaLocalizacion, $"Se intentó ampliar la lectura a más páginas y falló el OCR ({ampliado.Error.Codigo}); se conserva la lectura de las primeras."),
                };
            }
        }

        var paginasLeidas = textoFinal.PaginasLeidas ?? clasificacion.Valor.TotalPaginas;

        if (resultado.EsFallido)
        {
            await RegistrarAuditoriaAsync(
                hash, tipoEsperado, "ninguno", cronometro.ElapsedMilliseconds,
                textoFinal.CosteEstimadoOcr, estructuracion.CosteAcumulado, paginasLeidas, 0,
                CombinarIncidencias(resultado.Error.Mensaje, estructuracion.NotaIntentos), documentoId, cancellationToken,
                proveedoresInvocados: CombinarProveedoresInvocados(textoFinal.ProveedorOcrCodigo, estructuracion.ProveedoresInvocados));
            return resultado;
        }

        var notasDelModelo = resultado.Valor.NotasValidacion;

        // Lectura parcial: lo que el revisor ve (NotasValidacion viaja con la
        // extracción) debe decir que hay páginas sin leer. El contraste
        // determinista (p. ej. un «no apto») solo mira el texto leído.
        if (textoFinal.PaginasLeidas is { } leidas && leidas < clasificacion.Valor.TotalPaginas)
        {
            resultado = Result.Exito(resultado.Valor with
            {
                NotasValidacion = CombinarIncidencias(
                    notasDelModelo,
                    $"{textoFinal.NotaLocalizacion} Un «no apto», una restricción o un dato que figure solo en las páginas no leídas no se habrá detectado."),
            });
        }

        // Una ampliación que falló por un error transitorio (OCR o estructuración)
        // no se cachea: la caché solo se invalida al cambiar la versión del
        // pipeline, y la lectura incompleta quedaría como definitiva.
        var (entradaCachePendiente, vinculoCreado) = ampliacionFallida
            ? ((ExtraccionIaCache?)null, (ExtraccionIaCacheDocumento?)null)
            : await GuardarEnCacheAsync(hash, tipoEsperado, resultado.Valor, documentoId, cancellationToken);
        var incidencias = CombinarIncidencias(
            textoFinal.NotaLocalizacion, notasDelModelo, estructuracion.NotaIntentos);
        await RegistrarAuditoriaAsync(
            hash, tipoEsperado, estructuracion.ProveedorCodigo, cronometro.ElapsedMilliseconds,
            textoFinal.CosteEstimadoOcr, estructuracion.CosteAcumulado,
            paginasLeidas, resultado.Valor.ConfianzaGeneral, incidencias, documentoId, cancellationToken,
            entradaCachePendiente, estructuracion.ModeloExacto, estructuracion.RequestId,
            CombinarProveedoresInvocados(textoFinal.ProveedorOcrCodigo, estructuracion.ProveedoresInvocados),
            vinculoCreado);

        return resultado;
    }

    /// <summary>
    /// El contraste determinista se calcula sobre el mismo texto que vio el
    /// modelo y viaja con la extracción, también a la caché: quien decide
    /// con ella no tiene que fiarse solo de lo que el modelo diga del texto.
    /// Si el contraste no se puede calcular queda null, que para decidir
    /// cuenta como «no contrastado»: nunca tumba una lectura ya pagada.
    /// </summary>
    private static Result<ExtraccionEstructuradaDto> ConContraste(Result<ExtraccionEstructuradaDto> estructurada, TextoExtraidoDto texto) =>
        estructurada.EsExitoso
            ? Result.Exito(estructurada.Valor with
            {
                Contraste = ContrasteTextoDocumento.AnalizarSinFallar(texto.Texto, texto.TextoCompleto),
            })
            : estructurada;

    /// <summary>
    /// Une las dos vueltas de estructuración de un escaneado largo: el coste, los
    /// intentos y los proveedores de ambas, con el ganador de la segunda solo si se usa.
    /// </summary>
    private static ResultadoEstructuracion FusionarRondas(ResultadoEstructuracion primera, ResultadoEstructuracion segunda, bool usarSegunda)
    {
        decimal? coste = primera.CosteAcumulado is null && segunda.CosteAcumulado is null
            ? null
            : (primera.CosteAcumulado ?? 0m) + (segunda.CosteAcumulado ?? 0m);
        var baseResultado = usarSegunda ? segunda : primera;

        return baseResultado with
        {
            CosteAcumulado = coste,
            NotaIntentos = CombinarIncidencias(primera.NotaIntentos, segunda.NotaIntentos),
            ProveedoresInvocados = CombinarProveedoresInvocados(primera.ProveedoresInvocados, segunda.ProveedoresInvocados),
        };
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
    /// Con el proveedor único (Anthropic, decisión del 2026-10-29) la lista de
    /// candidatos reales tiene un solo elemento, así que ni el fallback ni el
    /// reintento por confianza llaman a otro proveedor: un fallo de Anthropic se
    /// propaga. El mecanismo se conserva porque es general; que no haya un
    /// segundo proveedor lo fija <c>ProveedorDocumentalUnicoTests</c>.
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
                // DEUDA CONOCIDA, no descuido: si el proveedor ya facturó la
                // llamada antes de fallar (p. ej. Anthropic respondió y
                // cobró, pero el JSON no deserializó — ver el comentario de
                // AnthropicDocumentAIProvider.ParsearEstructurado), ese coste
                // no llega hasta aquí. Result<ExtraccionEstructuradaDto> no
                // tiene forma de llevarlo en el camino de fallo sin ampliar
                // el contrato compartido IDocumentAIProvider (o el Error de
                // Domain/Common, ~130 sitios de uso) — desproporcionado para
                // este hallazgo (P3). Mismo hueco en los tres proveedores.
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

        // Escaneado / Imagen: el archivo completo va al proveedor de OCR, salvo
        // un PDF escaneado largo, que se lee por páginas (ver más abajo).
        var proveedoresOcr = proveedores.ObtenerPorCapacidad(CapacidadesProveedorIa.OcrImagenAEscaneado);
        if (proveedoresOcr.Count == 0)
        {
            return Result.Fallo<TextoExtraidoDto>(Error.Crear(
                "DocumentAIRouter.SinProveedorOcr", "No hay ningún proveedor de OCR disponible para procesar este documento."));
        }

        var proveedorOcr = proveedoresOcr[0];

        if (clasificacion.Tipo == TipoContenidoDocumento.Escaneado && clasificacion.TotalPaginas > PaginasDeLecturaCompleta)
            return await ObtenerTextoEscaneadoLargoAsync(clasificacion, contenido, proveedorOcr, cancellationToken);

        var textoOcr = await proveedorOcr.ExtraerTextoAsync(contenido, nombreArchivo, cancellationToken);
        if (textoOcr.EsFallido)
            return Result.Fallo<TextoExtraidoDto>(textoOcr.Error);

        return Result.Exito(new TextoExtraidoDto(textoOcr.Valor.Texto, null, textoOcr.Valor.CosteEstimado, proveedorOcr.Codigo));
    }

    /// <summary>
    /// PDF escaneado de más de <see cref="PaginasDeLecturaCompleta"/> páginas:
    /// OCR solo de las páginas 1, 2, 3 y la última (portada, datos de emisión y
    /// firma suelen estar ahí). Si tras estructurar falta lo mínimo
    /// (<see cref="EvidenciaMinimaLectura"/>), <see cref="ProcesarAsync"/> pide
    /// ampliar con <see cref="AmpliarLecturaEscaneadaAsync"/>. A diferencia del
    /// Caso Mixto, un documento por encima del tope no se rechaza: se lee hasta
    /// <see cref="MaximoPaginasEscaneadasPorDocumento"/> y la nota de Incidencias
    /// dice cuántas páginas se leyeron de cuántas.
    /// </summary>
    private async Task<Result<TextoExtraidoDto>> ObtenerTextoEscaneadoLargoAsync(
        ClasificacionDocumentoDto clasificacion, byte[] contenido, IDocumentAIProvider proveedorOcr, CancellationToken cancellationToken)
    {
        var total = clasificacion.TotalPaginas;
        var lectura = new LecturaEscaneadaPorPaginas(contenido, proveedorOcr, total);

        var iniciales = Enumerable.Range(0, PaginasInicialesEscaneado).Append(total - 1).Distinct().ToList();
        var leidas = new SortedDictionary<int, string>();
        var lecturaInicial = await LeerPaginasAsync(lectura, iniciales, leidas, cancellationToken);
        if (lecturaInicial.EsFallido)
            return Result.Fallo<TextoExtraidoDto>(lecturaInicial.Error);

        foreach (var (indice, texto) in leidas)
            lectura.TextosPorPagina[indice] = texto;

        return Result.Exito(ConstruirTextoEscaneadoLargo(lectura));
    }

    /// <summary>
    /// Lee el resto de páginas en orden hasta completar
    /// <see cref="MaximoPaginasEscaneadasPorDocumento"/> en total. Las páginas se
    /// incorporan a la lectura solo si todas se leen bien; el coste de las que
    /// llegaron a pagarse queda en <see cref="LecturaEscaneadaPorPaginas.CosteOcr"/>
    /// en cualquier caso.
    /// </summary>
    private async Task<Result<TextoExtraidoDto>> AmpliarLecturaEscaneadaAsync(
        LecturaEscaneadaPorPaginas lectura, CancellationToken cancellationToken)
    {
        lectura.Ampliada = true;

        var presupuesto = MaximoPaginasEscaneadasPorDocumento - lectura.TextosPorPagina.Count;
        var pendientes = Enumerable.Range(0, lectura.TotalPaginas)
            .Where(i => !lectura.TextosPorPagina.ContainsKey(i))
            .Take(presupuesto)
            .ToList();

        var nuevas = new SortedDictionary<int, string>();
        var ampliacion = await LeerPaginasAsync(lectura, pendientes, nuevas, cancellationToken);
        if (ampliacion.EsFallido)
            return Result.Fallo<TextoExtraidoDto>(ampliacion.Error);

        foreach (var (indice, texto) in nuevas)
            lectura.TextosPorPagina[indice] = texto;

        return Result.Exito(ConstruirTextoEscaneadoLargo(lectura));
    }

    /// <summary>
    /// Rasteriza y envía a OCR una página cada vez (misma razón que en el Caso
    /// Mixto: el pico de memoria no debe crecer con el número de páginas). El
    /// coste de cada página leída se suma a <see cref="LecturaEscaneadaPorPaginas.CosteOcr"/>
    /// en el momento, de modo que un fallo a medias no lo pierde.
    /// </summary>
    private async Task<Result> LeerPaginasAsync(
        LecturaEscaneadaPorPaginas lectura, IReadOnlyList<int> indices, SortedDictionary<int, string> destino, CancellationToken cancellationToken)
    {
        foreach (var indicePagina in indices)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var imagen = rasterizador.RasterizarPagina(lectura.Contenido, indicePagina, cancellationToken);
            if (imagen.EsFallido)
                return Result.Fallo(imagen.Error);

            var ocr = await lectura.ProveedorOcr.ExtraerTextoAsync(imagen.Valor, $"pagina-{indicePagina + 1}.png", cancellationToken);
            if (ocr.EsFallido)
                return Result.Fallo(ocr.Error);

            destino[indicePagina] = ocr.Valor.Texto;
            if (ocr.Valor.CosteEstimado is { } coste)
                lectura.CosteOcr = (lectura.CosteOcr ?? 0m) + coste;
        }

        return Result.Exito();
    }

    private static TextoExtraidoDto ConstruirTextoEscaneadoLargo(LecturaEscaneadaPorPaginas lectura)
    {
        var leidas = lectura.TextosPorPagina.Count;
        var nota = $"Documento escaneado de {lectura.TotalPaginas} páginas: se leyeron {leidas} páginas de {lectura.TotalPaginas}.";

        if (leidas < lectura.TotalPaginas)
        {
            nota += lectura.Ampliada && leidas >= MaximoPaginasEscaneadasPorDocumento
                ? $" Se alcanzó el máximo de {MaximoPaginasEscaneadasPorDocumento} páginas por documento; el resto del documento no entró en la lectura."
                : " El resto del documento no entró en la lectura.";
        }

        return new TextoExtraidoDto(
            string.Join("\n\n", lectura.TextosPorPagina.Values), nota, lectura.CosteOcr, lectura.ProveedorOcr.Codigo,
            PaginasLeidas: leidas, LecturaProgresiva: lectura);
    }

    /// <summary>
    /// Caso 4 (Mixto): extrae texto de páginas digitales sin coste (OCR = 0)
    /// y rasteriza + hace OCR solo en las páginas escaneadas — evita pagar
    /// OCR por páginas que ya tienen texto embebido.
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
        // sepa. (El escaneado puro largo sí se lee hasta el tope sin fallar, por
        // decisión de producto del 2026-10-29, pero lo declara en la nota de
        // Incidencias y en NotasValidacion; ver ObtenerTextoEscaneadoLargoAsync.)
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

        // El contraste determinista mira todas las páginas, no solo las que se
        // envían: un «no apto» en una página descartada no puede pasar sin verse.
        return new TextoExtraidoDto(texto, nota, TextoCompleto: string.Join("\n\n", paginas));
    }

    /// <summary>
    /// Añade la entrada de caché al seguimiento del contexto sin guardarla
    /// todavía — se persiste junto con la auditoría en el mismo
    /// <c>SaveChangesAsync</c> de <see cref="RegistrarAuditoriaAsync"/>, no
    /// aquí. Devuelve la entrada recién añadida (o null si ya había una,
    /// p. ej. porque otra llamada concurrente terminó primero) para que el
    /// llamador pueda retirarla del tracker si el guardado conjunto choca
    /// contra el índice único — ver el comentario de
    /// <see cref="RegistrarAuditoriaAsync"/> sobre la carrera de caché.
    ///
    /// Cuando <paramref name="documentoId"/> no es null (REC-036/DEC-34,
    /// ver <see cref="ExtraccionIaCacheDocumento"/>), asegura también el
    /// vínculo con ese Documento — tanto si la entrada es nueva como si ya
    /// existía (el caso que dejaba huérfano un acierto de caché sobre un
    /// Documento distinto del que la creó). El vínculo pendiente se
    /// devuelve aparte para que <see cref="RegistrarAuditoriaAsync"/> pueda
    /// retirarlo igual que la entrada si el guardado conjunto choca.
    /// </summary>
    private async Task<(ExtraccionIaCache? Cache, ExtraccionIaCacheDocumento? Vinculo)> GuardarEnCacheAsync(
        string hash, string tipoEsperado, ExtraccionEstructuradaDto extraccion, Guid? documentoId, CancellationToken cancellationToken)
    {
        var existente = await cacheRepositorio.ObtenerIdAsync(hash, tipoEsperado, cancellationToken);
        if (existente is not null)
        {
            // Ya cacheado (p. ej. por una llamada concurrente que ya guardó)
            // — no duplicar el JSON, pero sí asegurar el vínculo de este
            // Documento con la entrada que ganó la carrera.
            var vinculoSobreExistente = documentoId is { } docIdExistente
                ? await cacheRepositorio.VincularDocumentoAsync(existente.Value, docIdExistente, cancellationToken)
                : null;
            return (null, vinculoSobreExistente);
        }

        var json = JsonSerializer.Serialize(extraccion, JsonOpciones);
        var entrada = ExtraccionIaCache.Crear(hash, tipoEsperado, json);
        cacheRepositorio.Agregar(entrada);

        var vinculo = documentoId is { } docId
            ? await cacheRepositorio.VincularDocumentoAsync(entrada.Id, docId, cancellationToken)
            : null;

        return (entrada, vinculo);
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
    ///
    /// <paramref name="vinculoPendiente"/> (REC-036/DEC-34) es la misma
    /// historia sobre el índice único de <see cref="ExtraccionIaCacheDocumento"/>:
    /// si el SaveChangesAsync conjunto choca, también hay que retirarlo con
    /// <see cref="IExtraccionIaCacheRepository.DescartarVinculoTrasConflicto"/>
    /// antes de reintentar, o el mismo choque se repetiría.
    ///
    /// <b>Pero cuando lo que chocó es la propia ENTRADA de caché</b> (no solo
    /// el vínculo) — dos Documentos distintos procesando el mismo archivo
    /// bajo el mismo tipo esperado a la vez, el mismo escenario del § 6 del
    /// handoff HO-036-01 — descartarla no basta: <paramref name="documentoId"/>
    /// quedaría sin vínculo, porque nuestro <c>vinculoPendiente</c> apuntaba
    /// al Id de nuestra propia entrada, que nunca llegó a existir en base de
    /// datos (perdió la carrera). Hay que releer la entrada GANADORA (por la
    /// misma clave hash+tipo+versión) y vincular a esa — hallazgo de revisión
    /// Codex sobre este mismo cambio: sin esto, ambos <c>ProcesarAsync</c>
    /// devuelven éxito pero solo uno de los dos Documentos queda enlazado.
    /// </summary>
    private async Task RegistrarAuditoriaAsync(
        string hash, string tipoEsperado, string proveedorCodigo, long tiempoMs, decimal? costeEstimadoOcr,
        decimal? costeEstimado, int numeroPaginas, int confianzaGeneral, string? incidencias, Guid? documentoId,
        CancellationToken cancellationToken, ExtraccionIaCache? entradaCachePendiente = null,
        string? modeloExacto = null, string? requestId = null, string? proveedoresInvocados = null,
        ExtraccionIaCacheDocumento? vinculoPendiente = null)
    {
        auditoriaRepositorio.Agregar(AuditoriaExtraccionIa.Crear(
            hash, tipoEsperado, proveedorCodigo, tiempoMs, costeEstimadoOcr, costeEstimado, numeroPaginas, confianzaGeneral, incidencias, documentoId,
            modeloExacto, requestId, proveedoresInvocados));

        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException) when (entradaCachePendiente is not null || vinculoPendiente is not null)
        {
            if (entradaCachePendiente is not null)
                cacheRepositorio.DescartarTrasConflicto(entradaCachePendiente);
            if (vinculoPendiente is not null)
                cacheRepositorio.DescartarVinculoTrasConflicto(vinculoPendiente);

            if (entradaCachePendiente is not null && documentoId is { } documentoIdTrasConflicto)
            {
                var ganadora = await cacheRepositorio.ObtenerAsync(hash, tipoEsperado, cancellationToken);
                if (ganadora is not null)
                    await cacheRepositorio.VincularDocumentoAsync(ganadora.Id, documentoIdTrasConflicto, cancellationToken);
            }

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
