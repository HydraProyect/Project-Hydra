using CaeManager.Domain.DocumentosIa;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.DocumentosIa;

public class AuditoriaExtraccionIaTests
{
    private static string HashDeEjemplo() => new('b', AuditoriaExtraccionIa.LongitudHash);

    [Fact]
    public void Crea_un_registro_de_auditoria_con_los_datos_esperados()
    {
        var auditoria = AuditoriaExtraccionIa.Crear(
            HashDeEjemplo(), "Póliza de seguro", "anthropic", tiempoProcesamientoMs: 1500,
            costeEstimado: 0.03m, numeroPaginas: 3, confianzaGeneral: 92, incidencias: null);

        auditoria.HashSha256.Should().Be(HashDeEjemplo());
        auditoria.TipoEsperado.Should().Be("Póliza de seguro");
        auditoria.ProveedorCodigo.Should().Be("anthropic");
        auditoria.TiempoProcesamientoMs.Should().Be(1500);
        auditoria.CosteEstimado.Should().Be(0.03m);
        auditoria.NumeroPaginas.Should().Be(3);
        auditoria.ConfianzaGeneral.Should().Be(92);
    }

    [Fact]
    public void Permite_coste_estimado_nulo_cuando_el_proveedor_no_lo_calcula()
    {
        var auditoria = AuditoriaExtraccionIa.Crear(
            HashDeEjemplo(), "Certificado", "cache", 5, null, 1, 90, "Resultado servido desde caché documental.");

        auditoria.CosteEstimado.Should().BeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("hash-invalido")]
    public void Rechaza_un_hash_que_no_tenga_la_longitud_de_sha256(string hash)
    {
        var accion = () => AuditoriaExtraccionIa.Crear(hash, "Certificado", "anthropic", 100, null, 1, 80, null);

        accion.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Rechaza_un_codigo_de_proveedor_vacio()
    {
        var accion = () => AuditoriaExtraccionIa.Crear(HashDeEjemplo(), "Certificado", "   ", 100, null, 1, 80, null);

        accion.Should().Throw<ArgumentException>();
    }
}
