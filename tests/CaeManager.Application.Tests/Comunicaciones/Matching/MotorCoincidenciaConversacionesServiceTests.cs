using CaeManager.Application.Comunicaciones.Matching;
using CaeManager.Application.Tests.Comunicaciones;
using CaeManager.Domain.Comunicaciones;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Comunicaciones.Matching;

/// <summary>Conversation Matching Engine (docs/COMUNICACIONES.md § 13.2) — solo Mismo Cliente y Ventana temporal calculan con datos reales hoy; ver comentario de MotorCoincidenciaConversacionesService.</summary>
public class MotorCoincidenciaConversacionesServiceTests
{
    [Fact]
    public async Task Propone_la_candidata_mas_reciente_dentro_de_la_ventana_de_72h()
    {
        var clienteId = Guid.NewGuid();
        var repositorio = new ConversacionRepositorioFalso();

        // Ancla en el futuro: Conversacion.AgregarMensaje solo AVANZA
        // FechaUltimoMensajeUtc (nunca la retrasa respecto al momento de
        // construcción) — anclar muy por delante del "ahora" real del test
        // permite backdatear unas candidatas respecto a otras sin chocar con
        // ese límite del dominio.
        var referencia = DateTime.UtcNow.AddYears(1);

        var origen = Conversacion.CrearWhatsApp("+34600111222", Guid.NewGuid(), clienteId, Guid.NewGuid());
        origen.AgregarMensaje(DireccionMensaje.Entrante, CanalConversacion.WhatsApp, "+34600111222", "Hola", referencia);
        repositorio.Agregar(origen);

        var candidataReciente = new Conversacion("Consulta reciente", clienteId);
        candidataReciente.AgregarMensaje(DireccionMensaje.Entrante, CanalConversacion.Correo, "cliente@empresa.test", "Correo de hace 1 hora", referencia.AddHours(-1));
        repositorio.Agregar(candidataReciente);

        var candidataAntigua = new Conversacion("Consulta antigua", clienteId);
        candidataAntigua.AgregarMensaje(DireccionMensaje.Entrante, CanalConversacion.Correo, "cliente@empresa.test", "Correo de hace 10 días", referencia.AddDays(-10));
        repositorio.Agregar(candidataAntigua);

        var motor = new MotorCoincidenciaConversacionesService(repositorio);

        var propuesta = await motor.ProponerVinculacionAsync(origen.Id, clienteId, referencia, CancellationToken.None);

        propuesta.Should().NotBeNull();
        propuesta!.ConversacionId.Should().Be(candidataReciente.Id);
        propuesta.Desglose.MismoCliente.Should().Be(40);
        propuesta.Desglose.VentanaTemporal.Should().Be(10);
        propuesta.Desglose.Total.Should().Be(50);
        propuesta.Desglose.SimilaridadSemantica.Should().Be(0);
        propuesta.Desglose.MismoTrabajador.Should().Be(0);
        propuesta.Desglose.MismoCentro.Should().Be(0);
        propuesta.Desglose.MismoDocumento.Should().Be(0);
        propuesta.Desglose.MismoProyecto.Should().Be(0);
    }

    [Fact]
    public async Task No_propone_nada_si_ninguna_candidata_tuvo_actividad_en_72h()
    {
        var clienteId = Guid.NewGuid();
        var repositorio = new ConversacionRepositorioFalso();
        var referencia = DateTime.UtcNow.AddYears(1);

        var origen = Conversacion.CrearWhatsApp("+34600111222", Guid.NewGuid(), clienteId, Guid.NewGuid());
        repositorio.Agregar(origen);

        var candidataAntigua = new Conversacion("Consulta antigua", clienteId);
        candidataAntigua.AgregarMensaje(DireccionMensaje.Entrante, CanalConversacion.Correo, "cliente@empresa.test", "Correo de hace 10 días", referencia.AddDays(-10));
        repositorio.Agregar(candidataAntigua);

        var motor = new MotorCoincidenciaConversacionesService(repositorio);

        var propuesta = await motor.ProponerVinculacionAsync(origen.Id, clienteId, referencia, CancellationToken.None);

        propuesta.Should().BeNull();
    }

    [Fact]
    public async Task No_propone_nada_si_el_cliente_no_tiene_otras_conversaciones_abiertas()
    {
        var clienteId = Guid.NewGuid();
        var repositorio = new ConversacionRepositorioFalso();
        var origen = Conversacion.CrearWhatsApp("+34600111222", Guid.NewGuid(), clienteId, Guid.NewGuid());
        repositorio.Agregar(origen);

        var motor = new MotorCoincidenciaConversacionesService(repositorio);

        var propuesta = await motor.ProponerVinculacionAsync(origen.Id, clienteId, DateTime.UtcNow, CancellationToken.None);

        propuesta.Should().BeNull();
    }

    [Fact]
    public async Task Ignora_candidatas_cerradas_o_resueltas()
    {
        var clienteId = Guid.NewGuid();
        var repositorio = new ConversacionRepositorioFalso();
        var origen = Conversacion.CrearWhatsApp("+34600111222", Guid.NewGuid(), clienteId, Guid.NewGuid());
        repositorio.Agregar(origen);

        var candidataCerrada = new Conversacion("Consulta cerrada", clienteId);
        candidataCerrada.AgregarMensaje(DireccionMensaje.Entrante, CanalConversacion.Correo, "cliente@empresa.test", "Correo reciente", DateTime.UtcNow.AddHours(-1));
        candidataCerrada.CambiarEstado(EstadoConversacion.Cerrada);
        repositorio.Agregar(candidataCerrada);

        var motor = new MotorCoincidenciaConversacionesService(repositorio);

        var propuesta = await motor.ProponerVinculacionAsync(origen.Id, clienteId, DateTime.UtcNow, CancellationToken.None);

        propuesta.Should().BeNull();
    }
}
