using Bunit;
using CaeManager.Application.Comunicaciones;
using CaeManager.Application.Comunicaciones.Queries.ObtenerMacros;
using CaeManager.Domain.Comunicaciones;
using CaeManager.Web.Features.Comunicaciones.Components;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

public class PlantillasRespuestaComposerTests : BunitContext
{
    public PlantillasRespuestaComposerTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLocalization();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public void Cada_plantilla_se_ofrece_y_su_seleccion_pide_aplicarla(int indice)
    {
        var plantilla = PlantillasRespuestaPredefinidas.Crear(Guid.NewGuid())[indice];
        string? seleccionada = null;
        var cut = Render<ComposerBar>(p => p.Add(c => c.Canal, CanalConversacion.Correo)
            .Add(c => c.Macros, new[] { new MacroListaDto(plantilla.Id, null, null, plantilla.Titulo, plantilla.CuerpoHtml, plantilla.Version) })
            .Add(c => c.OnAplicarMacro, (string id) => seleccionada = id));
        cut.Find($"option[value='{plantilla.Id}']").TextContent.Should().Be(plantilla.Titulo);
        cut.Find("select").Change(plantilla.Id.ToString());
        seleccionada.Should().Be(plantilla.Id.ToString());
    }

    [Fact]
    public void El_hilo_muestra_que_plantilla_se_aplico_sin_atribuirla_a_una_persona()
    {
        var descripcion = "Se aplicó la plantilla Solicitud mensual al borrador.";
        var evento = new CaeManager.Application.Comunicaciones.Queries.ObtenerConversacionPorId.EventoDetalleDto(
            Guid.NewGuid(), TipoEventoConversacion.PlantillaAplicada, Guid.NewGuid(), DateTime.UtcNow, descripcion);
        var cut = Render<UnifiedTimeline>(p => p.Add(c => c.Mensajes, [])
            .Add(c => c.Participantes, []).Add(c => c.Eventos, [evento]));
        cut.Find(".timeline-evento-fila").TextContent.Should().Contain(descripcion);
        cut.FindAll(".timeline-mensaje-fila").Should().BeEmpty();
    }

    [Fact]
    public void Las_variables_pendientes_se_resaltan_y_bloquean_boton_y_atajo()
    {
        var envios = 0;
        var cut = Render<ComposerBar>(p => p.Add(c => c.Canal, CanalConversacion.Correo)
            .Add(c => c.Texto, "Hola {{Empresa}}, falta {{documentos pendientes}}")
            .Add(c => c.OnEnviar, () => envios++));
        cut.FindAll("[role=alert] mark").Select(e => e.TextContent).Should().Equal("{{Empresa}}", "{{documentos pendientes}}");
        cut.Find(".composer-acciones button").HasAttribute("disabled").Should().BeTrue();
        cut.Find(".composer-correo").KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "Enter", CtrlKey = true });
        envios.Should().Be(0);

        cut.Render(p => p.Add(c => c.Texto, "Hola Empresa Norte, falta el A1"));
        cut.FindAll("mark").Should().BeEmpty();
        cut.Find(".composer-acciones button").HasAttribute("disabled").Should().BeFalse();
        cut.Find(".composer-correo").KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "Enter", CtrlKey = true });
        envios.Should().Be(1);
    }
}
