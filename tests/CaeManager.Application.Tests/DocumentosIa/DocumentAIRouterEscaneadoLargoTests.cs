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

/// <summary>
/// OWASP API4 (consumo de recursos sin límite): un PDF escaneado largo ya no va
/// entero al OCR. Se leen primero las páginas 1, 2, 3 y la última; el resto solo si
/// la estructuración no encuentra lo mínimo (tipo, fecha de emisión y firma, las
/// evidencias de <see cref="EvidenciaMinimaLectura"/>), y nunca más de 100 páginas
/// por documento. Coste y número de páginas de la auditoría son los de lo leído.
/// </summary>
public class DocumentAIRouterEscaneadoLargoTests
{
    private const decimal CosteOcrPorPagina = 0.004m;
    private const decimal CosteEstructuracionPorLlamada = 0.01m;

    private static readonly CapacidadesProveedorIa Todas =
        CapacidadesProveedorIa.OcrImagenAEscaneado | CapacidadesProveedorIa.ExtraccionEstructurada;

    private static Result<ExtraccionEstructuradaDto> Completa() => Result.Exito(new ExtraccionEstructuradaDto(
        "Certificado",
        new Dictionary<string, string?> { ["fechaEmision"] = "2026-01-15", ["tieneFirma"] = "true" },
        90, null, CosteEstructuracionPorLlamada));

    private static Result<ExtraccionEstructuradaDto> Incompleta() => Result.Exito(new ExtraccionEstructuradaDto(
        "Certificado", new Dictionary<string, string?>(), 90, null, CosteEstructuracionPorLlamada));

    private static ProveedorIaFalso Proveedor(
        Func<string, Result<ExtraccionEstructuradaDto>> estructurado,
        Func<string, Result<TextoExtraccionDto>>? texto = null) =>
        new("mistral-ocr+anthropic", Todas,
            resultadoTexto: Result.Exito(new TextoExtraccionDto("texto ocr", CosteOcrPorPagina)),
            estructuradoPorTexto: estructurado,
            textoPorNombreArchivo: texto ?? (nombre => Result.Exito(new TextoExtraccionDto($"[{nombre}]", CosteOcrPorPagina))));

    private static (DocumentAIRouterService Router, AuditoriaExtraccionIaRepositorioFalso Auditoria, RasterizadorPaginasFalso Rasterizador) CrearRouter(
        TipoContenidoDocumento tipo, int paginas, ProveedorIaFalso proveedor)
    {
        var auditoria = new AuditoriaExtraccionIaRepositorioFalso();
        var rasterizador = new RasterizadorPaginasFalso();
        var clasificacion = Result.Exito(new ClasificacionDocumentoDto(tipo, paginas, new bool[paginas]));
        var router = new DocumentAIRouterService(
            new ClasificadorDocumentoServiceFalso(clasificacion),
            new ExtractorTextoDigitalServiceFalso(Result.Exito<IReadOnlyList<string>>(["no se usa"])),
            new LocalizadorPaginasRelevantesService(),
            rasterizador,
            new DocumentAIProviderFactory([proveedor]),
            new ExtraccionIaCacheRepositorioFalso(),
            auditoria,
            new UnitOfWorkFalso(),
            NullLogger<DocumentAIRouterService>.Instance);
        return (router, auditoria, rasterizador);
    }

    [Fact]
    public async Task Un_escaneado_de_30_paginas_con_lo_minimo_en_las_primeras_lee_solo_4_paginas()
    {
        var proveedor = Proveedor(_ => Completa());
        var (router, auditoria, rasterizador) = CrearRouter(TipoContenidoDocumento.Escaneado, 30, proveedor);

        var resultado = await router.ProcesarAsync([1, 2, 3], "escaneado.pdf", "Certificado");

        resultado.EsExitoso.Should().BeTrue();
        rasterizador.UltimosIndicesRasterizados.Should().Equal(0, 1, 2, 29);
        proveedor.VecesLlamadoParaTexto.Should().Be(4);
        proveedor.VecesLlamadoParaEstructurado.Should().Be(1, "no hace falta pagar una segunda estructuración");
        proveedor.NombresArchivoRecibidosParaTexto.Should().Equal("pagina-1.png", "pagina-2.png", "pagina-3.png", "pagina-30.png");

        var registro = auditoria.Auditorias.Should().ContainSingle().Subject;
        registro.NumeroPaginas.Should().Be(4);
        registro.CosteEstimadoOcr.Should().Be(4 * CosteOcrPorPagina);
        registro.CosteEstimado.Should().Be(CosteEstructuracionPorLlamada);
        registro.Incidencias.Should().Contain("se leyeron 4 páginas de 30");
    }

