using Bunit;
using CaeManager.Application.VistaDemo;
using CaeManager.Web.Components.Layout;
using FluentAssertions;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// Lo que el selector de vista de demo pinta y, sobre todo, lo que NO pinta: sin la función activada
/// o en una cuenta que no puede usarla no existe nada en la cabecera, y en cualquier vista sigue la
/// opción «Dirección» —la salida— con el formulario POST que la aplica. Que la petición valga lo
/// decide <c>VistaDemoActual</c> (VistaDemoLenteTests); aquí solo el reflejo en el marcado.
/// </summary>
public class SelectorVistaDemoTests : BunitContext
{
    private static readonly Guid Marta = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Pablo = Guid.Parse("22222222-2222-2222-2222-222222222222");

    public SelectorVistaDemoTests() =>
        Services.AddSingleton<AntiforgeryStateProvider>(new AntiforgeryFalso());

    private void Registrar(bool disponible, VistaDemoEfectiva? efectiva) =>
        Services.AddSingleton<IVistaDemoActual>(new VistaDemoFalsa(
            disponible, efectiva, [new GestorDeVistaDemo(Marta, "Marta"), new GestorDeVistaDemo(Pablo, "Pablo")]));

    [Fact]
    public void Sin_la_lente_registrada_no_pinta_nada()
    {
        Render<SelectorVistaDemo>().Markup.Trim().Should().BeEmpty();
    }

    [Fact]
    public void Si_la_cuenta_no_puede_usarla_no_pinta_nada_ni_ofrece_gestores()
    {
        Registrar(disponible: false, efectiva: null);

        var cut = Render<SelectorVistaDemo>();

        cut.Markup.Trim().Should().BeEmpty("en una cuenta o Tenant real el selector no existe");
        cut.Markup.Should().NotContain("Marta");
    }

    [Fact]
    public void Disponible_sin_lente_muestra_Direccion_y_ofrece_las_tres_vistas_plegado_por_defecto()
    {
        Registrar(disponible: true, efectiva: null);

        var cut = Render<SelectorVistaDemo>();

        cut.Find("details.selector-vista-demo").HasAttribute("open").Should().BeFalse("cerrado es una etiqueta compacta");
        cut.Find("summary").TextContent.Trim().Should().Be("Vista: Dirección");
        cut.FindAll("option").Select(o => o.GetAttribute("value")).Should().Equal(
            "direccion", "coordinador", $"gestor:{Marta:N}", $"gestor:{Pablo:N}");
        cut.Find("form").GetAttribute("action").Should().Be("/cuenta/vista-demo");
        cut.Find("form").GetAttribute("method").Should().Be("post");
    }

    [Theory]
    [InlineData(VistaDemo.Direccion, null, "Vista: Dirección", "direccion")]
    [InlineData(VistaDemo.CoordinadorCae, null, "Vista: Coordinador CAE", "coordinador")]
    [InlineData(VistaDemo.GestorCae, "marta", "Vista: Gestor CAE: Marta", "gestor:11111111111111111111111111111111")]
    public void En_cada_vista_el_selector_sigue_ahi_refleja_la_vista_efectiva_y_conserva_la_salida(
        VistaDemo vista, string? gestor, string etiqueta, string opcionSeleccionada)
    {
        Registrar(disponible: true, new VistaDemoEfectiva(vista, gestor is null ? null : Marta));

        var cut = Render<SelectorVistaDemo>();

        cut.Find("summary").TextContent.Trim().Should().Be(etiqueta);
        cut.FindAll("option").Where(o => o.HasAttribute("selected")).Select(o => o.GetAttribute("value"))
            .Should().Equal(opcionSeleccionada);
        cut.FindAll("option").Select(o => o.GetAttribute("value")).Should().Contain("direccion", "«Dirección» es siempre la salida");
    }

    [Fact]
    public void Una_vista_efectiva_de_un_gestor_que_ya_no_es_elegible_no_inventa_su_nombre()
    {
        Registrar(disponible: true, new VistaDemoEfectiva(VistaDemo.GestorCae, Guid.NewGuid()));

        Render<SelectorVistaDemo>().Find("summary").TextContent.Trim().Should().Be("Vista: Gestor CAE");
    }

    private sealed class VistaDemoFalsa(bool disponible, VistaDemoEfectiva? efectiva, IReadOnlyList<GestorDeVistaDemo> gestores) : IVistaDemoActual
    {
        public Task<bool> EstaDisponibleAsync(CancellationToken cancellationToken = default) => Task.FromResult(disponible);

        public Task<VistaDemoEfectiva?> ObtenerEfectivaAsync(CancellationToken cancellationToken = default) => Task.FromResult(efectiva);

        public Task<IReadOnlyList<GestorDeVistaDemo>> ObtenerGestoresElegiblesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(gestores);

        public Task<IReadOnlyList<Guid>?> ObtenerTenantIdsAcotadosAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Guid>?>(null);
    }

    private sealed class AntiforgeryFalso : AntiforgeryStateProvider
    {
        public override AntiforgeryRequestToken? GetAntiforgeryToken() => null;
    }
}
