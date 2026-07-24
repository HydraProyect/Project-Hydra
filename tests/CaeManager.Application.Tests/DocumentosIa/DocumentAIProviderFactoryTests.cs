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
}
