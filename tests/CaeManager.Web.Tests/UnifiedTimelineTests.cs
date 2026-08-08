using Bunit;
using CaeManager.Application.Comunicaciones.Matching;
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
        adjuntos ?? [], null, null);

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
    public void Una_sugerencia_de_vinculacion_muestra_el_asunto_destino_y_dispara_el_callback_con_su_id()
    {
        var conversacionDestinoId = Guid.NewGuid();
        var desglose = new DesgloseCoincidenciaDto(40, 0, 0, 0, 0, 0, 10);
        Guid? destinoRecibido = null;

        var cut = Render<UnifiedTimeline>(parametros => parametros
            .Add(p => p.Mensajes, [])
            .Add(p => p.Participantes, [])
            .Add(p => p.SugerenciaVinculacion, new SugerenciaVinculacionDetalleDto(conversacionDestinoId, "Consulta sobre visita", desglose))
            .Add(p => p.OnVincularConversacion, id => destinoRecibido = id));

        cut.Markup.Should().Contain("Consulta sobre visita");
        cut.Find(".timeline-sugerencia-vinculacion").Should().NotBeNull();

        cut.FindAll(".timeline-sugerencia-vinculacion button")[1].Click();

        destinoRecibido.Should().Be(conversacionDestinoId);
    }

    [Fact]
    public void Descartar_una_sugerencia_de_vinculacion_la_oculta_sin_llamar_al_callback()
    {
        var desglose = new DesgloseCoincidenciaDto(40, 0, 0, 0, 0, 0, 10);
        var vinculado = false;

        var cut = Render<UnifiedTimeline>(parametros => parametros
            .Add(p => p.Mensajes, [])
            .Add(p => p.Participantes, [])
            .Add(p => p.SugerenciaVinculacion, new SugerenciaVinculacionDetalleDto(Guid.NewGuid(), "Consulta sobre visita", desglose))
            .Add(p => p.OnVincularConversacion, _ => vinculado = true));

        cut.FindAll(".timeline-sugerencia-vinculacion button")[0].Click();

        cut.FindAll(".timeline-sugerencia-vinculacion").Should().BeEmpty();
        vinculado.Should().BeFalse();
    }
}
