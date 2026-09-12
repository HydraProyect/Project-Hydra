using Bunit;
using CaeManager.Application.Bandeja.Queries.ObtenerBandejaGestor;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Components.Workspace;
using CaeManager.Web.Features.Bandeja.Pages;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using static CaeManager.Web.Tests.BandejaDatosDePrueba;

namespace CaeManager.Web.Tests;

/// <summary>
/// «Mi trabajo» (<c>/bandeja</c>) tiene que distinguir <b>«no queda nada
/// pendiente»</b> de <b>«este chip no deja ver nada»</b>. Antes de este
/// incremento pintaba el mismo «Nada pendiente» en los dos casos, y el error
/// no es cosmético: felicitar a quien acaba de filtrar por «Revisión IA» le
/// dice que ha terminado el día cuando le quedan decenas de tareas de otro
/// tipo, justo en la pantalla cuyo único cometido es contestar «¿qué me
/// falta?».
///
/// <para>
/// El trinquete de <c>ListasDistinguenVacioPorFiltroTests</c> no lo veía:
/// Bandeja se maqueta con clases propias y no llevaba ninguna de sus marcas.
/// Se añadió la cuarta marca en ese mismo incremento — pero un trinquete de
/// fuente comprueba estructura, no que el texto sea cierto ni que el botón
/// funcione. Eso es lo que hacen estos casos, por render.
/// </para>
/// </summary>
public class BandejaVacioPorFiltroTests : BunitContext
{
    /// <summary>
    /// <c>AtajosListaTeclado</c> importa <c>./js/atajos-lista.js</c> al
    /// montarse: sin el modo laxo, el interop tumba todos los casos y el fallo
    /// no tiene nada que ver con el estado vacío que se quiere medir.
    /// </summary>
    public BandejaVacioPorFiltroTests() => JSInterop.Mode = JSRuntimeMode.Loose;

    private static readonly Guid ClienteRefri = Guid.NewGuid();

    private IRenderedComponent<Bandeja> Renderizar(string? tipo, params ItemBandejaDto[] items)
    {
        Services.AddScoped<IMediator>(_ => new MediatorDeLaBandeja(items));
        Services.AddScoped<ToastService>();
        Services.AddScoped<ContextWorkspaceService>();

        Services.GetRequiredService<NavigationManager>()
            .NavigateTo(tipo is null ? "bandeja" : "bandeja?tipo=" + Uri.EscapeDataString(tipo));

        return Render<Bandeja>();
    }

    [Fact]
    public void Un_chip_que_no_deja_ver_nada_no_se_lee_como_que_no_queda_trabajo()
    {
        var cut = Renderizar(
            tipo: nameof(TipoItemBandeja.RevisionIa),
            Item("v1", TipoItemBandeja.Vencido, ClienteRefri, "Refrielectric S.A."));

        cut.Markup.Should().Contain("Nada con este filtro");
        cut.Markup.Should().Contain("Quitar el filtro");
        cut.Markup.Should().NotContain("La cola está vacía",
            "decirle «no queda nada pendiente» a quien tiene un vencido delante, solo escondido por el chip, es mentira");
    }

    [Fact]
    public void Sin_filtro_y_sin_items_la_cola_esta_vacia_de_verdad()
    {
        var cut = Renderizar(tipo: null);

        cut.Markup.Should().Contain("La cola está vacía");
        cut.Markup.Should().NotContain("Nada con este filtro");
        cut.Markup.Should().NotContain("Quitar el filtro",
            "sin filtro puesto no hay ningún filtro que quitar");
    }

    [Fact]
    public void Quitar_el_filtro_devuelve_la_cola_completa_y_limpia_la_url()
    {
        var cut = Renderizar(
            tipo: nameof(TipoItemBandeja.RevisionIa),
            Item("v1", TipoItemBandeja.Vencido, ClienteRefri, "Refrielectric S.A."));
        cut.Markup.Should().Contain("Nada con este filtro", "es el punto de partida de este caso");

        cut.Find(".estado-vacio button").Click();

        cut.Markup.Should().NotContain("Nada con este filtro");
        cut.Markup.Should().NotContain("La cola está vacía");
        cut.Markup.Should().Contain("Refrielectric S.A.");
        Services.GetRequiredService<NavigationManager>().Uri.Should().NotContain("tipo=",
            "el filtro vive en la URL: quitarlo de la pantalla y dejarlo en la barra de direcciones "
            + "hace que recargar devuelva el vacío que se acaba de abandonar");
    }

    [Fact]
    public void Con_resultados_no_se_pinta_ningun_estado_vacio()
    {
        var cut = Renderizar(
            tipo: nameof(TipoItemBandeja.Vencido),
            Item("v1", TipoItemBandeja.Vencido, ClienteRefri, "Refrielectric S.A."));

        cut.Markup.Should().NotContain("Nada con este filtro");
        cut.Markup.Should().NotContain("La cola está vacía");
        cut.Markup.Should().Contain("Refrielectric S.A.");
    }

    /// <summary>
    /// El vacío por filtro no puede apagar los recuentos: son justamente el
    /// camino de vuelta. Si «Vencido (1)» se pusiera a cero al filtrar por
    /// «Revisión IA», la pantalla estaría diciendo que tampoco hay vencidos.
    /// </summary>
    [Fact]
    public void Con_el_chip_vacio_puesto_los_demas_chips_siguen_contando_sobre_el_total()
    {
        var cut = Renderizar(
            tipo: nameof(TipoItemBandeja.RevisionIa),
            Item("v1", TipoItemBandeja.Vencido, ClienteRefri, "Refrielectric S.A."),
            Item("v2", TipoItemBandeja.Vencido, ClienteRefri, "Refrielectric S.A."));

        var vencido = cut.FindAll("button.bandeja-chip").Single(b => b.TextContent.Contains("Vencido"));
        vencido.QuerySelector(".bandeja-chip-cuenta")!.TextContent.Trim().Should().Be("2");

        var todos = cut.FindAll("button.bandeja-chip").Single(b => b.TextContent.Contains("Todos"));
        todos.QuerySelector(".bandeja-chip-cuenta")!.TextContent.Trim().Should().Be("2");
    }
}
