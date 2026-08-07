using Bunit;
using CaeManager.Application.Comunicaciones.Queries.ObtenerConversacionPorId;
using CaeManager.Domain.Comunicaciones;
using CaeManager.Web.Features.Comunicaciones.Components;
using FluentAssertions;

namespace CaeManager.Web.Tests;

/// <summary>
/// Cubre la mezcla cronológica de Mensajes + Eventos del sistema
/// (docs/COMUNICACIONES.md § 12.3/§ 16.7) — lo funcional (Mediator, Commands)
/// ya lo cubren los tests de integración; esto solo comprueba qué se pinta y
/// en qué orden.
/// </summary>
public class UnifiedTimelineTests : BunitContext
{
    private static MensajeDetalleDto CrearMensaje(DateTime fechaUtc, string cuerpo = "Hola") => new(
        Guid.NewGuid(), DireccionMensaje.Entrante, CanalConversacion.Correo, "cliente@ejemplo.com", cuerpo, fechaUtc,
        [], null, null);

    private static EventoDetalleDto CrearEvento(DateTime fechaUtc, string descripcion = "Se ha creado una visita.") =>
        new(Guid.NewGuid(), TipoEventoConversacion.VisitaCreada, Guid.NewGuid(), fechaUtc, descripcion);

    [Fact]
    public void Intercala_eventos_entre_mensajes_por_orden_cronologico()
    {
        var t0 = new DateTime(2026, 8, 8, 9, 0, 0, DateTimeKind.Utc);

        var cut = Render<UnifiedTimeline>(parametros => parametros
            .Add(p => p.Mensajes, [CrearMensaje(t0, "Primero"), CrearMensaje(t0.AddMinutes(20), "Tercero")])
            .Add(p => p.Participantes, [])
            .Add(p => p.Eventos, [CrearEvento(t0.AddMinutes(10), "Segundo: visita creada")]));

        var filas = cut.FindAll(".timeline-mensaje-fila, .timeline-evento-fila");

        filas.Should().HaveCount(3);
        filas[0].TextContent.Should().Contain("Primero");
        filas[1].TextContent.Should().Contain("Segundo: visita creada");
        filas[2].TextContent.Should().Contain("Tercero");
    }

    [Fact]
    public void Un_evento_de_visita_creada_incluye_el_enlace_ver_visita()
    {
        var cut = Render<UnifiedTimeline>(parametros => parametros
            .Add(p => p.Mensajes, [])
            .Add(p => p.Participantes, [])
            .Add(p => p.Eventos, [CrearEvento(DateTime.UtcNow, "Se ha creado una visita en Centro Norte.")]));

        cut.Markup.Should().Contain("Se ha creado una visita en Centro Norte.");
        cut.Find(".timeline-evento-enlace").TextContent.Should().Be("Ver visita");
    }

    [Fact]
    public void Sin_eventos_solo_se_pintan_mensajes()
    {
        var cut = Render<UnifiedTimeline>(parametros => parametros
            .Add(p => p.Mensajes, [CrearMensaje(DateTime.UtcNow)])
            .Add(p => p.Participantes, []));

        cut.FindAll(".timeline-evento-fila").Should().BeEmpty();
        cut.FindAll(".timeline-mensaje-fila").Should().ContainSingle();
    }
}
