using Bunit;
using CaeManager.Web.Components.DesignSystem;
using FluentAssertions;

namespace CaeManager.Web.Tests;

/// <summary>
/// Una página sin prerender no llega al HeadOutlet estático con su PageTitle: el componente fija
/// <c>document.title</c> por JS al montarse y cuando cambia el título.
/// </summary>
public class TituloDePaginaSinPrerenderTests : BunitContext
{
    private const string Modulo = "./js/titulo-pagina.js";

    [Fact]
    public void Fija_el_titulo_de_la_pestana_al_montarse()
    {
        var modulo = JSInterop.SetupModule(Modulo);
        modulo.SetupVoid("fijarTitulo", "Mi trabajo — TALVEG").SetVoidResult();

        Render<TituloDePaginaSinPrerender>(p => p.Add(c => c.Titulo, "Mi trabajo — TALVEG"));

        modulo.VerifyInvoke("fijarTitulo").Arguments.Should().ContainSingle().Which.Should().Be("Mi trabajo — TALVEG");
    }

    [Fact]
    public void Vuelve_a_fijarlo_cuando_cambia_el_titulo_y_no_antes()
    {
        var modulo = JSInterop.SetupModule(Modulo);
        modulo.SetupVoid("fijarTitulo", _ => true).SetVoidResult();

        var cmp = Render<TituloDePaginaSinPrerender>(p => p.Add(c => c.Titulo, "A"));
        cmp.Render();
        modulo.Invocations["fijarTitulo"].Should().HaveCount(1, "el mismo título no se vuelve a fijar");

        cmp.Render(p => p.Add(c => c.Titulo, "B"));

        modulo.Invocations["fijarTitulo"].Should().HaveCount(2);
        modulo.Invocations["fijarTitulo"][1].Arguments.Should().ContainSingle().Which.Should().Be("B");
    }
}
