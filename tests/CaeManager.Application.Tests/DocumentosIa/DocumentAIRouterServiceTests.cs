using CaeManager.Application.Common;
using CaeManager.Application.DocumentosIa;
using CaeManager.Application.DocumentosIa.Common;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Domain.Common;
using CaeManager.Domain.DocumentosIa;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CaeManager.Application.Tests.DocumentosIa;

public class DocumentAIRouterServiceTests
{
    private static DocumentAIRouterService CrearRouter(
        Result<ClasificacionDocumentoDto> clasificacion, Result<string> textoDigital, params IDocumentAIProvider[] proveedores) =>
        CrearRouterConDependencias(clasificacion, textoDigital, proveedores).Router;

    private static (DocumentAIRouterService Router, ExtraccionIaCacheRepositorioFalso Cache, AuditoriaExtraccionIaRepositorioFalso Auditoria, RasterizadorPaginasFalso Rasterizador) CrearRouterConDependencias(
        Result<ClasificacionDocumentoDto> clasificacion, Result<string> textoDigital, params IDocumentAIProvider[] proveedores)
    {
        var textoPorPagina = textoDigital.EsExitoso
            ? Result.Exito<IReadOnlyList<string>>([textoDigital.Valor])
            : Result.Fallo<IReadOnlyList<string>>(textoDigital.Error);
        return CrearRouterConDependencias(clasificacion, textoPorPagina, proveedores);
    }

    private static (DocumentAIRouterService Router, ExtraccionIaCacheRepositorioFalso Cache, AuditoriaExtraccionIaRepositorioFalso Auditoria, RasterizadorPaginasFalso Rasterizador) CrearRouterConDependencias(
        Result<ClasificacionDocumentoDto> clasificacion, Result<IReadOnlyList<string>> textoPorPagina, params IDocumentAIProvider[] proveedores)
    {
        var cache = new ExtraccionIaCacheRepositorioFalso();
        var auditoria = new AuditoriaExtraccionIaRepositorioFalso();
        var rasterizador = new RasterizadorPaginasFalso();
        var router = new DocumentAIRouterService(
            new ClasificadorDocumentoServiceFalso(clasificacion),
            new ExtractorTextoDigitalServiceFalso(textoPorPagina),
            new LocalizadorPaginasRelevantesService(),
            rasterizador,
            new DocumentAIProviderFactory(proveedores),
            cache,
            auditoria,
            new UnitOfWorkFalso(),
            NullLogger<DocumentAIRouterService>.Instance);
        return (router, cache, auditoria, rasterizador);
    }

    private static Result<ClasificacionDocumentoDto> Clasificacion(TipoContenidoDocumento tipo, params bool[] paginas) =>
        Result.Exito(new ClasificacionDocumentoDto(tipo, paginas.Length, paginas));

    [Fact]
    public async Task Caso_1_documento_digital_no_llama_a_ningun_proveedor_de_ocr()
    {
        var proveedor = new ProveedorIaFalso("anthropic", CapacidadesProveedorIa.OcrImagenAEscaneado | CapacidadesProveedorIa.ExtraccionEstructurada);
        var router = CrearRouter(Clasificacion(TipoContenidoDocumento.Digital, true, true), Result.Exito("texto digital real"), proveedor);

        var resultado = await router.ProcesarAsync([1, 2, 3], "documento.pdf", "Reconocimiento médico");

        resultado.EsExitoso.Should().BeTrue();
        proveedor.VecesLlamadoParaTexto.Should().Be(0);
        proveedor.VecesLlamadoParaEstructurado.Should().Be(1);
    }

    [Theory]
    [InlineData(TipoContenidoDocumento.Escaneado)]
    [InlineData(TipoContenidoDocumento.Imagen)]
    [InlineData(TipoContenidoDocumento.Mixto)]
    public async Task Casos_2_3_4_documento_no_digital_pasa_por_ocr_antes_de_estructurar(TipoContenidoDocumento tipo)
    {
        var proveedor = new ProveedorIaFalso(
            "anthropic", CapacidadesProveedorIa.OcrImagenAEscaneado | CapacidadesProveedorIa.ExtraccionEstructurada,
            resultadoTexto: Result.Exito(new TextoExtraccionDto("texto vía ocr", CosteEstimado: 0.01m)));
        var router = CrearRouter(Clasificacion(tipo, false), Result.Exito("no debería usarse"), proveedor);

        var resultado = await router.ProcesarAsync([1, 2, 3], "documento.pdf", "Póliza de seguro");

        resultado.EsExitoso.Should().BeTrue();
        proveedor.VecesLlamadoParaTexto.Should().Be(1);
        proveedor.VecesLlamadoParaEstructurado.Should().Be(1);
    }

    [Fact]
    public async Task Falla_de_forma_controlada_si_no_hay_proveedor_de_ocr_para_un_documento_escaneado()
    {
        var soloEstructuracion = new ProveedorIaFalso("gemini", CapacidadesProveedorIa.ExtraccionEstructurada);
        var router = CrearRouter(Clasificacion(TipoContenidoDocumento.Escaneado, false), Result.Exito("n/a"), soloEstructuracion);

        var resultado = await router.ProcesarAsync([1, 2, 3], "documento.pdf", "Póliza de seguro");

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("DocumentAIRouter.SinProveedorOcr");
    }

    [Fact]
    public async Task Falla_de_forma_controlada_si_no_hay_ningun_proveedor_registrado()
    {
        var router = CrearRouter(Clasificacion(TipoContenidoDocumento.Digital, true), Result.Exito("texto"));

        var resultado = await router.ProcesarAsync([1, 2, 3], "documento.pdf", "Certificado");

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("DocumentAIRouter.SinProveedor");
    }

