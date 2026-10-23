using CaeManager.Domain.ApiKeys;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.ApiKeys;

public class ClaveApiTests
{
    private static ClaveApi CrearClave() =>
        new("Integración ERP", "hydra_ab12cd", "hash-simulado", Guid.NewGuid(), DateTime.UtcNow.AddDays(30), "Integración de prueba");

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
        var accion = () => new ClaveApi(nombre, prefijo, hash, Guid.NewGuid(), DateTime.UtcNow.AddDays(30), "Integración de prueba");

        accion.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void No_se_puede_crear_una_clave_sin_usuario_creador()
    {
        var accion = () => new ClaveApi("nombre", "prefijo", "hash", Guid.Empty, DateTime.UtcNow.AddDays(30), "Integración de prueba");

        accion.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(366)]
    public void Rechaza_caducidad_pasada_o_superior_a_365_dias(int dias)
    {
        var accion = () => new ClaveApi("ERP", "prefijo", "hash", Guid.NewGuid(), DateTime.UtcNow.AddDays(dias), "Prueba");
        accion.Should().Throw<ArgumentException>().WithParameterName("caducaEnUtc");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(364)]
    [InlineData(365)]
    public void Acepta_caducidad_futura_hasta_365_dias(double dias)
    {
        // 365 días menos un minuto: el máximo legítimo no debe rechazarse por el instante de la comprobación.
        var caducidad = DateTime.UtcNow.AddDays(dias).AddMinutes(dias == 365 ? -1 : 0);
        var clave = new ClaveApi("ERP", "prefijo", "hash", Guid.NewGuid(), caducidad, "Prueba");
        clave.CaducaEnUtc.Should().Be(caducidad);
    }

    [Fact]
    public void Rechaza_caducidad_ausente_y_motivo_vacio()
    {
        var sinFecha = () => new ClaveApi("ERP", "prefijo", "hash", Guid.NewGuid(), default, "Prueba");
        sinFecha.Should().Throw<ArgumentException>().WithParameterName("caducaEnUtc");
        var sinMotivo = () => new ClaveApi("ERP", "prefijo", "hash", Guid.NewGuid(), DateTime.UtcNow.AddDays(1), "  ");
        sinMotivo.Should().Throw<ArgumentException>().WithParameterName("motivoEmision");
    }

    [Fact]
    public void La_vigencia_termina_exactamente_en_la_caducidad_y_la_revocacion_prevalece()
    {
        var clave = CrearClave();
        clave.EstaVigente(clave.CaducaEnUtc.AddTicks(-1)).Should().BeTrue();
        clave.EstaVigente(clave.CaducaEnUtc).Should().BeFalse();
        clave.EstaVigente(clave.CaducaEnUtc.AddTicks(1)).Should().BeFalse();
        clave.Revocar();
        clave.EstaVigente(DateTime.UtcNow).Should().BeFalse();
    }
}
