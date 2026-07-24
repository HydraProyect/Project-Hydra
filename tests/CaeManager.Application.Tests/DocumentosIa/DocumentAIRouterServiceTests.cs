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

    private static (DocumentAIRouterService Router, ExtraccionIaCacheRepositorioFalso Cache, AuditoriaExtraccionIaRepositorioFalso Auditoria) CrearRouterConDependencias(
        Result<ClasificacionDocumentoDto> clasificacion, Result<string> textoDigital, params IDocumentAIProvider[] proveedores)
    {
        var cache = new ExtraccionIaCacheRepositorioFalso();
        var auditoria = new AuditoriaExtraccionIaRepositorioFalso();
        var router = new DocumentAIRouterService(
            new ClasificadorDocumentoServiceFalso(clasificacion),
            new ExtractorTextoDigitalServiceFalso(textoDigital),
            new DocumentAIProviderFactory(proveedores),
            cache,
            auditoria,
            new UnitOfWorkFalso(),
            NullLogger<DocumentAIRouterService>.Instance);
        return (router, cache, auditoria);
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
            resultadoTexto: Result.Exito("texto vía ocr"));
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
        var (router, _, auditoria) = CrearRouterConDependencias(Clasificacion(TipoContenidoDocumento.Digital, true), Result.Exito("texto"), proveedor);

        await router.ProcesarAsync([1, 2, 3], "documento.pdf", "Póliza de seguro");

        auditoria.Auditorias.Should().ContainSingle();
        auditoria.Auditorias[0].ProveedorCodigo.Should().Be("anthropic");
        auditoria.Auditorias[0].ConfianzaGeneral.Should().Be(95);
        auditoria.Auditorias[0].CosteEstimado.Should().Be(0.02m);
        auditoria.Auditorias[0].NumeroPaginas.Should().Be(1);
    }

    [Fact]
    public async Task Registra_una_auditoria_con_proveedor_ninguno_cuando_falla()
    {
        var (router, _, auditoria) = CrearRouterConDependencias(Clasificacion(TipoContenidoDocumento.Digital, true), Result.Exito("texto"));

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
        var (router, _, auditoria) = CrearRouterConDependencias(Clasificacion(TipoContenidoDocumento.Digital, true), Result.Exito("texto"), proveedor);
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
        var (router, _, _) = CrearRouterConDependencias(Clasificacion(TipoContenidoDocumento.Digital, true), Result.Exito("texto"), proveedor);

        await router.ProcesarAsync([1, 2, 3], "documento.pdf", "Póliza de seguro");
        await router.ProcesarAsync([4, 5, 6], "documento.pdf", "Póliza de seguro");

        proveedor.VecesLlamadoParaEstructurado.Should().Be(2);
    }
}
