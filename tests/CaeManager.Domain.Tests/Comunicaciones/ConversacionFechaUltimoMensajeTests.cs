using CaeManager.Domain.Comunicaciones;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.Comunicaciones;

public class ConversacionFechaUltimoMensajeTests
{
    [Fact]
    public void AgregarMensaje_con_fecha_pasada_deja_la_fecha_de_alta_hasta_que_se_alinea()
    {
        var conversacion = new Conversacion("Aviso");
        var pasada = DateTime.UtcNow.AddDays(-10);
        conversacion.AgregarMensaje(DireccionMensaje.Entrante, CanalConversacion.Correo, "a@b.local", "<p>x</p>", pasada);

        conversacion.FechaUltimoMensajeUtc.Should().BeAfter(pasada);

        conversacion.AlinearFechaConLosMensajes();

        conversacion.FechaUltimoMensajeUtc.Should().Be(pasada);
    }

    [Fact]
    public void AlinearFechaConLosMensajes_toma_la_del_mensaje_mas_reciente()
    {
        var conversacion = new Conversacion("Aviso");
        var primera = DateTime.UtcNow.AddDays(-20);
        var ultima = DateTime.UtcNow.AddDays(-17);
        conversacion.AgregarMensaje(DireccionMensaje.Saliente, CanalConversacion.Correo, "a@b.local", "<p>x</p>", primera);
        conversacion.AgregarMensaje(DireccionMensaje.Entrante, CanalConversacion.Correo, "c@d.local", "<p>y</p>", ultima);

        conversacion.AlinearFechaConLosMensajes();

        conversacion.FechaUltimoMensajeUtc.Should().Be(ultima);
    }

    [Fact]
    public void AlinearFechaConLosMensajes_sin_mensajes_no_toca_la_fecha()
    {
        var conversacion = new Conversacion("Aviso");
        var antes = conversacion.FechaUltimoMensajeUtc;

        conversacion.AlinearFechaConLosMensajes();

        conversacion.FechaUltimoMensajeUtc.Should().Be(antes);
    }
}
