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
    private static MensajeDetalleDto CrearMensaje(
        DateTime fechaUtc, string cuerpo = "Hola", IReadOnlyList<AdjuntoDetalleDto>? adjuntos = null) => new(
        Guid.NewGuid(), DireccionMensaje.Entrante, CanalConversacion.Correo, "cliente@ejemplo.com", cuerpo, fechaUtc,
        adjuntos ?? [], null, []);

    private static EventoDetalleDto CrearEvento(
        DateTime fechaUtc, string descripcion = "Se ha creado una visita.", TipoEventoConversacion tipo = TipoEventoConversacion.VisitaCreada) =>
        new(Guid.NewGuid(), tipo, Guid.NewGuid(), fechaUtc, descripcion);

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

    [Fact]
    public void Un_evento_de_documento_actualizado_incluye_el_enlace_ver_documento()
    {
        var cut = Render<UnifiedTimeline>(parametros => parametros
            .Add(p => p.Mensajes, [])
            .Add(p => p.Participantes, [])
            .Add(p => p.Eventos, [CrearEvento(
                DateTime.UtcNow, "Se ha actualizado el documento Certificado de Elena Soto.", TipoEventoConversacion.DocumentoActualizado)]));

        cut.Markup.Should().Contain("Se ha actualizado el documento Certificado de Elena Soto.");
        cut.Find(".timeline-evento-enlace").TextContent.Should().Be("Ver documento");
    }

    [Fact]
    public void Un_adjunto_muestra_el_boton_actualizar_documentacion_y_dispara_el_callback_con_su_id()
    {
        var adjuntoId = Guid.NewGuid();
        Guid? adjuntoRecibido = null;

        var cut = Render<UnifiedTimeline>(parametros => parametros
            .Add(p => p.Mensajes, [CrearMensaje(
                DateTime.UtcNow, adjuntos: [new AdjuntoDetalleDto(adjuntoId, "TC2_Julio.pdf", "application/pdf", 1024)])])
            .Add(p => p.Participantes, [])
            .Add(p => p.OnActualizarDocumentoDesdeAdjunto, id => adjuntoRecibido = id));

        cut.Find(".timeline-adjunto-actualizar-documento").Click();

        adjuntoRecibido.Should().Be(adjuntoId);
    }

    [Fact]
    public void Un_mensaje_con_sugerencia_de_visita_pendiente_muestra_el_marcador_pasivo()
    {
        var sugerencia = new SugerenciaVisitaDetalleDto(Guid.NewGuid(), null, null, null, null, "Pide una visita", 92, 92, 92);
        var mensaje = new MensajeDetalleDto(
            Guid.NewGuid(), DireccionMensaje.Entrante, CanalConversacion.Correo, "cliente@ejemplo.com", "Hola", DateTime.UtcNow,
            [], sugerencia, []);

        var cut = Render<UnifiedTimeline>(parametros => parametros
            .Add(p => p.Mensajes, [mensaje])
            .Add(p => p.Participantes, []));

        cut.Markup.Should().Contain("Posible solicitud de visita — revisar en Acciones sugeridas");
        cut.FindAll(".accion-card").Should().BeEmpty();
    }

    /// <summary>Una notificación en bloque puede detectar varios ítems de gestión a la vez (ronda de reducción de ruido en Comunicaciones) — cada uno lleva su propio marcador pasivo.</summary>
    [Fact]
    public void Un_mensaje_con_varios_items_de_gestion_muestra_un_marcador_por_cada_uno()
    {
        var items = new[]
        {
            new SugerenciaGestionDetalleDto(Guid.NewGuid(), Guid.NewGuid(), "Ana García", Guid.NewGuid(), "EPI", "Dos pendientes", 88, 90, 90),
            new SugerenciaGestionDetalleDto(Guid.NewGuid(), Guid.NewGuid(), "Luis Pérez", Guid.NewGuid(), "Apto médico", "Dos pendientes", 88, 85, 85),
        };
        var mensaje = new MensajeDetalleDto(
            Guid.NewGuid(), DireccionMensaje.Entrante, CanalConversacion.Correo, "plataforma@ejemplo.com", "Hola", DateTime.UtcNow,
            [], null, items);

        var cut = Render<UnifiedTimeline>(parametros => parametros
            .Add(p => p.Mensajes, [mensaje])
            .Add(p => p.Participantes, []));

        cut.FindAll(".timeline-marca-ia").Should().HaveCount(2);
    }
}