    [Fact]
    public async Task Propaga_el_fallo_de_clasificacion_sin_llamar_a_ningun_proveedor()
    {
        var proveedor = new ProveedorIaFalso("anthropic", CapacidadesProveedorIa.OcrImagenAEscaneado | CapacidadesProveedorIa.ExtraccionEstructurada);
        var clasificacionFallida = Result.Fallo<ClasificacionDocumentoDto>(Error.Crear("ClasificacionDocumento.ArchivoInvalido", "no se pudo leer"));
        var router = CrearRouter(clasificacionFallida, Result.Exito("n/a"), proveedor);

        var resultado = await router.ProcesarAsync([1, 2, 3], "documento.pdf", "Certificado");

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("ClasificacionDocumento.ArchivoInvalido");
        proveedor.VecesLlamadoParaTexto.Should().Be(0);
        proveedor.VecesLlamadoParaEstructurado.Should().Be(0);
    }

    [Fact]
    public async Task Reintenta_con_un_segundo_proveedor_cuando_la_confianza_del_primero_es_baja_y_mejora()
    {
        var primero = new ProveedorIaFalso(
            "anthropic", CapacidadesProveedorIa.ExtraccionEstructurada,
            resultadoEstructurado: Result.Exito(new ExtraccionEstructuradaDto("Póliza", new Dictionary<string, string?>(), 55, "baja calidad")));
        var segundo = new ProveedorIaFalso(
            "gemini", CapacidadesProveedorIa.ExtraccionEstructurada,
            resultadoEstructurado: Result.Exito(new ExtraccionEstructuradaDto("Póliza", new Dictionary<string, string?>(), 92, null)));
        var router = CrearRouter(Clasificacion(TipoContenidoDocumento.Digital, true), Result.Exito("texto"), primero, segundo);

        var resultado = await router.ProcesarAsync([1, 2, 3], "documento.pdf", "Póliza de seguro");

        resultado.EsExitoso.Should().BeTrue();
        resultado.Valor.ConfianzaGeneral.Should().Be(92);
        primero.VecesLlamadoParaEstructurado.Should().Be(1);
        segundo.VecesLlamadoParaEstructurado.Should().Be(1);
    }

    [Fact]
    public async Task No_reintenta_cuando_la_confianza_del_primero_ya_es_alta()
    {
        var primero = new ProveedorIaFalso(
            "anthropic", CapacidadesProveedorIa.ExtraccionEstructurada,
            resultadoEstructurado: Result.Exito(new ExtraccionEstructuradaDto("Póliza", new Dictionary<string, string?>(), 98, null)));
        var segundo = new ProveedorIaFalso("gemini", CapacidadesProveedorIa.ExtraccionEstructurada);
        var router = CrearRouter(Clasificacion(TipoContenidoDocumento.Digital, true), Result.Exito("texto"), primero, segundo);

        var resultado = await router.ProcesarAsync([1, 2, 3], "documento.pdf", "Póliza de seguro");

        resultado.Valor.ConfianzaGeneral.Should().Be(98);
        segundo.VecesLlamadoParaEstructurado.Should().Be(0);
    }

    [Fact]
    public async Task No_reintenta_cuando_solo_hay_un_proveedor_de_estructuracion_aunque_la_confianza_sea_baja()
    {
        var unico = new ProveedorIaFalso(
            "anthropic", CapacidadesProveedorIa.ExtraccionEstructurada,
            resultadoEstructurado: Result.Exito(new ExtraccionEstructuradaDto("Póliza", new Dictionary<string, string?>(), 40, "documento borroso")));
        var router = CrearRouter(Clasificacion(TipoContenidoDocumento.Digital, true), Result.Exito("texto"), unico);

        var resultado = await router.ProcesarAsync([1, 2, 3], "documento.pdf", "Póliza de seguro");

        resultado.EsExitoso.Should().BeTrue();
        resultado.Valor.ConfianzaGeneral.Should().Be(40);
        unico.VecesLlamadoParaEstructurado.Should().Be(1);
    }

    /// <summary>
    /// El fallback que no existía: antes, el segundo proveedor solo entraba si
    /// el primero devolvía ÉXITO con confianza baja, así que un timeout, un 429
    /// o una credencial ausente en el primario tumbaban el análisis aunque
    /// hubiera alternativas sanas configuradas.
    /// </summary>
    [Fact]
    public async Task Cae_al_siguiente_proveedor_cuando_el_primero_falla()
    {
        var primero = new ProveedorIaFalso(
            "anthropic", CapacidadesProveedorIa.ExtraccionEstructurada,
            resultadoEstructurado: Result.Fallo<ExtraccionEstructuradaDto>(
                Error.Crear("DocumentAIProvider.ErrorApi", "No pudimos procesar el documento automáticamente.")));
        var segundo = new ProveedorIaFalso(
            "gemini", CapacidadesProveedorIa.ExtraccionEstructurada,
            resultadoEstructurado: Result.Exito(new ExtraccionEstructuradaDto("Póliza", new Dictionary<string, string?>(), 88, null)));
        var (router, _, auditoria, _) = CrearRouterConDependencias(
            Clasificacion(TipoContenidoDocumento.Digital, true), Result.Exito("texto"), primero, segundo);

        var resultado = await router.ProcesarAsync([1, 2, 3], "documento.pdf", "Póliza de seguro");

        resultado.EsExitoso.Should().BeTrue();
        resultado.Valor.ConfianzaGeneral.Should().Be(88);
        primero.VecesLlamadoParaEstructurado.Should().Be(1);
        segundo.VecesLlamadoParaEstructurado.Should().Be(1);
        auditoria.Auditorias[0].ProveedorCodigo.Should().Be("gemini", "el ganador es quien respondió, no quien se intentó primero");
    }

