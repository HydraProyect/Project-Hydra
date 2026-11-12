using AngleSharp.Dom;
using Bunit;
using CaeManager.Application.Trabajadores.Commands.CrearTrabajador;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.Trabajadores.Pages;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace CaeManager.Web.Tests;

/// <summary>
/// La fecha de nacimiento del alta por pasos llega como «» cuando está a medias o ilegible: guardar así daría de alta al
/// Trabajador «sin fecha», y salir del paso destruiría el campo con el texto que se está corrigiendo. El alta consume la
/// bandera de <see cref="CampoFecha"/> en «Siguiente», en el indicador de pasos y en «Guardar».
/// </summary>
public partial class TrabajadoresListaGen2Tests
{
    private static IElement FechaDeNacimiento(IRenderedComponent<Trabajadores> cut) =>
        PanelDeAlta(cut).FindComponents<CampoTexto>().Single(c => c.Instance.Type == "date").Find("input[data-campo-fecha]");

    private async Task<(IRenderedComponent<Trabajadores> Cut, MediatorFalso Mediador)> AbrirAltaConDatosAsync()
    {
        var mediador = new MediatorFalso();
        var cut = Renderizar(mediador);
        await AbrirAltaAsync(cut);
        await cut.Find(".drawer-panel select").ChangeAsync(new ChangeEventArgs { Value = EmpresaEbro.ToString() });
        await EscribirDatosCompletosDelAltaAsync(cut);
        return (cut, mediador);
    }

    [Fact]
    public async Task Con_la_fecha_de_nacimiento_a_medias_Siguiente_no_sale_del_paso_y_con_la_fecha_entera_si()
    {
        var (cut, _) = await AbrirAltaConDatosAsync();

        await FechaDeNacimiento(cut).InputAsync(new ChangeEventArgs { Value = "15/03" });
        await PulsarEnElPanelAsync(cut, "Siguiente");

        BotonesDelPanel(cut).Should().Equal("Cancelar", "Guardar y terminar", "Siguiente");
        cut.Find(".drawer-panel").TextContent.Should().Contain("no es válida");

        await FechaDeNacimiento(cut).InputAsync(new ChangeEventArgs { Value = "15/03/1990" });
        cut.Find(".drawer-panel").TextContent.Should().NotContain("no es válida", "el aviso de un intento anterior ya no es cierto");
        await PulsarEnElPanelAsync(cut, "Siguiente");

        BotonesDelPanel(cut).Should().Equal("Atrás", "Guardar y terminar", "Siguiente");
    }

    [Fact]
    public async Task Con_la_fecha_de_nacimiento_a_medias_el_indicador_de_pasos_no_sale_del_paso_1()
    {
        var (cut, _) = await AbrirAltaConDatosAsync();
        await PulsarEnElPanelAsync(cut, "Siguiente");
        await PulsarEnElPanelAsync(cut, "Siguiente");
        await PulsarEnElPanelAsync(cut, "Atrás");
        await PulsarEnElPanelAsync(cut, "Atrás");
        BotonesDelPanel(cut).Should().Equal("Cancelar", "Guardar y terminar", "Siguiente");

        await FechaDeNacimiento(cut).InputAsync(new ChangeEventArgs { Value = "15/03" });
        await cut.FindAll(".drawer-pasos .indicador-pasos-boton")
            .Single(b => b.TextContent.Trim() == "Trabajo").ClickAsync(new MouseEventArgs());

        BotonesDelPanel(cut).Should().Equal("Cancelar", "Guardar y terminar", "Siguiente");
        cut.Find(".drawer-panel").TextContent.Should().Contain("no es válida");
    }

    [Fact]
    public async Task Con_la_fecha_de_nacimiento_a_medias_Guardar_no_da_de_alta_al_trabajador_sin_fecha()
    {
        var (cut, mediador) = await AbrirAltaConDatosAsync();

        await FechaDeNacimiento(cut).InputAsync(new ChangeEventArgs { Value = "15/03" });
        await PulsarEnElPanelAsync(cut, "Guardar y terminar");

        mediador.Enviadas.OfType<CrearTrabajadorCommand>().Should().BeEmpty("el texto a medias llega como «» y se daría de alta «sin fecha»");
        cut.Find(".drawer-panel").TextContent.Should().Contain("no es válida");

        await FechaDeNacimiento(cut).InputAsync(new ChangeEventArgs { Value = "15/03/1990" });
        await PulsarEnElPanelAsync(cut, "Guardar y terminar");

        mediador.Enviadas.OfType<CrearTrabajadorCommand>().Should().ContainSingle("control positivo: con la fecha entera sí se da de alta")
            .Which.FechaNacimiento.Should().Be(new DateOnly(1990, 3, 15));
    }

    [Fact]
    public async Task Cancelar_el_alta_con_la_fecha_de_nacimiento_a_medias_pregunta()
    {
        var cut = Renderizar(new MediatorFalso());
        await AbrirAltaAsync(cut);

        await FechaDeNacimiento(cut).InputAsync(new ChangeEventArgs { Value = "15/03" });

        await PulsarEnElPanelAsync(cut, "Cancelar");
        await cut.ComprobarQuePreguntaYDescartarAsync(".drawer-panel");
    }
}
