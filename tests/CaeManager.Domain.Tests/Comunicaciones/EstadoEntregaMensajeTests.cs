using CaeManager.Domain.Comunicaciones;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.Comunicaciones;

public class EstadoEntregaMensajeTests
{
    private static Mensaje CrearMensaje(DireccionMensaje direccion) =>
        new(Guid.NewGuid(), direccion, "+34686543364", "Hola",
            new DateTime(2026, 8, 4, 10, 0, 0, DateTimeKind.Utc), "wamid.test");

    [Fact]
    public void Progresa_de_Enviado_a_Entregado_y_Leido()
    {
        var mensaje = CrearMensaje(DireccionMensaje.Saliente);

        mensaje.ActualizarEstadoEntrega(EstadoEntregaMensaje.Enviado);
        mensaje.ActualizarEstadoEntrega(EstadoEntregaMensaje.Entregado);
        mensaje.ActualizarEstadoEntrega(EstadoEntregaMensaje.Leido);

        mensaje.EstadoEntrega.Should().Be(EstadoEntregaMensaje.Leido);
    }

    [Fact]
    public void Un_Entregado_tardio_no_pisa_un_Leido()
    {
        var mensaje = CrearMensaje(DireccionMensaje.Saliente);
        mensaje.ActualizarEstadoEntrega(EstadoEntregaMensaje.Leido);

        mensaje.ActualizarEstadoEntrega(EstadoEntregaMensaje.Entregado);

        mensaje.EstadoEntrega.Should().Be(EstadoEntregaMensaje.Leido);
    }

    [Fact]
    public void Fallido_es_terminal_y_guarda_el_motivo()
    {
        var mensaje = CrearMensaje(DireccionMensaje.Saliente);
        mensaje.ActualizarEstadoEntrega(EstadoEntregaMensaje.Fallido, "Número no registrado en WhatsApp");

        mensaje.ActualizarEstadoEntrega(EstadoEntregaMensaje.Entregado);

        mensaje.EstadoEntrega.Should().Be(EstadoEntregaMensaje.Fallido);
        mensaje.ErrorEntrega.Should().Be("Número no registrado en WhatsApp");
    }

    [Fact]
    public void Fallido_puede_llegar_despues_de_Enviado()
    {
        var mensaje = CrearMensaje(DireccionMensaje.Saliente);
        mensaje.ActualizarEstadoEntrega(EstadoEntregaMensaje.Enviado);

        mensaje.ActualizarEstadoEntrega(EstadoEntregaMensaje.Fallido, "Fuera de ventana");

        mensaje.EstadoEntrega.Should().Be(EstadoEntregaMensaje.Fallido);
    }

    [Fact]
    public void El_motivo_del_fallo_se_trunca_a_la_longitud_maxima()
    {
        var mensaje = CrearMensaje(DireccionMensaje.Saliente);

        mensaje.ActualizarEstadoEntrega(EstadoEntregaMensaje.Fallido, new string('x', 600));

        mensaje.ErrorEntrega.Should().HaveLength(Mensaje.LongitudMaximaErrorEntrega);
    }

    [Fact]
    public void Un_status_sobre_un_mensaje_entrante_se_ignora()
    {
        var mensaje = CrearMensaje(DireccionMensaje.Entrante);

        mensaje.ActualizarEstadoEntrega(EstadoEntregaMensaje.Leido);

        mensaje.EstadoEntrega.Should().BeNull();
    }
}