    [Fact]
    public async Task El_texto_estructurado_lleva_las_paginas_leidas_en_el_orden_del_documento()
    {
        var proveedor = Proveedor(_ => Completa());
        var (router, _, _) = CrearRouter(TipoContenidoDocumento.Escaneado, 30, proveedor);

        await router.ProcesarAsync([1, 2, 3], "escaneado.pdf", "Certificado");

        proveedor.UltimoTextoRecibidoParaEstructurar.Should()
            .Contain("[pagina-1.png]\n\n[pagina-2.png]\n\n[pagina-3.png]\n\n[pagina-30.png]");
    }

    [Fact]
    public async Task Un_escaneado_de_30_paginas_amplia_al_resto_cuando_faltan_campos()
    {
        // La fecha y la firma solo aparecen al leer la página 5.
        var proveedor = Proveedor(texto => texto.Contains("[pagina-5.png]") ? Completa() : Incompleta());
        var (router, auditoria, rasterizador) = CrearRouter(TipoContenidoDocumento.Escaneado, 30, proveedor);

        var resultado = await router.ProcesarAsync([1, 2, 3], "escaneado.pdf", "Certificado");

        resultado.EsExitoso.Should().BeTrue();
        resultado.Valor.Campos.Should().ContainKey("fechaEmision");
        rasterizador.UltimosIndicesRasterizados.Should().Equal(Enumerable.Range(0, 3).Concat([29]).Concat(Enumerable.Range(3, 26)));
        proveedor.VecesLlamadoParaTexto.Should().Be(30);
        proveedor.VecesLlamadoParaEstructurado.Should().Be(2);

        var registro = auditoria.Auditorias.Should().ContainSingle().Subject;
        registro.NumeroPaginas.Should().Be(30);
        registro.CosteEstimadoOcr.Should().Be(30 * CosteOcrPorPagina);
        registro.CosteEstimado.Should().Be(2 * CosteEstructuracionPorLlamada, "se pagan las dos estructuraciones");
        registro.Incidencias.Should().Contain("se leyeron 30 páginas de 30");
    }

    [Fact]
    public async Task Un_escaneado_de_500_paginas_nunca_pasa_de_100_aunque_falten_campos()
    {
        var proveedor = Proveedor(_ => Incompleta());
        var (router, auditoria, rasterizador) = CrearRouter(TipoContenidoDocumento.Escaneado, 500, proveedor);

        var resultado = await router.ProcesarAsync([1, 2, 3], "escaneado-enorme.pdf", "Certificado");

        resultado.EsExitoso.Should().BeTrue("el tope limita la lectura, no tumba el documento");
        rasterizador.VecesLlamado.Should().Be(100);
        proveedor.VecesLlamadoParaTexto.Should().Be(100);
        rasterizador.UltimosIndicesRasterizados.Distinct().Should().HaveCount(100);
        rasterizador.UltimosIndicesRasterizados.Should().Contain(499, "la última página se lee siempre");
        rasterizador.UltimosIndicesRasterizados.Max().Should().Be(499);
        rasterizador.UltimosIndicesRasterizados.Where(i => i != 499).Max().Should().Be(98);

        var registro = auditoria.Auditorias.Should().ContainSingle().Subject;
        registro.NumeroPaginas.Should().Be(100);
        registro.CosteEstimadoOcr.Should().Be(100 * CosteOcrPorPagina);
        registro.Incidencias.Should().Contain("se leyeron 100 páginas de 500").And.Contain("máximo de 100");
    }

