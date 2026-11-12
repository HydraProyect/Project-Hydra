using CaeManager.Domain.Integraciones;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.Integraciones;

/// <summary>Auditoría módulo 6: el nonce de un solo uso que reemplaza el "state" cifrado sin ligar a nadie del flujo OAuth de Microsoft 365 (ver ConectarMicrosoft365Endpoints).</summary>
public class SolicitudConexionMicrosoft365Tests
{
    private static readonly DateTime Ahora = new(2026, 8, 30, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Se_crea_con_los_datos_informados_y_expira_a_los_15_minutos()
    {
        var usuarioId = Guid.NewGuid();
        var clienteId = Guid.NewGuid();
        var gestorPropietarioId = Guid.NewGuid();

        var solicitud = new SolicitudConexionMicrosoft365(usuarioId, clienteId, gestorPropietarioId, Ahora);

        solicitud.UsuarioSolicitanteId.Should().Be(usuarioId);
        solicitud.ClienteId.Should().Be(clienteId);
        solicitud.GestorPropietarioId.Should().Be(gestorPropietarioId);
        solicitud.FechaExpiracionUtc.Should().Be(Ahora + SolicitudConexionMicrosoft365.DuracionValidez);
    }

    [Fact]
    public void Rechaza_un_usuario_solicitante_vacio()
    {
        var accion = () => new SolicitudConexionMicrosoft365(Guid.Empty, null, null, Ahora);

        accion.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Es_valida_para_el_mismo_usuario_dentro_de_la_ventana()
    {
        var usuarioId = Guid.NewGuid();
        var solicitud = new SolicitudConexionMicrosoft365(usuarioId, null, null, Ahora);

        solicitud.EsValidaPara(usuarioId, Ahora.AddMinutes(5)).Should().BeTrue();
    }

    [Fact]
    public void No_es_valida_pasada_la_ventana_de_expiracion()
    {
        var usuarioId = Guid.NewGuid();
        var solicitud = new SolicitudConexionMicrosoft365(usuarioId, null, null, Ahora);

        solicitud.EsValidaPara(usuarioId, Ahora + SolicitudConexionMicrosoft365.DuracionValidez).Should().BeFalse();
    }

    /// <summary>
    /// Propiedad central del fix (auditoría módulo 6): aunque alguien consiga
    /// leer o adivinar el "state", completar el callback como OTRO usuario no
    /// debe bastar — es justo lo que impide el OAuth account-linking CSRF
    /// (un atacante autoriza su propio buzón y hace que la víctima complete
    /// el callback).
    /// </summary>
    [Fact]
    public void No_es_valida_para_un_usuario_distinto_del_que_la_creo()
    {
        var solicitud = new SolicitudConexionMicrosoft365(Guid.NewGuid(), null, null, Ahora);

        solicitud.EsValidaPara(Guid.NewGuid(), Ahora.AddMinutes(1)).Should().BeFalse();
    }

    [Fact]
    public void Guarda_el_buzon_compartido_sin_espacios_alrededor()
    {
        var solicitud = new SolicitudConexionMicrosoft365(Guid.NewGuid(), null, null, Ahora, "  piloto@tenant.com ");

        solicitud.BuzonCompartido.Should().Be("piloto@tenant.com");
    }

    [Theory]
    [InlineData("   ")]
    [InlineData("")]
    public void Rechaza_un_buzon_compartido_en_blanco(string buzonCompartido)
    {
        var accion = () => new SolicitudConexionMicrosoft365(Guid.NewGuid(), null, null, Ahora, buzonCompartido);

        accion.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Rechaza_un_buzon_compartido_demasiado_largo()
    {
        var direccion = new string('a', ConexionIntegracion.LongitudMaximaBuzonEmail) + "@tenant.com";

        var accion = () => new SolicitudConexionMicrosoft365(Guid.NewGuid(), null, null, Ahora, direccion);

        accion.Should().Throw<ArgumentException>();
    }

    /// <summary>Un buzón compartido no es el buzón personal de un Gestor CAE: las dos cosas a la vez no tienen sentido.</summary>
    [Fact]
    public void Rechaza_un_buzon_compartido_con_Gestor_propietario()
    {
        var accion = () => new SolicitudConexionMicrosoft365(Guid.NewGuid(), null, Guid.NewGuid(), Ahora, "piloto@tenant.com");

        accion.Should().Throw<ArgumentException>();
    }
}