    [Fact]
    public async Task Devuelve_el_ultimo_error_cuando_fallan_todos_los_proveedores()
    {
        var primero = new ProveedorIaFalso(
            "anthropic", CapacidadesProveedorIa.ExtraccionEstructurada,
            resultadoEstructurado: Result.Fallo<ExtraccionEstructuradaDto>(Error.Crear("DocumentAIProvider.ErrorApi", "caído")));
        var segundo = new ProveedorIaFalso(
            "gemini", CapacidadesProveedorIa.ExtraccionEstructurada,
            resultadoEstructurado: Result.Fallo<ExtraccionEstructuradaDto>(Error.Crear("DocumentAIProvider.ErrorRed", "sin red")));
        var (router, _, auditoria, _) = CrearRouterConDependencias(
            Clasificacion(TipoContenidoDocumento.Digital, true), Result.Exito("texto"), primero, segundo);

        var resultado = await router.ProcesarAsync([1, 2, 3], "documento.pdf", "Póliza de seguro");

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("DocumentAIProvider.ErrorRed", "el error devuelto es el del último intento");
        primero.VecesLlamadoParaEstructurado.Should().Be(1);
        segundo.VecesLlamadoParaEstructurado.Should().Be(1);
        auditoria.Auditorias[0].ProveedorCodigo.Should().Be("ninguno");
    }

    /// <summary>
    /// Antes, si el segundo modelo mejoraba al primero se auditaba solo el
    /// coste del ganador: el gasto real quedaba infravalorado justo en los
    /// documentos que consumen más llamadas.
    /// </summary>
    [Fact]
    public async Task Suma_en_la_auditoria_el_coste_del_intento_descartado()
    {
        var primero = new ProveedorIaFalso(
            "anthropic", CapacidadesProveedorIa.ExtraccionEstructurada,
            resultadoEstructurado: Result.Exito(new ExtraccionEstructuradaDto("Póliza", new Dictionary<string, string?>(), 55, null, CosteEstimado: 0.02m)));
        var segundo = new ProveedorIaFalso(
            "gemini", CapacidadesProveedorIa.ExtraccionEstructurada,
            resultadoEstructurado: Result.Exito(new ExtraccionEstructuradaDto("Póliza", new Dictionary<string, string?>(), 92, null, CosteEstimado: 0.03m)));
        var (router, _, auditoria, _) = CrearRouterConDependencias(
            Clasificacion(TipoContenidoDocumento.Digital, true), Result.Exito("texto"), primero, segundo);

        await router.ProcesarAsync([1, 2, 3], "documento.pdf", "Póliza de seguro");

        auditoria.Auditorias[0].CosteEstimado.Should().Be(0.05m, "0.02 del descartado + 0.03 del ganador");
        auditoria.Auditorias[0].Incidencias.Should().Contain("anthropic").And.Contain("gemini");
    }

    [Fact]
    public async Task No_se_queda_con_el_segundo_resultado_si_no_mejora_la_confianza()
    {
        var primero = new ProveedorIaFalso(
            "anthropic", CapacidadesProveedorIa.ExtraccionEstructurada,
            resultadoEstructurado: Result.Exito(new ExtraccionEstructuradaDto("Póliza", new Dictionary<string, string?>(), 60, null)));
        var segundo = new ProveedorIaFalso(
            "gemini", CapacidadesProveedorIa.ExtraccionEstructurada,
            resultadoEstructurado: Result.Exito(new ExtraccionEstructuradaDto("Póliza", new Dictionary<string, string?>(), 45, null)));
        var router = CrearRouter(Clasificacion(TipoContenidoDocumento.Digital, true), Result.Exito("texto"), primero, segundo);

        var resultado = await router.ProcesarAsync([1, 2, 3], "documento.pdf", "Póliza de seguro");

        resultado.Valor.ConfianzaGeneral.Should().Be(60);
    }

    [Fact]
    public async Task Registra_una_auditoria_con_el_proveedor_el_coste_y_la_confianza_al_procesar_con_exito()
    {
        var proveedor = new ProveedorIaFalso(
            "anthropic", CapacidadesProveedorIa.ExtraccionEstructurada,
            resultadoEstructurado: Result.Exito(new ExtraccionEstructuradaDto("Póliza", new Dictionary<string, string?>(), 95, null, CosteEstimado: 0.02m)));
        var (router, _, auditoria, _) = CrearRouterConDependencias(Clasificacion(TipoContenidoDocumento.Digital, true), Result.Exito("texto"), proveedor);

        await router.ProcesarAsync([1, 2, 3], "documento.pdf", "Póliza de seguro");

        auditoria.Auditorias.Should().ContainSingle();
        auditoria.Auditorias[0].ProveedorCodigo.Should().Be("anthropic");
        auditoria.Auditorias[0].ConfianzaGeneral.Should().Be(95);
        auditoria.Auditorias[0].CosteEstimado.Should().Be(0.02m);
        auditoria.Auditorias[0].CosteEstimadoOcr.Should().BeNull("documento digital, no hubo paso OCR");
        auditoria.Auditorias[0].NumeroPaginas.Should().Be(1);
    }

    [Fact]
    public async Task Registra_coste_ocr_en_auditoria_cuando_el_documento_es_escaneado()
    {
        var proveedor = new ProveedorIaFalso(
            "mistral-ocr+anthropic",
            CapacidadesProveedorIa.OcrImagenAEscaneado | CapacidadesProveedorIa.ExtraccionEstructurada,
            resultadoTexto: Result.Exito(new TextoExtraccionDto("texto ocr", CosteEstimado: 0.008m)),
            resultadoEstructurado: Result.Exito(new ExtraccionEstructuradaDto("Nómina", new Dictionary<string, string?>(), 90, null, CosteEstimado: 0.03m)));
        var (router, _, auditoria, _) = CrearRouterConDependencias(Clasificacion(TipoContenidoDocumento.Escaneado, false), Result.Exito("no se usa"), proveedor);

        await router.ProcesarAsync([1, 2, 3], "nomina.pdf", "Nómina");

        auditoria.Auditorias.Should().ContainSingle();
        auditoria.Auditorias[0].CosteEstimadoOcr.Should().Be(0.008m, "el proveedor devolvió coste OCR");
        auditoria.Auditorias[0].CosteEstimado.Should().Be(0.03m, "el proveedor devolvió coste de estructuración");
    }

