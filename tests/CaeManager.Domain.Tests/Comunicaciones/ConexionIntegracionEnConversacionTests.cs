using CaeManager.Domain.Comunicaciones;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.Comunicaciones;

/// <summary>Cubre solo lo nuevo de P3-33 sobre Conversacion/Mensaje — no reabre el resto del módulo, sin tests previos.</summary>
public class ConexionIntegracionEnConversacionTests
{
    [Fact]
    public void AgregarMensaje_admite_un_mensajeExternoId_opcional()
    {
        var conversacion = new Conversacion("Consulta", clienteId: Guid.NewGuid());

        var mensaje = conversacion.AgregarMensaje(DireccionMensaje.Entrante, CanalConversacion.Correo, "cliente@ejemplo.com", "<p>hola</p>", mensajeExternoId: "graph-msg-1");

        mensaje.MensajeExternoId.Should().Be("graph-msg-1");
    }

    [Fact]
    public void AsociarConexion_ata_el_hilo_al_buzon_y_al_hiloExterno()
    {
        var conversacion = new Conversacion("Consulta", clienteId: Guid.NewGuid());
        var conexionId = Guid.NewGuid();

        conversacion.AsociarConexion(conexionId, "graph-thread-1");

        conversacion.ConexionIntegracionId.Should().Be(conexionId);
        conversacion.HiloExternoId.Should().Be("graph-thread-1");
    }

    [Fact]
    public void AsociarConexion_rechaza_una_conexion_vacia()
    {
        var conversacion = new Conversacion("Consulta");

        var accion = () => conversacion.AsociarConexion(Guid.Empty, "graph-thread-1");

        accion.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void AsociarConexion_rechaza_un_hiloExterno_vacio()
    {
        var conversacion = new Conversacion("Consulta");

        var accion = () => conversacion.AsociarConexion(Guid.NewGuid(), " ");

        accion.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Mensaje_rechaza_un_mensajeExternoId_mas_largo_que_el_maximo()
    {
        var accion = () => new Mensaje(
            Guid.NewGuid(), DireccionMensaje.Entrante, CanalConversacion.Correo, "cliente@ejemplo.com", "<p>hola</p>", DateTime.UtcNow,
            new string('a', Mensaje.LongitudMaximaMensajeExternoId + 1));

        accion.Should().Throw<ArgumentException>();
    }
}
