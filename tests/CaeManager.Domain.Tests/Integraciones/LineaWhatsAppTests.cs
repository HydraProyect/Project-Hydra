using CaeManager.Domain.Integraciones;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.Integraciones;

public class LineaWhatsAppTests
{
    private static LineaWhatsApp CrearLineaPool() =>
        new(Guid.NewGuid(), "111222333", "waba-1", "+34686543364", "token-eaag", ModoAsignacionLinea.PoolInbound);

    [Fact]
    public void Se_crea_con_los_identificadores_de_meta()
    {
        var conexionId = Guid.NewGuid();

        var linea = new LineaWhatsApp(conexionId, " 111222333 ", "waba-1", "+34686543364", "token-eaag",
            ModoAsignacionLinea.PoolInbound, mensajeAutoTriage: "¿De qué cliente nos escribes?");

        linea.ConexionIntegracionId.Should().Be(conexionId);
        linea.PhoneNumberId.Should().Be("111222333");
        linea.WabaId.Should().Be("waba-1");
        linea.NumeroTelefono.Should().Be("+34686543364");
        linea.TokenAcceso.Should().Be("token-eaag");
        linea.MensajeAutoTriage.Should().Be("¿De qué cliente nos escribes?");
        linea.ComercialAsignadoId.Should().BeNull();
    }

    [Fact]
    public void GestorFijo_exige_comercial_dueno()
    {
        var accion = () => new LineaWhatsApp(Guid.NewGuid(), "111", "waba", "+34600", "token",
            ModoAsignacionLinea.GestorFijo, comercialAsignadoId: null);

        accion.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Cambiar_a_PoolInbound_limpia_el_comercial_dueno()
    {
        var linea = new LineaWhatsApp(Guid.NewGuid(), "111", "waba", "+34600", "token",
            ModoAsignacionLinea.GestorFijo, Guid.NewGuid());

        linea.ConfigurarAsignacion(ModoAsignacionLinea.PoolInbound, null);

        linea.ComercialAsignadoId.Should().BeNull();
    }

    [Fact]
    public void Rechaza_token_vacio()
    {
        var accion = () => CrearLineaPool().ActualizarToken(" ");

        accion.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ReemplazarMiembrosPool_anade_quita_y_deduplica()
    {
        var linea = CrearLineaPool();
        var usuario1 = Guid.NewGuid();
        var usuario2 = Guid.NewGuid();
        var usuario3 = Guid.NewGuid();
        linea.ReemplazarMiembrosPool([usuario1, usuario2]);

        linea.ReemplazarMiembrosPool([usuario2, usuario3, usuario3, Guid.Empty]);

        linea.MiembrosPool.Select(m => m.UsuarioId).Should().BeEquivalentTo([usuario2, usuario3]);
    }

    [Fact]
    public void El_mensaje_auto_triage_en_blanco_se_normaliza_a_null()
    {
        var linea = CrearLineaPool();

        linea.EstablecerMensajeAutoTriage("   ");

        linea.MensajeAutoTriage.Should().BeNull();
    }
}