    /// <summary>
    /// Reproducibilidad: sin esto, "por qué esta extracción dio este
    /// resultado" no se puede reconstruir después de que el proveedor
    /// cambie de versión bajo el mismo alias — ver el comentario de
    /// AuditoriaExtraccionIa.
    /// </summary>
    [Fact]
    public async Task Registra_el_modelo_exacto_el_request_id_y_la_version_de_pipeline_de_la_llamada_ganadora()
    {
        var proveedor = new ProveedorIaFalso(
            "gemini", CapacidadesProveedorIa.ExtraccionEstructurada,
            resultadoEstructurado: Result.Exito(new ExtraccionEstructuradaDto(
                "Póliza", new Dictionary<string, string?>(), 95, null, ModeloExacto: "gemini-3.5-flash-002", RequestId: "x-goog-request-id=abc123")));
        var (router, _, auditoria, _) = CrearRouterConDependencias(Clasificacion(TipoContenidoDocumento.Digital, true), Result.Exito("texto"), proveedor);

        await router.ProcesarAsync([1, 2, 3], "documento.pdf", "Póliza de seguro");

        auditoria.Auditorias[0].ModeloExacto.Should().Be("gemini-3.5-flash-002");
        auditoria.Auditorias[0].RequestId.Should().Be("x-goog-request-id=abc123");
        auditoria.Auditorias[0].VersionPipeline.Should().Be(ExtraccionIaCache.VersionPipelineActual);
    }

    [Fact]
    public async Task Registra_todos_los_proveedores_invocados_para_la_estructuracion_no_solo_el_ganador()
    {
        var primero = new ProveedorIaFalso(
            "anthropic", CapacidadesProveedorIa.ExtraccionEstructurada,
            resultadoEstructurado: Result.Fallo<ExtraccionEstructuradaDto>(Error.Crear("DocumentAIProvider.ErrorApi", "caído")));
        var segundo = new ProveedorIaFalso(
            "gemini", CapacidadesProveedorIa.ExtraccionEstructurada,
            resultadoEstructurado: Result.Exito(new ExtraccionEstructuradaDto("Póliza", new Dictionary<string, string?>(), 88, null)));
        var (router, _, auditoria, _) = CrearRouterConDependencias(
            Clasificacion(TipoContenidoDocumento.Digital, true), Result.Exito("texto"), primero, segundo);

        await router.ProcesarAsync([1, 2, 3], "documento.pdf", "Póliza de seguro");

        auditoria.Auditorias[0].ProveedoresInvocados.Should().Be("anthropic,gemini",
            "el que falló también se intentó, y eso tiene que quedar visible");
    }

    [Fact]
    public async Task Registra_el_proveedor_de_ocr_junto_a_los_de_estructuracion_en_un_documento_escaneado()
    {
        var ocr = new ProveedorIaFalso(
            "mistral-ocr", CapacidadesProveedorIa.OcrImagenAEscaneado,
            resultadoTexto: Result.Exito(new TextoExtraccionDto("texto ocr")));
        var estructuracion = new ProveedorIaFalso(
            "gemini", CapacidadesProveedorIa.ExtraccionEstructurada,
            resultadoEstructurado: Result.Exito(new ExtraccionEstructuradaDto("Nómina", new Dictionary<string, string?>(), 90, null)));
        var (router, _, auditoria, _) = CrearRouterConDependencias(
            Clasificacion(TipoContenidoDocumento.Escaneado, false), Result.Exito("no se usa"), ocr, estructuracion);

        await router.ProcesarAsync([1, 2, 3], "nomina.pdf", "Nómina");

        auditoria.Auditorias[0].ProveedoresInvocados.Should().Be("mistral-ocr,gemini");
    }

    [Fact]
    public async Task Enlaza_la_auditoria_al_documento_cuando_se_indica_documentoId()
    {
        var proveedor = new ProveedorIaFalso(
            "anthropic", CapacidadesProveedorIa.ExtraccionEstructurada,
            resultadoEstructurado: Result.Exito(new ExtraccionEstructuradaDto("Apto médico", new Dictionary<string, string?>(), 97, null)));
        var (router, _, auditoria, _) = CrearRouterConDependencias(Clasificacion(TipoContenidoDocumento.Digital, true), Result.Exito("texto"), proveedor);
        var documentoId = Guid.NewGuid();

        await router.ProcesarAsync([1, 2, 3], "documento.pdf", "Apto médico", documentoId);

        auditoria.Auditorias.Should().ContainSingle();
        auditoria.Auditorias[0].DocumentoId.Should().Be(documentoId);
        auditoria.Auditorias[0].DecisionHumana.Should().BeNull("todavía no hay decisión humana — solo el enlace");
    }

    [Fact]
    public async Task No_enlaza_la_auditoria_a_ningun_documento_cuando_no_se_indica()
    {
        var proveedor = new ProveedorIaFalso(
            "anthropic", CapacidadesProveedorIa.ExtraccionEstructurada,
            resultadoEstructurado: Result.Exito(new ExtraccionEstructuradaDto("Apto médico", new Dictionary<string, string?>(), 97, null)));
        var (router, _, auditoria, _) = CrearRouterConDependencias(Clasificacion(TipoContenidoDocumento.Digital, true), Result.Exito("texto"), proveedor);

        await router.ProcesarAsync([1, 2, 3], "documento.pdf", "Apto médico");

        auditoria.Auditorias[0].DocumentoId.Should().BeNull("triage previo a la creación del Documento — todavía no existe qué enlazar");
    }

