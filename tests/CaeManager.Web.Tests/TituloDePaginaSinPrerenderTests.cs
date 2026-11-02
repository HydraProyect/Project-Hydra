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
        modulo.SetupVoid("fijarTitulo", _ => true).SetVoidResult();
        modulo.SetupVoid("liberar", _ => true).SetVoidResult();

        Render<TituloDePaginaSinPrerender>(p => p.Add(c => c.Titulo, "Mi trabajo — TALVEG"));

        modulo.VerifyInvoke("fijarTitulo").Arguments[0].Should().Be("Mi trabajo — TALVEG");
    }

    [Fact]
    public async Task Al_desecharse_libera_el_modulo_y_no_deja_el_titulo_vigilado()
    {
        var modulo = JSInterop.SetupModule(Modulo);
        modulo.SetupVoid("fijarTitulo", _ => true).SetVoidResult();
        modulo.SetupVoid("liberar", _ => true).SetVoidResult();

        var cmp = Render<TituloDePaginaSinPrerender>(p => p.Add(c => c.Titulo, "A"));
        await cmp.Instance.DisposeAsync();

        modulo.Invocations["liberar"].Should().HaveCount(1);
        modulo.Invocations["liberar"][0].Arguments[0].Should().Be(modulo.Invocations["fijarTitulo"][0].Arguments[1],
            "cada instancia solo libera lo que fijó ella: el módulo JS es único por documento");
    }

    [Fact]
    public void Un_fallo_del_modulo_de_titulo_no_tumba_la_pagina()
    {
        var modulo = JSInterop.SetupModule(Modulo);
        modulo.SetupVoid("fijarTitulo", _ => true).SetException(new Microsoft.JSInterop.JSException("no cargó"));

        var act = () => Render<TituloDePaginaSinPrerender>(p => p.Add(c => c.Titulo, "A"));

        act.Should().NotThrow("el título es secundario: un fallo suyo no puede ceder la cartera al aviso de error");
    }

    [Fact]
    public void Mi_trabajo_monta_el_componente_con_su_titulo()
    {
        // MiTrabajo no se prerenderiza: sin este componente la pestaña vuelve a quedar sin título. Los tests de
        // la página renderizan con JS suelto y no lo notarían.
        var directorio = new DirectoryInfo(AppContext.BaseDirectory);
        while (directorio is not null && !File.Exists(Path.Combine(directorio.FullName, "CaeManager.slnx")))
            directorio = directorio.Parent;
        directorio.Should().NotBeNull();

        var razor = File.ReadAllText(Path.Combine(
            directorio!.FullName, "src", "CaeManager.Web", "Features", "Bandeja", "Pages", "MiTrabajo.razor"));

        razor.Should().Contain("<TituloDePaginaSinPrerender Titulo=\"@(");
        razor.Should().Contain("prerender: false");
    }

    [Fact]
    public void Vuelve_a_fijarlo_cuando_cambia_el_titulo_y_no_antes()
    {
        var modulo = JSInterop.SetupModule(Modulo);
        modulo.SetupVoid("fijarTitulo", _ => true).SetVoidResult();
        modulo.SetupVoid("liberar", _ => true).SetVoidResult();

        var cmp = Render<TituloDePaginaSinPrerender>(p => p.Add(c => c.Titulo, "A"));
        cmp.Render();
        modulo.Invocations["fijarTitulo"].Should().HaveCount(1, "el mismo título no se vuelve a fijar");

        cmp.Render(p => p.Add(c => c.Titulo, "B"));

        modulo.Invocations["fijarTitulo"].Should().HaveCount(2);
        modulo.Invocations["fijarTitulo"][1].Arguments[0].Should().Be("B");
    }
}
