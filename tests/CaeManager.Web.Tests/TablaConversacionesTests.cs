using Bunit;
using CaeManager.Application.Comunicaciones.Queries.ObtenerConversaciones;
using CaeManager.Domain.Comunicaciones;
using CaeManager.Web.Features.Comunicaciones.Components;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

public class TablaConversacionesTests : BunitContext
{
    public TablaConversacionesTests() => Services.AddLocalization();

    [Fact]
    public void La_tabla_muestra_empresa_estado_y_persona_asignada_y_abre_el_hilo()
    {
        var usuarioId = Guid.NewGuid();
        var conversacion = CrearConversacion() with { EjecutivoAsignadoId = usuarioId };
        Guid? abierta = null;
        var cut = Render<TablaConversaciones>(p => p
            .Add(c => c.Conversaciones, [conversacion])
            .Add(c => c.NombresAsignados, new Dictionary<Guid, string> { [usuarioId] = "Marta Olano" })
            .Add(c => c.OnAbrir, id => abierta = id));

        var celdas = cut.FindAll("[role=cell]");
        celdas[2].TextContent.Should().Be("Empresa de prueba");
        celdas[3].TextContent.Should().Be("Abierta");
        celdas[4].TextContent.Should().Be("Marta Olano");
        // Un botón nativo se activa con Intro/Espacio y emite click; no depende de un li con onclick.
        cut.Find("button").GetAttribute("type").Should().Be("button");
        cut.Find("button").Click();
        abierta.Should().Be(conversacion.Id);
    }

    [Fact]
    public void Sin_asignacion_no_inventa_una_persona_ni_un_plazo()
    {
        var cut = Render<TablaConversaciones>(p => p.Add(c => c.Conversaciones,
            [CrearConversacion() with { SlaRestante = TimeSpan.FromHours(2) }]));

        cut.FindAll("[role=cell]")[4].TextContent.Should().Be("Sin asignar");
        cut.FindAll("[role=columnheader]").Should().HaveCount(5);
        cut.Markup.Should().NotContain("SLA");
    }

    private static ConversacionListaDto CrearConversacion() => new(
        Guid.NewGuid(), Guid.NewGuid(), "Empresa de prueba", "Documentación pendiente", EstadoConversacion.Abierta,
        null, "contacto@example.invalid", "Adjunto documentos.", DateTime.UtcNow, 1,
        CanalConversacion.Correo, null, DireccionMensaje.Entrante);
}
