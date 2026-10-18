using CaeManager.Application.Comunicaciones;
using CaeManager.Application.Comunicaciones.Commands.CambiarPrioridadConversacion;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Comunicaciones;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace CaeManager.Application.Tests.Comunicaciones;

public class SlaConversacionTests
{
    [Theory]
    [InlineData(PrioridadConversacion.Critica, "2026-10-22T09:30:00+02:00", "2026-10-22T11:00:00+02:00")]
    [InlineData(PrioridadConversacion.Urgente, "2026-10-22T14:00:00+02:00", "2026-10-23T10:00:00+02:00")]
    [InlineData(PrioridadConversacion.Normal, "2026-10-22T11:00:00+02:00", "2026-10-23T11:00:00+02:00")]
    [InlineData(PrioridadConversacion.Normal, "2026-10-22T16:30:00+02:00", "2026-10-23T15:00:00+02:00")]
    public void Los_plazos_respetan_pausas_jornadas_y_el_cierre_intensivo(PrioridadConversacion prioridad, string inicio, string fin) =>
        PlazosConversacionService.Vencimiento(prioridad, DateTimeOffset.Parse(inicio), new CalendarioLaboralOperadorCae([]))
            .Should().Be(DateTimeOffset.Parse(fin));

    [Fact]
    public void El_SLA_empieza_en_el_primer_entrante_humano_despues_de_la_ultima_respuesta()
    {
        var conversacion = new Conversacion("Asunto");
        var inicio = DateTime.UtcNow.AddDays(-1);
        conversacion.AgregarMensaje(DireccionMensaje.Entrante, CanalConversacion.Correo, "contacto@ejemplo.test", "Primero", inicio);
        conversacion.AgregarMensaje(DireccionMensaje.Saliente, CanalConversacion.Correo, "cae@ejemplo.test", "Respuesta", inicio.AddMinutes(1));
        conversacion.AgregarMensaje(DireccionMensaje.Entrante, CanalConversacion.Correo, "no-reply@ejemplo.test", "Automático", inicio.AddMinutes(2), esAutomatico: true);
        conversacion.AgregarMensaje(DireccionMensaje.Entrante, CanalConversacion.Correo, "contacto@ejemplo.test", "Pendiente", inicio.AddMinutes(3));
        conversacion.AgregarMensaje(DireccionMensaje.Entrante, CanalConversacion.Correo, "contacto@ejemplo.test", "Otro", inicio.AddMinutes(4));
        PlazosConversacionService.PrimerEntranteSinRespuesta(conversacion.Mensajes).Should().Be(inicio.AddMinutes(3));
    }

    [Fact]
    public async Task La_prioridad_Critica_avisa_sin_lectura_de_la_conversacion_y_no_duplica_una_repeticion()
    {
        var conversacion = new Conversacion("Asunto");
        conversacion.Prioridad.Should().Be(PrioridadConversacion.Normal);
        var repositorio = new ConversacionRepositorioFalso();
        repositorio.Agregar(conversacion);
        var contexto = new ContextoFalso();
        var handler = new CambiarPrioridadConversacionCommandHandler(repositorio, new AlcanceDatosServiceFalso(), contexto,
            new UnitOfWorkFalso(), NullLogger<CambiarPrioridadConversacionCommandHandler>.Instance);
        (await handler.Handle(new(conversacion.Id, PrioridadConversacion.Critica), CancellationToken.None)).EsExitoso.Should().BeTrue();
        (await handler.Handle(new(conversacion.Id, PrioridadConversacion.Critica), CancellationToken.None)).EsExitoso.Should().BeTrue();
        contexto.Avisos.Should().Be(1);
        conversacion.AvisoCriticaPendienteId.Should().BeNull();
    }

    [Fact]
    public async Task Una_conversacion_fuera_de_cartera_no_cambia_ni_genera_avisos()
    {
        var conversacion = new Conversacion("Restringida", Guid.NewGuid());
        var repositorio = new ConversacionRepositorioFalso();
        repositorio.Agregar(conversacion);
        var contexto = new ContextoFalso();
        var guardado = new UnitOfWorkFalso();
        var handler = new CambiarPrioridadConversacionCommandHandler(repositorio, new AlcanceDatosServiceFalso(tieneAccesoTotal: false),
            contexto, guardado, NullLogger<CambiarPrioridadConversacionCommandHandler>.Instance);
        (await handler.Handle(new(conversacion.Id, PrioridadConversacion.Critica), CancellationToken.None)).EsFallido.Should().BeTrue();
        conversacion.Prioridad.Should().Be(PrioridadConversacion.Normal);
        guardado.VecesGuardado.Should().Be(0);
        contexto.Avisos.Should().Be(0);
    }

    [Fact]
    public async Task Un_fallo_de_aviso_conserva_el_evento_para_reintentar_sin_fingir_que_no_se_guardo_la_prioridad()
    {
        var conversacion = new Conversacion("Asunto");
        var repositorio = new ConversacionRepositorioFalso();
        repositorio.Agregar(conversacion);
        var contexto = new ContextoFalso { Fallar = true };
        var handler = new CambiarPrioridadConversacionCommandHandler(repositorio, new AlcanceDatosServiceFalso(), contexto,
            new UnitOfWorkFalso(), NullLogger<CambiarPrioridadConversacionCommandHandler>.Instance);
        (await handler.Handle(new(conversacion.Id, PrioridadConversacion.Critica), CancellationToken.None)).EsExitoso.Should().BeTrue();
        conversacion.Prioridad.Should().Be(PrioridadConversacion.Critica);
        conversacion.AvisoCriticaPendienteId.Should().NotBeNull();
    }

    private sealed class ContextoFalso : IContextoOperativoConversacionService
    {
        public int Avisos { get; private set; }
        public bool Fallar { get; init; }
        public Task<OperacionDeConversacion?> ResolverAsync(Guid propietarioTenantId, Guid? clienteEmpresarialId, Guid? empresaId, CancellationToken cancellationToken) =>
            Task.FromResult<OperacionDeConversacion?>(new(Guid.NewGuid(), Guid.NewGuid()));
        public Task AvisarCriticaAsync(OperacionDeConversacion operacion, Guid eventoId, CancellationToken cancellationToken)
        {
            if (Fallar) throw new InvalidOperationException("Fallo simulado");
            Avisos++;
            return Task.CompletedTask;
        }
    }
}