    /// <summary>REC-036/DEC-34: la entrada nueva de caché queda vinculada al Documento que la produjo — ver ExtraccionIaCacheDocumento.</summary>
    [Fact]
    public async Task Vincula_la_entrada_de_cache_recien_creada_al_documento_indicado()
    {
        var proveedor = new ProveedorIaFalso(
            "anthropic", CapacidadesProveedorIa.ExtraccionEstructurada,
            resultadoEstructurado: Result.Exito(new ExtraccionEstructuradaDto("Apto médico", new Dictionary<string, string?>(), 97, null)));
        var (router, cache, _, _) = CrearRouterConDependencias(Clasificacion(TipoContenidoDocumento.Digital, true), Result.Exito("texto"), proveedor);
        var documentoId = Guid.NewGuid();

        await router.ProcesarAsync([1, 2, 3], "documento.pdf", "Apto médico", documentoId);

        cache.Vinculos.Should().ContainSingle().Which.DocumentoId.Should().Be(documentoId);
    }

    /// <summary>Triage previo a la creación del Documento (sin documentoId): la entrada de caché no debe llevar ningún vínculo.</summary>
    [Fact]
    public async Task No_vincula_la_entrada_de_cache_a_ningun_documento_cuando_no_se_indica()
    {
        var proveedor = new ProveedorIaFalso(
            "anthropic", CapacidadesProveedorIa.ExtraccionEstructurada,
            resultadoEstructurado: Result.Exito(new ExtraccionEstructuradaDto("Apto médico", new Dictionary<string, string?>(), 97, null)));
        var (router, cache, _, _) = CrearRouterConDependencias(Clasificacion(TipoContenidoDocumento.Digital, true), Result.Exito("texto"), proveedor);

        await router.ProcesarAsync([1, 2, 3], "documento.pdf", "Apto médico");

        cache.Vinculos.Should().BeEmpty();
    }

    /// <summary>
    /// El caso central de riesgo #2 del handoff: una entrada creada por
    /// triage sin documentoId (p. ej. detección al subir) que luego un
    /// acierto de caché sobre un Documento ya existente (verificación IA)
    /// reutiliza — el vínculo tiene que crearse en ESE segundo momento, o la
    /// entrada quedaría huérfana para siempre.
    /// </summary>
    [Fact]
    public async Task Un_acierto_de_cache_sobre_un_documento_existente_vincula_una_entrada_creada_antes_sin_documento()
    {
        var proveedor = new ProveedorIaFalso(
            "anthropic", CapacidadesProveedorIa.ExtraccionEstructurada,
            resultadoEstructurado: Result.Exito(new ExtraccionEstructuradaDto("Apto médico", new Dictionary<string, string?>(), 97, null)));
        var (router, cache, _, _) = CrearRouterConDependencias(Clasificacion(TipoContenidoDocumento.Digital, true), Result.Exito("texto"), proveedor);
        byte[] archivo = [1, 2, 3];
        var documentoId = Guid.NewGuid();

        await router.ProcesarAsync(archivo, "documento.pdf", "Apto médico"); // triage: sin documentoId
        cache.Vinculos.Should().BeEmpty("todavía no hay Documento al que enlazar");

        await router.ProcesarAsync(archivo, "documento.pdf", "Apto médico", documentoId); // verificación: acierto de caché, con documentoId

        proveedor.VecesLlamadoParaEstructurado.Should().Be(1, "la segunda llamada se sirvió desde caché, no volvió a pagar al proveedor");
        cache.Vinculos.Should().ContainSingle().Which.DocumentoId.Should().Be(documentoId);
    }

    /// <summary>Reprocesar el mismo Documento (misma clave de caché) no debe duplicar el vínculo — es la misma idempotencia que Agregar ya da para el JSON.</summary>
    [Fact]
    public async Task Reprocesar_el_mismo_documento_no_duplica_el_vinculo()
    {
        var proveedor = new ProveedorIaFalso(
            "anthropic", CapacidadesProveedorIa.ExtraccionEstructurada,
            resultadoEstructurado: Result.Exito(new ExtraccionEstructuradaDto("Apto médico", new Dictionary<string, string?>(), 97, null)));
        var (router, cache, _, _) = CrearRouterConDependencias(Clasificacion(TipoContenidoDocumento.Digital, true), Result.Exito("texto"), proveedor);
        byte[] archivo = [1, 2, 3];
        var documentoId = Guid.NewGuid();

        await router.ProcesarAsync(archivo, "documento.pdf", "Apto médico", documentoId);
        await router.ProcesarAsync(archivo, "documento.pdf", "Apto médico", documentoId);

        cache.Vinculos.Should().ContainSingle();
    }

    [Fact]
    public async Task Registra_una_auditoria_con_proveedor_ninguno_cuando_falla()
    {
        var (router, _, auditoria, _) = CrearRouterConDependencias(Clasificacion(TipoContenidoDocumento.Digital, true), Result.Exito("texto"));

        await router.ProcesarAsync([1, 2, 3], "documento.pdf", "Póliza de seguro");

        auditoria.Auditorias.Should().ContainSingle();
        auditoria.Auditorias[0].ProveedorCodigo.Should().Be("ninguno");
        auditoria.Auditorias[0].ConfianzaGeneral.Should().Be(0);
    }

    [Fact]
    public async Task La_segunda_llamada_con_el_mismo_contenido_se_sirve_desde_cache_sin_llamar_al_proveedor()
    {
        var proveedor = new ProveedorIaFalso(
            "anthropic", CapacidadesProveedorIa.ExtraccionEstructurada,
            resultadoEstructurado: Result.Exito(new ExtraccionEstructuradaDto("Póliza", new Dictionary<string, string?>(), 95, null, CosteEstimado: 0.02m)));
        var (router, _, auditoria, _) = CrearRouterConDependencias(Clasificacion(TipoContenidoDocumento.Digital, true), Result.Exito("texto"), proveedor);
        var contenido = new byte[] { 9, 8, 7 };

        var primeraVez = await router.ProcesarAsync(contenido, "documento.pdf", "Póliza de seguro");
        var segundaVez = await router.ProcesarAsync(contenido, "documento.pdf", "Póliza de seguro");

        primeraVez.EsExitoso.Should().BeTrue();
        segundaVez.EsExitoso.Should().BeTrue();
        segundaVez.Valor.ConfianzaGeneral.Should().Be(95);
        proveedor.VecesLlamadoParaEstructurado.Should().Be(1);
        auditoria.Auditorias.Should().HaveCount(2);
        auditoria.Auditorias[1].ProveedorCodigo.Should().Be("cache");
        auditoria.Auditorias[1].CosteEstimado.Should().Be(0m);
    }

