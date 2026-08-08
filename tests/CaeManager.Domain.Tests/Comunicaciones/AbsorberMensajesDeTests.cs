using CaeManager.Domain.Comunicaciones;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.Comunicaciones;

/// <summary>Conversation Matching Engine (docs/COMUNICACIONES.md § 13.2) — mecánica de fusión al confirmar una vinculación.</summary>
public class AbsorberMensajesDeTests
{
    [Fact]
    public void AbsorberMensajesDe_traslada_los_mensajes_del_origen_conservando_su_canal()
    {
        var clienteId = Guid.NewGuid();
        var origen = Conversacion.CrearWhatsApp("+34600111222", Guid.NewGuid(), clienteId, Guid.NewGuid());
        var mensajeWhatsApp = origen.AgregarMensaje(DireccionMensaje.Entrante, CanalConversacion.WhatsApp, "+34600111222", "Hola, ¿cuándo es la próxima visita?");

        var destino = new Conversacion("Consulta sobre visita", clienteId);
        destino.AgregarMensaje(DireccionMensaje.Entrante, CanalConversacion.Correo, "cliente@empresa.test", "Correo original");

        destino.AbsorberMensajesDe(origen);

        destino.Mensajes.Should().Contain(m => m.Id == mensajeWhatsApp.Id);
        mensajeWhatsApp.ConversacionId.Should().Be(destino.Id);
        mensajeWhatsApp.Canal.Should().Be(CanalConversacion.WhatsApp);
        origen.Mensajes.Should().BeEmpty();
    }

    [Fact]
    public void AbsorberMensajesDe_actualiza_fecha_ultimo_mensaje_del_destino_si_el_origen_es_mas_reciente()
    {
        var clienteId = Guid.NewGuid();
        var destino = new Conversacion("Consulta antigua", clienteId);
        destino.AgregarMensaje(DireccionMensaje.Entrante, CanalConversacion.Correo, "cliente@empresa.test", "Mensaje viejo", DateTime.UtcNow.AddDays(-5));

        var origen = Conversacion.CrearWhatsApp("+34600111222", Guid.NewGuid(), clienteId, Guid.NewGuid());
        var fechaReciente = DateTime.UtcNow;
        origen.AgregarMensaje(DireccionMensaje.Entrante, CanalConversacion.WhatsApp, "+34600111222", "Mensaje nuevo", fechaReciente);

        destino.AbsorberMensajesDe(origen);

        destino.FechaUltimoMensajeUtc.Should().Be(fechaReciente);
        destino.FechaUltimoMensajeEntranteUtc.Should().Be(fechaReciente);
    }

    [Fact]
    public void AbsorberMensajesDe_rechaza_fusionar_una_conversacion_consigo_misma()
    {
        var conversacion = new Conversacion("Hilo único");

        var accion = () => conversacion.AbsorberMensajesDe(conversacion);

        accion.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void ReasignarConversacion_rechaza_un_id_vacio()
    {
        var conversacion = new Conversacion("Hilo");
        var mensaje = conversacion.AgregarMensaje(DireccionMensaje.Entrante, CanalConversacion.Correo, "cliente@empresa.test", "Cuerpo");

        var accion = () => mensaje.ReasignarConversacion(Guid.Empty);

        accion.Should().Throw<ArgumentException>();
    }
}
