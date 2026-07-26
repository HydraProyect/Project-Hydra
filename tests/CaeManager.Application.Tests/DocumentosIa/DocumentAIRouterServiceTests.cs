using CaeManager.Application.Common;
using CaeManager.Application.DocumentosIa;
using CaeManager.Application.DocumentosIa.Common;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Domain.Common;
using CaeManager.Domain.DocumentosIa;
using FluentAssertions;
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
            "mistral", CapacidadesProveedorIa.ExtraccionEstructurada,
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
            "mistral", CapacidadesProveedorIa.ExtraccionEstructurada,
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

    [Fact]
    public async Task No_se_queda_con_el_segundo_resultado_si_no_mejora_la_confianza()
    {
        var primero = new ProveedorIaFalso(
            "mistral", CapacidadesProveedorIa.ExtraccionEstructurada,
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
}
