using CaeManager.Domain.ApiKeys;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.ApiKeys;

public class ClaveApiTests
{
    private static ClaveApi CrearClave() =>
        new("Integración ERP", "hydra_ab12cd", "hash-simulado", Guid.NewGuid());

    [Fact]
    public void Una_clave_nueva_esta_activa_y_sin_uso()
    {
        var clave = CrearClave();

        clave.EstaActiva.Should().BeTrue();
        clave.RevocadaEnUtc.Should().BeNull();
        clave.UltimoUsoUtc.Should().BeNull();
    }

    [Fact]
    public void Revocar_desactiva_la_clave()
    {
        var clave = CrearClave();

        clave.Revocar();

        clave.EstaActiva.Should().BeFalse();
        clave.RevocadaEnUtc.Should().NotBeNull();
    }

    [Fact]
    public void Revocar_dos_veces_no_pisa_la_fecha_original()
    {
        var clave = CrearClave();

        clave.Revocar();
        var primeraRevocacion = clave.RevocadaEnUtc;
        clave.Revocar();

        clave.RevocadaEnUtc.Should().Be(primeraRevocacion);
    }

    [Fact]
    public void RegistrarUso_fija_la_fecha_de_ultimo_uso()
    {
        var clave = CrearClave();

        clave.RegistrarUso();

        clave.UltimoUsoUtc.Should().NotBeNull();
    }

    [Theory]
    [InlineData("", "prefijo", "hash", true)]
    [InlineData("nombre", "", "hash", true)]
    [InlineData("nombre", "prefijo", "", true)]
    public void No_se_puede_crear_una_clave_con_campos_vacios(string nombre, string prefijo, string hash, bool _)
    {
        var accion = () => new ClaveApi(nombre, prefijo, hash, Guid.NewGuid());

        accion.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void No_se_puede_crear_una_clave_sin_usuario_creador()
    {
        var accion = () => new ClaveApi("nombre", "prefijo", "hash", Guid.Empty);

        accion.Should().Throw<ArgumentException>();
    }
}