    /// <summary>
    /// Lo que guarda la caché no es una transcripción del archivo: es una
    /// interpretación hecha bajo un tipo esperado concreto, que entra en el
    /// prompt y condiciona qué campos busca el modelo. Con la clave anterior
    /// (solo hash) el mismo PDF pedido primero como un tipo y después como otro
    /// devolvía la primera lectura, sin volver a mirar el documento y sin dejar
    /// constancia de que la pregunta era distinta.
    /// </summary>
    [Fact]
    public async Task El_mismo_archivo_pedido_como_otro_tipo_no_reutiliza_la_lectura_anterior()
    {
        var proveedor = new ProveedorIaFalso(
            "anthropic", CapacidadesProveedorIa.ExtraccionEstructurada,
            resultadoEstructurado: Result.Exito(new ExtraccionEstructuradaDto("Póliza", new Dictionary<string, string?>(), 95, null)));
        var (router, _, auditoria, _) = CrearRouterConDependencias(
            Clasificacion(TipoContenidoDocumento.Digital, true), Result.Exito("texto"), proveedor);

        byte[] mismoArchivo = [1, 2, 3];
        await router.ProcesarAsync(mismoArchivo, "documento.pdf", "Póliza de seguro");
        await router.ProcesarAsync(mismoArchivo, "documento.pdf", "Apto médico");

        proveedor.VecesLlamadoParaEstructurado.Should().Be(2, "son dos preguntas distintas sobre el mismo archivo");
        auditoria.Auditorias.Should().NotContain(a => a.ProveedorCodigo == "cache");
    }

    [Fact]
    public async Task El_mismo_archivo_pedido_como_el_mismo_tipo_si_reutiliza_la_cache()
    {
        var proveedor = new ProveedorIaFalso(
            "anthropic", CapacidadesProveedorIa.ExtraccionEstructurada,
            resultadoEstructurado: Result.Exito(new ExtraccionEstructuradaDto("Póliza", new Dictionary<string, string?>(), 95, null)));
        var (router, _, auditoria, _) = CrearRouterConDependencias(
            Clasificacion(TipoContenidoDocumento.Digital, true), Result.Exito("texto"), proveedor);

        byte[] mismoArchivo = [1, 2, 3];
        await router.ProcesarAsync(mismoArchivo, "documento.pdf", "Póliza de seguro");
        // Mayúsculas y espacios distintos: la normalización del tipo tiene que
        // hacer que siga siendo la misma clave, o la caché no acertaría nunca.
        await router.ProcesarAsync(mismoArchivo, "documento.pdf", "  PÓLIZA   DE SEGURO ");

        proveedor.VecesLlamadoParaEstructurado.Should().Be(1);
        auditoria.Auditorias[1].ProveedorCodigo.Should().Be("cache");
    }

    [Fact]
    public async Task Contenidos_distintos_no_comparten_cache()
    {
        var proveedor = new ProveedorIaFalso(
            "anthropic", CapacidadesProveedorIa.ExtraccionEstructurada,
            resultadoEstructurado: Result.Exito(new ExtraccionEstructuradaDto("Póliza", new Dictionary<string, string?>(), 95, null)));
        var (router, _, _, _) = CrearRouterConDependencias(Clasificacion(TipoContenidoDocumento.Digital, true), Result.Exito("texto"), proveedor);

        await router.ProcesarAsync([1, 2, 3], "documento.pdf", "Póliza de seguro");
        await router.ProcesarAsync([4, 5, 6], "documento.pdf", "Póliza de seguro");

        proveedor.VecesLlamadoParaEstructurado.Should().Be(2);
    }

    [Fact]
    public async Task Caso_4_mixto_usa_texto_digital_para_paginas_digitales_y_ocr_solo_para_las_escaneadas()
    {
        // Documento de 3 páginas: 0=digital, 1=escaneada, 2=digital
        var proveedor = new ProveedorIaFalso(
            "mistral-ocr+anthropic",
            CapacidadesProveedorIa.OcrImagenAEscaneado | CapacidadesProveedorIa.ExtraccionEstructurada,
            resultadoTexto: Result.Exito(new TextoExtraccionDto("texto ocr pagina 1", CosteEstimado: 0.004m)),
            resultadoEstructurado: Result.Exito(new ExtraccionEstructuradaDto("Nómina", new Dictionary<string, string?>(), 88, null)));

        var textoPorPagina = Result.Exito<IReadOnlyList<string>>(["texto digital p0", "ignorado", "texto digital p2"]);
        var clasificacion = Clasificacion(TipoContenidoDocumento.Mixto, true, false, true);
        var (router, _, auditoria, rasterizador) = CrearRouterConDependencias(clasificacion, textoPorPagina, proveedor);

        var resultado = await router.ProcesarAsync([1, 2, 3], "nomina.pdf", "Nómina");

        resultado.EsExitoso.Should().BeTrue();
        proveedor.VecesLlamadoParaTexto.Should().Be(1, "solo se llama OCR para la página escaneada (índice 1)");
        proveedor.VecesLlamadoParaEstructurado.Should().Be(1);
        rasterizador.VecesLlamado.Should().Be(1);
        rasterizador.UltimosIndicesRasterizados.Should().BeEquivalentTo([1], "solo la página escaneada se rasteriza");
        proveedor.UltimoTextoRecibidoParaEstructurar.Should().Be("texto digital p0\n\ntexto ocr pagina 1\n\ntexto digital p2");
        auditoria.Auditorias[0].CosteEstimadoOcr.Should().Be(0.004m);
    }

