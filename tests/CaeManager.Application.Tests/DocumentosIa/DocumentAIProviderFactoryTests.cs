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

    [Fact]
    public void ObtenerPorCapacidad_usa_el_orden_declarado_y_no_el_de_registro()
    {
        var desconocido = new ProveedorIaFalso("a-proveedor-nuevo", CapacidadesProveedorIa.OcrImagenAEscaneado | CapacidadesProveedorIa.ExtraccionEstructurada);
        var anthropic = new ProveedorIaFalso("anthropic", CapacidadesProveedorIa.OcrImagenAEscaneado | CapacidadesProveedorIa.ExtraccionEstructurada);
        var falso = new ProveedorIaFalso("falso-e2e", CapacidadesProveedorIa.OcrImagenAEscaneado | CapacidadesProveedorIa.ExtraccionEstructurada);

        // Orden de registro deliberadamente "malo", y elegido para que ni el
        // orden de registro ni el alfabético coincidan con el de la tabla: si la
        // tabla se ignorase, "a-proveedor-nuevo" o "anthropic" irían delante.
        var factory = new DocumentAIProviderFactory([desconocido, anthropic, falso]);

        foreach (var capacidad in new[] { CapacidadesProveedorIa.ExtraccionEstructurada, CapacidadesProveedorIa.OcrImagenAEscaneado })
        {
            factory.ObtenerPorCapacidad(capacidad)
                .Select(p => p.Codigo).Should().Equal(["falso-e2e", "anthropic", "a-proveedor-nuevo"], $"la tabla manda para {capacidad}");
        }
    }

    [Fact]
    public void ObtenerPorCapacidad_excluye_a_los_proveedores_sin_credencial()
    {
        var sinClave = new ProveedorIaFalso("anthropic", CapacidadesProveedorIa.ExtraccionEstructurada, estaDisponible: false);
        var conClave = new ProveedorIaFalso("falso-e2e", CapacidadesProveedorIa.ExtraccionEstructurada);
        var factory = new DocumentAIProviderFactory([sinClave, conClave]);

        // Sin el filtro, Anthropic seguiría en la lista y se llevaría el
        // trabajo de los reintentos pese a no poder atenderlo.
        factory.ObtenerPorCapacidad(CapacidadesProveedorIa.ExtraccionEstructurada)
            .Select(p => p.Codigo).Should().Equal("falso-e2e");
    }

    [Fact]
    public void ObtenerPorCapacidad_ordena_los_proveedores_desconocidos_por_codigo_detras_de_los_declarados()
    {
        var nuevoZ = new ProveedorIaFalso("z-proveedor-nuevo", CapacidadesProveedorIa.ExtraccionEstructurada);
        var nuevoA = new ProveedorIaFalso("a-proveedor-nuevo", CapacidadesProveedorIa.ExtraccionEstructurada);
        var conocido = new ProveedorIaFalso("anthropic", CapacidadesProveedorIa.ExtraccionEstructurada);
        var factory = new DocumentAIProviderFactory([nuevoZ, conocido, nuevoA]);

        factory.ObtenerPorCapacidad(CapacidadesProveedorIa.ExtraccionEstructurada)
            .Select(p => p.Codigo).Should().Equal("anthropic", "a-proveedor-nuevo", "z-proveedor-nuevo");
    }
}