using Bunit;
using CaeManager.Application.Plantillas.Commands.IniciarLoteGeneracionDocumentos;
using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadoresParaSelector;
using CaeManager.Domain.Common;
using CaeManager.Domain.Plantillas;
using CaeManager.Web.Features.Plantillas.Pages;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Xunit;

namespace CaeManager.Web.Tests;

/// <summary>
/// Tope de Trabajadores por lote de generación (decisión de Chris, 2026-11-14): con más de
/// <see cref="IniciarLoteGeneracionDocumentosCommandValidator.MaximoTrabajadoresPorLote"/> elegidos, la pantalla avisa
/// y deshabilita «Generar lote»; el servidor rechaza el lote entero por su cuenta.
/// </summary>
public partial class ConfigurarPlantillaEditorGen2Tests
{
    private async Task<(IRenderedComponent<ConfigurarPlantilla> Cut, MediatorFalso Mediador)> LoteConTrabajadoresMarcadosAsync(int disponibles, int marcados)
    {
        IReadOnlyList<TrabajadorSelectorDto> trabajadores = Enumerable.Range(0, disponibles)
            .Select(i => new TrabajadorSelectorDto(Guid.NewGuid(), $"Trabajador {i:000}", null, null))
            .ToList();
        var mediador = new MediatorFalso
        {
            Version = Result.Exito(Detalle(_versionId, estado: EstadoConfiguracionPlantilla.Confirmada)),
            Retener = p => p is ObtenerTrabajadoresParaSelectorQuery ? Task.FromResult<object>(trabajadores) : null
        };
        var cut = Renderizar(mediador);
        await cut.FindAll("input[type=radio]")[1].ChangeAsync(new ChangeEventArgs { Value = true });
        for (var i = 0; i < marcados; i++)
            await cut.FindAll(".editor-plantilla-lista-trabajadores input")[i].ChangeAsync(new ChangeEventArgs { Value = true });
        return (cut, mediador);
    }

    [Fact]
    public async Task Con_200_trabajadores_elegidos_el_lote_se_puede_generar_sin_aviso()
    {
        var (cut, _) = await LoteConTrabajadoresMarcadosAsync(disponibles: 201, marcados: 200);

        Boton(cut, "Generar lote").HasAttribute("disabled").Should().BeFalse();
        cut.FindAll(".editor-plantilla-aviso-campo").Should().BeEmpty();
    }

    [Fact]
    public async Task Con_201_trabajadores_elegidos_avisa_con_el_tope_y_deshabilita_el_envio()
    {
        var (cut, mediador) = await LoteConTrabajadoresMarcadosAsync(disponibles: 201, marcados: 201);

        cut.Find(".editor-plantilla-aviso-campo[role=alert]").TextContent.Should().Contain("200").And.Contain("201");
        Boton(cut, "Generar lote").HasAttribute("disabled").Should().BeTrue();

        await Boton(cut, "Generar lote").ClickAsync(new MouseEventArgs());
        mediador.Veces<IniciarLoteGeneracionDocumentosCommand>().Should().Be(0, "por encima del tope no se manda nada al servidor");
    }

    [Fact]
    public async Task Al_quitar_uno_de_los_201_el_aviso_desaparece_y_el_envio_se_habilita()
    {
        var (cut, _) = await LoteConTrabajadoresMarcadosAsync(disponibles: 201, marcados: 201);

        await cut.FindAll(".editor-plantilla-lista-trabajadores input")[0].ChangeAsync(new ChangeEventArgs { Value = false });

        cut.FindAll(".editor-plantilla-aviso-campo").Should().BeEmpty();
        Boton(cut, "Generar lote").HasAttribute("disabled").Should().BeFalse();
    }
}