    [Fact]
    public async Task Caso_4_mixto_acumula_coste_ocr_de_todas_las_paginas_escaneadas()
    {
        // Documento de 4 páginas: 0=digital, 1=escaneada, 2=escaneada, 3=digital
        var proveedor = new ProveedorIaFalso(
            "mistral-ocr+anthropic",
            CapacidadesProveedorIa.OcrImagenAEscaneado | CapacidadesProveedorIa.ExtraccionEstructurada,
            resultadoTexto: Result.Exito(new TextoExtraccionDto("texto ocr", CosteEstimado: 0.004m)),
            resultadoEstructurado: Result.Exito(new ExtraccionEstructuradaDto("Contrato", new Dictionary<string, string?>(), 90, null)));

        var textoPorPagina = Result.Exito<IReadOnlyList<string>>(["p0", "ignorado", "ignorado", "p3"]);
        var clasificacion = Clasificacion(TipoContenidoDocumento.Mixto, true, false, false, true);
        var (router, _, auditoria, rasterizador) = CrearRouterConDependencias(clasificacion, textoPorPagina, proveedor);

        await router.ProcesarAsync([1, 2, 3], "contrato.pdf", "Contrato");

        proveedor.VecesLlamadoParaTexto.Should().Be(2, "dos páginas escaneadas");
        rasterizador.UltimosIndicesRasterizados.Should().BeEquivalentTo([1, 2]);
        auditoria.Auditorias[0].CosteEstimadoOcr.Should().Be(0.008m, "0.004 × 2 páginas escaneadas");
    }

    /// <summary>
    /// Antes se rasterizaban TODAS las páginas escaneadas antes del primer OCR,
    /// así que la lista entera de PNG vivía en memoria hasta terminar el
    /// documento — cientos de megas para un escaneado grande, en el mismo
    /// proceso que sirve Blazor.
    ///
    /// La comprobación mira el estado del rasterizador DESDE DENTRO de cada
    /// llamada de OCR: si el router siguiera rasterizando todo por adelantado,
    /// en el primer OCR ya constarían las tres páginas. Contar llamadas al
    /// final no distinguiría las dos implementaciones.
    /// </summary>
    [Fact]
    public async Task Caso_4_mixto_rasteriza_y_envia_pagina_a_pagina_sin_retenerlas_todas()
    {
        RasterizadorPaginasFalso? rasterizadorObservado = null;
        var paginasRasterizadasEnCadaOcr = new List<int>();

        var proveedor = new ProveedorIaFalso(
            "mistral-ocr+anthropic",
            CapacidadesProveedorIa.OcrImagenAEscaneado | CapacidadesProveedorIa.ExtraccionEstructurada,
            resultadoTexto: Result.Exito(new TextoExtraccionDto("texto ocr")),
            resultadoEstructurado: Result.Exito(new ExtraccionEstructuradaDto("Contrato", new Dictionary<string, string?>(), 90, null)),
            alExtraerTexto: () => paginasRasterizadasEnCadaOcr.Add(rasterizadorObservado!.VecesLlamado));

        // 4 páginas: 0 digital, 1-3 escaneadas.
        var textoPorPagina = Result.Exito<IReadOnlyList<string>>(["p0", "ignorado", "ignorado", "ignorado"]);
        var clasificacion = Clasificacion(TipoContenidoDocumento.Mixto, true, false, false, false);
        var (router, _, _, rasterizador) = CrearRouterConDependencias(clasificacion, textoPorPagina, proveedor);
        rasterizadorObservado = rasterizador;

        await router.ProcesarAsync([1, 2, 3], "contrato.pdf", "Contrato");

        // En el OCR de la página k solo se ha rasterizado hasta la k: 1, 2, 3.
        paginasRasterizadasEnCadaOcr.Should().Equal(new[] { 1, 2, 3 },
            "cada página se rasteriza justo antes de enviarla, no todas por adelantado");
        rasterizador.UltimosIndicesRasterizados.Should().Equal(new[] { 1, 2, 3 });
    }

    /// <summary>
    /// El bucle de OCR es el único punto del pipeline donde el gasto crece
    /// linealmente con el tamaño del archivo (el proveedor factura por página).
    /// Se falla en vez de truncar: recortar en silencio devolvería una
    /// extracción incompleta presentada como completa, y las decisiones que
    /// cuelgan de ella se tomarían sobre un texto al que le faltan páginas.
    /// </summary>
    [Fact]
    public async Task Rechaza_un_documento_con_mas_paginas_escaneadas_de_las_que_se_procesan_automaticamente()
    {
        const int paginas = 101;
        var proveedor = new ProveedorIaFalso(
            "mistral-ocr+anthropic",
            CapacidadesProveedorIa.OcrImagenAEscaneado | CapacidadesProveedorIa.ExtraccionEstructurada);

        // Página 0 digital y el resto escaneadas: 101 escaneadas en total.
        var digitales = new bool[paginas + 1];
        digitales[0] = true;
        var textoPorPagina = Result.Exito<IReadOnlyList<string>>(
            Enumerable.Range(0, paginas + 1).Select(i => $"p{i}").ToList());

        var (router, _, _, rasterizador) = CrearRouterConDependencias(
            Clasificacion(TipoContenidoDocumento.Mixto, digitales), textoPorPagina, proveedor);

        var resultado = await router.ProcesarAsync([1, 2, 3], "escaneado-enorme.pdf", "Contrato");

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("DocumentAIRouter.DemasiadasPaginasEscaneadas");
        rasterizador.VecesLlamado.Should().Be(0, "el tope se comprueba antes de rasterizar y pagar nada");
        proveedor.VecesLlamadoParaTexto.Should().Be(0);
    }

