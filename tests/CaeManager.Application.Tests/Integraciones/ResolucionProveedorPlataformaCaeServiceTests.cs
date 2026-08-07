using CaeManager.Application.Integraciones;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Integraciones;

public class ResolucionProveedorPlataformaCaeServiceTests
{
    [Theory]
    [InlineData("https://app.twind.io/login", "app.twind.io")]
    [InlineData("http://ctaima.com", "ctaima.com")]
    [InlineData("app.twind.io/login", "app.twind.io")]
    [InlineData("  ergasia.es  ", "ergasia.es")]
    public void ExtraerHost_acepta_url_completa_o_host_suelto(string entrada, string hostEsperado)
    {
        ResolucionProveedorPlataformaCaeService.ExtraerHost(entrada).Should().Be(hostEsperado);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ExtraerHost_devuelve_null_para_entrada_vacia(string? entrada)
    {
        ResolucionProveedorPlataformaCaeService.ExtraerHost(entrada).Should().BeNull();
    }

    [Fact]
    public void CoincideHost_es_verdadero_para_coincidencia_exacta()
    {
        ResolucionProveedorPlataformaCaeService.CoincideHost("ergasia.es", "ergasia.es").Should().BeTrue();
    }

    [Fact]
    public void CoincideHost_es_verdadero_para_subdominio_del_cliente()
    {
        ResolucionProveedorPlataformaCaeService.CoincideHost("cliente123.ergasia.es", "ergasia.es").Should().BeTrue();
    }

    [Fact]
    public void CoincideHost_no_confunde_un_sufijo_de_cadena_con_un_subdominio_real()
    {
        // "notergasia.es" contiene "ergasia.es" como sufijo de cadena, pero no
        // es un subdominio (falta el punto delimitador) — no debe coincidir.
        ResolucionProveedorPlataformaCaeService.CoincideHost("notergasia.es", "ergasia.es").Should().BeFalse();
    }

    [Fact]
    public void CoincideHost_ignora_mayusculas()
    {
        ResolucionProveedorPlataformaCaeService.CoincideHost("APP.TWIND.IO", "app.twind.io").Should().BeTrue();
    }

    [Fact]
    public void Resolver_devuelve_vacio_sin_ningun_dominio_coincidente()
    {
        var resultado = ResolucionProveedorPlataformaCaeService.Resolver(
            "desconocido.example.com", dominios: [], proveedores: []);

        resultado.Should().BeEmpty();
    }

    [Fact]
    public void Resolver_devuelve_un_unico_candidato_cuando_solo_un_proveedor_coincide()
    {
        var dokifyId = Guid.NewGuid();
        var nalandaId = Guid.NewGuid();
        var dominios = new[]
        {
            new ResolucionProveedorPlataformaCaeService.ParDominioProveedor(dokifyId, "dokify.net"),
            new ResolucionProveedorPlataformaCaeService.ParDominioProveedor(nalandaId, "nalandaglobal.com"),
        };
        var proveedores = new[]
        {
            new ProveedorPlataformaCaeCandidatoDto(dokifyId, "Dokify", true),
            new ProveedorPlataformaCaeCandidatoDto(nalandaId, "Nalanda", true),
        };

        var resultado = ResolucionProveedorPlataformaCaeService.Resolver("dokify.net", dominios, proveedores);

        resultado.Should().ContainSingle().Which.Nombre.Should().Be("Dokify");
    }

    [Fact]
    public void Resolver_devuelve_varios_candidatos_cuando_dos_proveedores_comparten_dominio()
    {
        // Caso real: Sabentis y Quirón Prevención comparten quironprevencion.com.
        var sabentisId = Guid.NewGuid();
        var quironId = Guid.NewGuid();
        var dominios = new[]
        {
            new ResolucionProveedorPlataformaCaeService.ParDominioProveedor(sabentisId, "quironprevencion.com"),
            new ResolucionProveedorPlataformaCaeService.ParDominioProveedor(quironId, "quironprevencion.com"),
        };
        var proveedores = new[]
        {
            new ProveedorPlataformaCaeCandidatoDto(sabentisId, "Sabentis", true),
            new ProveedorPlataformaCaeCandidatoDto(quironId, "Quirón Prevención", true),
        };

        var resultado = ResolucionProveedorPlataformaCaeService.Resolver("quironprevencion.com", dominios, proveedores);

        resultado.Should().HaveCount(2);
        resultado.Select(p => p.Nombre).Should().BeEquivalentTo("Sabentis", "Quirón Prevención");
    }
}