    [Fact]
    public async Task Un_escaneado_largo_sin_campos_pero_dentro_del_tope_lee_todas_las_paginas()
    {
        var proveedor = Proveedor(_ => Incompleta());
        var (router, auditoria, rasterizador) = CrearRouter(TipoContenidoDocumento.Escaneado, 100, proveedor);

        await router.ProcesarAsync([1, 2, 3], "escaneado.pdf", "Certificado");

        rasterizador.UltimosIndicesRasterizados.Distinct().Should().HaveCount(100);
        auditoria.Auditorias[0].NumeroPaginas.Should().Be(100);
        auditoria.Auditorias[0].Incidencias.Should().Contain("se leyeron 100 páginas de 100");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    public async Task Un_escaneado_de_4_paginas_o_menos_se_lee_entero_sin_rasterizar(int paginas)
    {
        var proveedor = Proveedor(_ => Incompleta());
        var (router, auditoria, rasterizador) = CrearRouter(TipoContenidoDocumento.Escaneado, paginas, proveedor);

        var resultado = await router.ProcesarAsync([1, 2, 3], "escaneado.pdf", "Certificado");

        resultado.EsExitoso.Should().BeTrue();
        rasterizador.VecesLlamado.Should().Be(0);
        proveedor.NombresArchivoRecibidosParaTexto.Should().Equal("escaneado.pdf");
        proveedor.VecesLlamadoParaEstructurado.Should().Be(1, "aunque falten campos, ya se leyó todo el documento");
        auditoria.Auditorias[0].NumeroPaginas.Should().Be(paginas);
        auditoria.Auditorias[0].Incidencias.Should().BeNull();
    }

    [Fact]
    public async Task Un_escaneado_de_5_paginas_ya_se_lee_por_paginas_y_se_salta_la_cuarta()
    {
        var proveedor = Proveedor(_ => Completa());
        var (router, auditoria, rasterizador) = CrearRouter(TipoContenidoDocumento.Escaneado, 5, proveedor);

        await router.ProcesarAsync([1, 2, 3], "escaneado.pdf", "Certificado");

        rasterizador.UltimosIndicesRasterizados.Should().Equal(0, 1, 2, 4);
        auditoria.Auditorias[0].NumeroPaginas.Should().Be(4);
        auditoria.Auditorias[0].Incidencias.Should().Contain("se leyeron 4 páginas de 5");
    }

    [Fact]
    public async Task Una_imagen_suelta_no_cambia_y_va_entera_al_ocr()
    {
        var proveedor = Proveedor(_ => Incompleta());
        var (router, _, rasterizador) = CrearRouter(TipoContenidoDocumento.Imagen, 1, proveedor);

        await router.ProcesarAsync([1, 2, 3], "foto.jpg", "Certificado");

        rasterizador.VecesLlamado.Should().Be(0);
        proveedor.NombresArchivoRecibidosParaTexto.Should().Equal("foto.jpg");
    }

    [Fact]
    public async Task Si_el_ocr_del_resto_falla_se_conserva_la_primera_lectura_y_se_cobra_lo_ya_gastado()
    {
        // Fallan las páginas a partir de la 10 (índice 9): se pagaron las páginas 4 a 9.
        var proveedor = Proveedor(
            _ => Incompleta(),
            nombre => nombre == "pagina-10.png"
                ? Result.Fallo<TextoExtraccionDto>(Error.Crear("Ocr.Fallo", "error de prueba"))
                : Result.Exito(new TextoExtraccionDto($"[{nombre}]", CosteOcrPorPagina)));
        var (router, auditoria, _) = CrearRouter(TipoContenidoDocumento.Escaneado, 30, proveedor);

        var resultado = await router.ProcesarAsync([1, 2, 3], "escaneado.pdf", "Certificado");

        resultado.EsExitoso.Should().BeTrue("la primera lectura ya era válida");
        proveedor.VecesLlamadoParaEstructurado.Should().Be(1);

        var registro = auditoria.Auditorias.Should().ContainSingle().Subject;
        registro.NumeroPaginas.Should().Be(4);
        registro.CosteEstimadoOcr.Should().Be((4 + 6) * CosteOcrPorPagina);
        registro.Incidencias.Should().Contain("Se intentó ampliar la lectura").And.Contain("Ocr.Fallo");

        // Un fallo transitorio no deja la lectura incompleta como definitiva en la caché.
        await router.ProcesarAsync([1, 2, 3], "escaneado.pdf", "Certificado");
        proveedor.VecesLlamadoParaEstructurado.Should().Be(2, "la segunda llamada no se sirve de la caché");
    }

    [Fact]
    public async Task Si_la_estructuracion_ampliada_falla_se_conserva_la_primera_lectura()
    {
        var llamadas = 0;
        var proveedor = Proveedor(_ => ++llamadas == 2
            ? Result.Fallo<ExtraccionEstructuradaDto>(Error.Crear("Ia.Fallo", "error de prueba"))
            : Incompleta());
        var (router, auditoria, _) = CrearRouter(TipoContenidoDocumento.Escaneado, 30, proveedor);

        var resultado = await router.ProcesarAsync([1, 2, 3], "escaneado.pdf", "Certificado");

        resultado.EsExitoso.Should().BeTrue();
        var registro = auditoria.Auditorias.Should().ContainSingle().Subject;
        registro.NumeroPaginas.Should().Be(4, "el texto que respalda el resultado es el de las 4 primeras");
        registro.CosteEstimadoOcr.Should().Be(30 * CosteOcrPorPagina, "las 26 páginas de la ampliación se pagaron");
        registro.Incidencias.Should().Contain("no se pudo estructurar");

        // Tampoco se cachea: el fallo de la segunda estructuración puede ser transitorio.
        await router.ProcesarAsync([1, 2, 3], "escaneado.pdf", "Certificado");
        proveedor.VecesLlamadoParaEstructurado.Should().Be(4, "la segunda llamada vuelve a leer y a estructurar dos veces: no se sirve de la caché");
    }

    [Fact]
    public async Task Si_la_lectura_ampliada_trae_menos_que_la_primera_se_conserva_la_primera_y_si_se_cachea()
    {
        var llamadas = 0;
        var proveedor = Proveedor(_ => ++llamadas == 1
            ? Result.Exito(new ExtraccionEstructuradaDto(
                "Certificado", new Dictionary<string, string?> { ["fechaEmision"] = "2026-01-15" }, 90, null, CosteEstructuracionPorLlamada))
            : Result.Exito(new ExtraccionEstructuradaDto(
                null, new Dictionary<string, string?>(), 90, null, CosteEstructuracionPorLlamada)));
        var (router, auditoria, _) = CrearRouter(TipoContenidoDocumento.Escaneado, 30, proveedor);

        var resultado = await router.ProcesarAsync([1, 2, 3], "escaneado.pdf", "Certificado");

        resultado.Valor.TipoDetectado.Should().Be("Certificado");
        resultado.Valor.Campos.Should().ContainKey("fechaEmision");
        var registro = auditoria.Auditorias.Should().ContainSingle().Subject;
        registro.NumeroPaginas.Should().Be(4);
        registro.CosteEstimado.Should().Be(2 * CosteEstructuracionPorLlamada);
        registro.Incidencias.Should().Contain("no mejoró");

        await router.ProcesarAsync([1, 2, 3], "escaneado.pdf", "Certificado");
        proveedor.VecesLlamadoParaEstructurado.Should().Be(2, "es un resultado estable: sí se sirve de la caché");
    }

    [Fact]
    public async Task La_lectura_parcial_lo_dice_en_las_notas_de_validacion_que_ve_el_revisor()
    {
        var proveedor = Proveedor(_ => Completa());
        var (router, _, _) = CrearRouter(TipoContenidoDocumento.Escaneado, 30, proveedor);

        var resultado = await router.ProcesarAsync([1, 2, 3], "escaneado.pdf", "Certificado");

        resultado.Valor.NotasValidacion.Should()
            .Contain("se leyeron 4 páginas de 30")
            .And.Contain("no apto");
        resultado.Valor.PaginasSinLeer.Should().Be(26, "el dato que llega a la revisión no sale de interpretar la nota");
    }

    [Fact]
    public async Task La_lectura_parcial_lo_sigue_siendo_al_repetirse_desde_la_cache()
    {
        var proveedor = Proveedor(_ => Completa());
        var (router, _, _) = CrearRouter(TipoContenidoDocumento.Escaneado, 30, proveedor);
        await router.ProcesarAsync([1, 2, 3], "escaneado.pdf", "Certificado");
        var llamadas = proveedor.VecesLlamadoParaEstructurado;

        var repetida = await router.ProcesarAsync([1, 2, 3], "escaneado.pdf", "Certificado");

        proveedor.VecesLlamadoParaEstructurado.Should().Be(llamadas, "control positivo: la segunda viene de la caché");
        repetida.Valor.PaginasSinLeer.Should().Be(26);
    }

    [Fact]
    public async Task Si_se_leyeron_todas_las_paginas_no_hay_aviso_de_lectura_parcial()
    {
        var proveedor = Proveedor(texto => texto.Contains("[pagina-5.png]") ? Completa() : Incompleta());
        var (router, _, _) = CrearRouter(TipoContenidoDocumento.Escaneado, 30, proveedor);

        var resultado = await router.ProcesarAsync([1, 2, 3], "escaneado.pdf", "Certificado");

        resultado.Valor.NotasValidacion.Should().BeNull();
        resultado.Valor.PaginasSinLeer.Should().Be(0);
    }

    [Fact]
    public async Task Un_digital_grande_sigue_pasando_por_el_localizador_y_no_por_el_ocr_por_paginas()
    {
        var proveedor = Proveedor(_ => Incompleta());
        var auditoria = new AuditoriaExtraccionIaRepositorioFalso();
        var rasterizador = new RasterizadorPaginasFalso();
        var paginas = Enumerable.Range(0, 30).Select(i => $"Página {i}").ToList();
        var router = new DocumentAIRouterService(
            new ClasificadorDocumentoServiceFalso(Result.Exito(new ClasificacionDocumentoDto(
                TipoContenidoDocumento.Digital, 30, Enumerable.Repeat(true, 30).ToArray()))),
            new ExtractorTextoDigitalServiceFalso(Result.Exito<IReadOnlyList<string>>(paginas)),
            new LocalizadorPaginasRelevantesService(),
            rasterizador,
            new DocumentAIProviderFactory([proveedor]),
            new ExtraccionIaCacheRepositorioFalso(),
            auditoria,
            new UnitOfWorkFalso(),
            NullLogger<DocumentAIRouterService>.Instance);

        await router.ProcesarAsync([1, 2, 3], "digital.pdf", "Certificado");

        rasterizador.VecesLlamado.Should().Be(0);
        proveedor.VecesLlamadoParaTexto.Should().Be(0);
        proveedor.VecesLlamadoParaEstructurado.Should().Be(1, "un digital no amplía lectura");
        auditoria.Auditorias[0].NumeroPaginas.Should().Be(30);
    }

    [Theory]
    [InlineData(null, "2026-01-15", "true", "tipoDetectado")]
    [InlineData("  ", "2026-01-15", "true", "tipoDetectado")]
    [InlineData("Certificado", null, "true", "fechaEmision")]
    [InlineData("Certificado", "2026-01-15", null, "tieneFirma")]
    [InlineData("Certificado", "2026-01-15", " ", "tieneFirma")]
    public void La_evidencia_minima_nombra_lo_que_falta(string? tipo, string? fecha, string? firma, string esperado)
    {
        var extraccion = new ExtraccionEstructuradaDto(
            tipo, new Dictionary<string, string?> { ["fechaEmision"] = fecha, ["tieneFirma"] = firma }, 90, null);

        EvidenciaMinimaLectura.Faltantes(extraccion).Should().Equal(esperado);
    }

    [Fact]
    public void Un_false_explicito_en_la_firma_y_sin_fecha_de_vencimiento_no_cuenta_como_falta()
    {
        var extraccion = new ExtraccionEstructuradaDto(
            "Certificado", new Dictionary<string, string?> { ["fechaEmision"] = "2026-01-15", ["tieneFirma"] = "false" }, 90, null);

        EvidenciaMinimaLectura.Faltan(extraccion).Should().BeFalse();
    }
}