    [Fact]
    public async Task Caso_4_mixto_falla_de_forma_controlada_si_el_rasterizador_no_puede_procesar_el_archivo()
    {
        var proveedor = new ProveedorIaFalso(
            "mistral-ocr", CapacidadesProveedorIa.OcrImagenAEscaneado | CapacidadesProveedorIa.ExtraccionEstructurada);
        var textoPorPagina = Result.Exito<IReadOnlyList<string>>(["p0", "ignorado"]);
        var clasificacion = Clasificacion(TipoContenidoDocumento.Mixto, true, false);

        var cache = new ExtraccionIaCacheRepositorioFalso();
        var auditoria = new AuditoriaExtraccionIaRepositorioFalso();
        var rasterizadorFallido = new RasterizadorPaginasFalso(fallido: true);
        var router = new DocumentAIRouterService(
            new ClasificadorDocumentoServiceFalso(clasificacion),
            new ExtractorTextoDigitalServiceFalso(textoPorPagina),
            new LocalizadorPaginasRelevantesService(),
            rasterizadorFallido,
            new DocumentAIProviderFactory([proveedor]),
            cache,
            auditoria,
            new UnitOfWorkFalso(),
            NullLogger<DocumentAIRouterService>.Instance);

        var resultado = await router.ProcesarAsync([1, 2, 3], "documento.pdf", "Certificado");

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Rasterizador.FalloConversion");
    }

    [Fact]
    public async Task Localiza_paginas_relevantes_y_lo_deja_constar_en_la_auditoria_para_un_documento_digital_grande()
    {
        var proveedor = new ProveedorIaFalso(
            "anthropic", CapacidadesProveedorIa.ExtraccionEstructurada,
            resultadoEstructurado: Result.Exito(new ExtraccionEstructuradaDto("Póliza", new Dictionary<string, string?>(), 95, null)));

        // 20 páginas de relleno (> UmbralPaginasParaLocalizar) con solo una relevante.
        var paginas = Enumerable.Range(0, 20).Select(i => $"página de relleno {i}").ToList();
        paginas[10] = "número de póliza 555, tomador: Empresa S.L.";
        var clasificacion = Result.Exito(new ClasificacionDocumentoDto(
            TipoContenidoDocumento.Digital, paginas.Count, Enumerable.Repeat(true, paginas.Count).ToList()));
        var (router, _, auditoria, _) = CrearRouterConDependencias(clasificacion, Result.Exito<IReadOnlyList<string>>(paginas), proveedor);

        var resultado = await router.ProcesarAsync([1, 2, 3], "poliza.pdf", "Póliza de seguro");

        resultado.EsExitoso.Should().BeTrue();
        proveedor.UltimoTextoRecibidoParaEstructurar.Should().Contain("número de póliza 555");
        proveedor.UltimoTextoRecibidoParaEstructurar.Should().NotContain("página de relleno 3");
        auditoria.Auditorias.Should().ContainSingle();
        auditoria.Auditorias[0].Incidencias.Should().Contain("Documento grande");
        auditoria.Auditorias[0].NumeroPaginas.Should().Be(20);
    }

    /// <summary>
    /// Simula la carrera de caché documental (ObtenerAsync + Agregar
    /// separados): otra ejecución concurrente escribió la misma clave de
    /// caché justo antes de que este SaveChangesAsync se ejecutara, así que
    /// el guardado conjunto (caché nueva + auditoría) choca contra el índice
    /// único. El router tiene que descartar la entrada perdedora del
    /// tracker y reintentar — nunca perder la auditoría de una extracción
    /// que sí tuvo éxito, y nunca repetir el mismo choque indefinidamente.
    /// </summary>
    [Fact]
    public async Task La_extraccion_no_se_pierde_cuando_otra_ejecucion_gana_la_carrera_de_cache()
    {
        var proveedor = new ProveedorIaFalso(
            "anthropic", CapacidadesProveedorIa.ExtraccionEstructurada,
            resultadoEstructurado: Result.Exito(new ExtraccionEstructuradaDto("Póliza", new Dictionary<string, string?>(), 95, null)));

        var cache = new ExtraccionIaCacheRepositorioFalso();
        var auditoria = new AuditoriaExtraccionIaRepositorioFalso();
        var unitOfWork = new UnitOfWorkQueFallaLaPrimeraVezFalso();
        var router = new DocumentAIRouterService(
            new ClasificadorDocumentoServiceFalso(Clasificacion(TipoContenidoDocumento.Digital, true)),
            new ExtractorTextoDigitalServiceFalso(Result.Exito<IReadOnlyList<string>>(["texto"])),
            new LocalizadorPaginasRelevantesService(),
            new RasterizadorPaginasFalso(),
            new DocumentAIProviderFactory([proveedor]),
            cache,
            auditoria,
            unitOfWork,
            NullLogger<DocumentAIRouterService>.Instance);

        var resultado = await router.ProcesarAsync([1, 2, 3], "documento.pdf", "Póliza de seguro");

        resultado.EsExitoso.Should().BeTrue("perder la carrera de caché no invalida una extracción que sí tuvo éxito");
        auditoria.Auditorias.Should().ContainSingle("la auditoría no puede perderse solo porque perdió la carrera de caché");
        auditoria.Auditorias[0].ProveedorCodigo.Should().Be("anthropic");
        cache.VecesDescartada.Should().Be(1, "la entrada perdedora se retira del tracker, o el siguiente guardado repetiría el mismo choque");
        unitOfWork.VecesGuardado.Should().Be(2, "un intento fallido y un reintento que sí guarda la auditoría");
    }

    private sealed class UnitOfWorkQueFallaLaPrimeraVezFalso : IUnitOfWork
    {
        private bool _yaFallo;

        public int VecesGuardado { get; private set; }

        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            VecesGuardado++;
            if (!_yaFallo)
            {
                _yaFallo = true;
                throw new DbUpdateException("Simula el choque contra el índice único de ExtraccionIaCache.");
            }

            return Task.FromResult(1);
        }
    }
}
