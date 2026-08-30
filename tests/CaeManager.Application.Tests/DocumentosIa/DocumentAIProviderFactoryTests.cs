using CaeManager.Application.DocumentosIa.Common;
using CaeManager.Domain.DocumentosIa;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.DocumentosIa;

public class DocumentAIProviderFactoryTests
{
    [Fact]
    public void Resuelve_un_proveedor_registrado_por_codigo_sin_distinguir_mayusculas()
    {
        var anthropic = new ProveedorIaFalso("anthropic", CapacidadesProveedorIa.ExtraccionEstructurada);
        var factory = new DocumentAIProviderFactory([anthropic]);

        var resultado = factory.Resolver("ANTHROPIC");

        resultado.EsExitoso.Should().BeTrue();
        resultado.Valor.Should().BeSameAs(anthropic);
    }

    [Fact]
    public void Falla_de_forma_controlada_si_no_existe_un_proveedor_con_ese_codigo()
    {
        var factory = new DocumentAIProviderFactory([new ProveedorIaFalso("anthropic", CapacidadesProveedorIa.ExtraccionEstructurada)]);

        var resultado = factory.Resolver("gemini-2-5-flash");

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("DocumentAIProvider.NoEncontrado");
    }

    [Fact]
    public void ObtenerPorCapacidad_devuelve_solo_los_proveedores_que_la_declaran()
    {
        var ocr = new ProveedorIaFalso("mistral-ocr", CapacidadesProveedorIa.OcrImagenAEscaneado);
        var estructuracion = new ProveedorIaFalso("gemini-2-5-flash", CapacidadesProveedorIa.ExtraccionEstructurada);
        var ambas = new ProveedorIaFalso("anthropic", CapacidadesProveedorIa.OcrImagenAEscaneado | CapacidadesProveedorIa.ExtraccionEstructurada);
        var factory = new DocumentAIProviderFactory([ocr, estructuracion, ambas]);

        var conOcr = factory.ObtenerPorCapacidad(CapacidadesProveedorIa.OcrImagenAEscaneado);

        conOcr.Should().BeEquivalentTo([ocr, ambas]);
    }

    [Fact]
    public void ObtenerPorCapacidad_devuelve_vacio_si_ningun_proveedor_la_declara()
    {
        var factory = new DocumentAIProviderFactory([new ProveedorIaFalso("mistral-ocr", CapacidadesProveedorIa.OcrImagenAEscaneado)]);

        var resultado = factory.ObtenerPorCapacidad(CapacidadesProveedorIa.ComparacionDocumentos);

        resultado.Should().BeEmpty();
    }

    /// <summary>
    /// Reproduce el reparto real del contenedor: Mistral se registra ANTES que
    /// Anthropic y declara también extracción estructurada, así que mientras el
    /// orden salía del contenedor, el primario de estructuración era Mistral —
    /// aunque el comentario del registro afirmara que era Anthropic. Quién
    /// recibe los datos del documento no puede depender del orden de unas
    /// líneas de DI.
    /// </summary>
    [Fact]
    public void ObtenerPorCapacidad_usa_el_orden_declarado_y_no_el_de_registro()
    {
        var mistral = new ProveedorIaFalso("mistral-ocr", CapacidadesProveedorIa.OcrImagenAEscaneado | CapacidadesProveedorIa.ExtraccionEstructurada);
        var anthropic = new ProveedorIaFalso("anthropic", CapacidadesProveedorIa.OcrImagenAEscaneado | CapacidadesProveedorIa.ExtraccionEstructurada);
        var gemini = new ProveedorIaFalso("gemini", CapacidadesProveedorIa.ExtraccionEstructurada);

        // Orden de registro deliberadamente "malo": el mismo que el DI real.
        var factory = new DocumentAIProviderFactory([mistral, anthropic, gemini]);

        factory.ObtenerPorCapacidad(CapacidadesProveedorIa.ExtraccionEstructurada)
            .Select(p => p.Codigo).Should().Equal("anthropic", "gemini", "mistral-ocr");

        // Para OCR sí manda Mistral, que es el especializado — el orden es por
        // capacidad, no uno global.
        factory.ObtenerPorCapacidad(CapacidadesProveedorIa.OcrImagenAEscaneado)
            .Select(p => p.Codigo).Should().Equal("mistral-ocr", "anthropic");
    }

    [Fact]
    public void ObtenerPorCapacidad_excluye_a_los_proveedores_sin_credencial()
    {
        var sinClave = new ProveedorIaFalso("anthropic", CapacidadesProveedorIa.ExtraccionEstructurada, estaDisponible: false);
        var conClave = new ProveedorIaFalso("gemini", CapacidadesProveedorIa.ExtraccionEstructurada);
        var factory = new DocumentAIProviderFactory([sinClave, conClave]);

        // Sin el filtro, Anthropic iría primero por orden declarado y se
        // llevaría el trabajo pese a no poder atenderlo.
        factory.ObtenerPorCapacidad(CapacidadesProveedorIa.ExtraccionEstructurada)
            .Select(p => p.Codigo).Should().Equal("gemini");
    }

    [Fact]
    public void ObtenerPorCapacidad_ordena_los_proveedores_desconocidos_por_codigo_al_final()
    {
        var nuevoZ = new ProveedorIaFalso("z-proveedor-nuevo", CapacidadesProveedorIa.ExtraccionEstructurada);
        var nuevoA = new ProveedorIaFalso("a-proveedor-nuevo", CapacidadesProveedorIa.ExtraccionEstructurada);
        var conocido = new ProveedorIaFalso("gemini", CapacidadesProveedorIa.ExtraccionEstructurada);
        var factory = new DocumentAIProviderFactory([nuevoZ, conocido, nuevoA]);

        factory.ObtenerPorCapacidad(CapacidadesProveedorIa.ExtraccionEstructurada)
            .Select(p => p.Codigo).Should().Equal("gemini", "a-proveedor-nuevo", "z-proveedor-nuevo");
    }
}
