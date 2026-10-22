using Bunit;
using CaeManager.Application.Common;
using CaeManager.Infrastructure.AsistenteIa;
using CaeManager.Web.Features.AsistenteIa;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CaeManager.Web.Tests;

/// <summary>
/// El botón flotante de Vega (marco-talveg.js, 2026-10-21). Sin ningún proveedor configurado
/// no se muestra en absoluto — mismo principio que Sentry/Backups (funciona sin configurar
/// quedando inerte, en vez de mostrar algo roto).
/// </summary>
public class BotonAsistenteIaTests : BunitContext
{
    private const string ModuloVega = "./js/vega.js";

    private readonly BunitJSModuleInterop _modulo;

    public BotonAsistenteIaTests()
    {
        Services.AddLocalization();
        Services.AddScoped<AsistenteIaService>();
        Services.AddSingleton<IOptions<TypeSafeOptions>>(Options.Create(new TypeSafeOptions()));
        Services.AddScoped<DisponibilidadAsistente>();
        AddAuthorization().SetAuthorized("laura.perez@consultora.es");
        _modulo = JSInterop.SetupModule(ModuloVega);
        _modulo.Mode = JSRuntimeMode.Loose;
    }

    private void ConClave(bool clave) =>
        Services.AddSingleton<IOptions<AnthropicOptions>>(Options.Create(new AnthropicOptions { ApiKey = clave ? "sk-ant-clave-de-prueba" : null }));

    [Fact]
    public void No_se_renderiza_nada_si_no_hay_ApiKey_configurada()
    {
        ConClave(false);

        var cut = Render<BotonAsistenteIa>();

        cut.Markup.Should().BeEmpty();
    }

    [Fact]
    public void Es_una_pildora_con_el_icono_Vega_y_el_atajo()
    {
        ConClave(true);

        var cut = Render<BotonAsistenteIa>();

        var boton = cut.Find("button.boton-asistente-ia");
        boton.QuerySelector(".vega-avatar svg").Should().NotBeNull("el icono ia va dentro del círculo turquesa");
        boton.QuerySelector(".boton-asistente-ia-nombre")!.TextContent.Should().Be("Vega");
        boton.QuerySelector("kbd")!.TextContent.Should().Be("Ctrl I");
        boton.GetAttribute("aria-label").Should().Be($"Abrir Vega, el asistente de {Marca.Nombre} (Ctrl+I)");
        boton.GetAttribute("aria-keyshortcuts").Should().Be("Control+I");
        boton.GetAttribute("data-tooltip").Should().Be("Abrir Vega · Ctrl+I");
    }

    [Fact]
    public void Pulsar_el_boton_abre_Vega()
    {
        ConClave(true);
        var cut = Render<BotonAsistenteIa>();

        cut.Find("button.boton-asistente-ia").Click();

        Services.GetRequiredService<AsistenteIaService>().Abierto.Should().BeTrue();
    }

    [Fact]
    public void No_aparece_en_la_pantalla_completa_del_asistente()
    {
        ConClave(true);
        Services.GetRequiredService<NavigationManager>().NavigateTo("/asistente");

        var cut = Render<BotonAsistenteIa>();

        cut.FindAll("button.boton-asistente-ia").Should().BeEmpty("en /asistente Vega ya ocupa la pantalla");
        cut.FindAll(".vega-pista").Should().BeEmpty();
    }

    [Fact]
    public void Desaparece_al_entrar_en_la_pantalla_completa_y_vuelve_al_salir()
    {
        ConClave(true);
        var cut = Render<BotonAsistenteIa>();
        var navegacion = Services.GetRequiredService<NavigationManager>();

        cut.InvokeAsync(() => navegacion.NavigateTo("/asistente"));
        cut.WaitForAssertion(() => cut.FindAll("button.boton-asistente-ia").Should().BeEmpty());

        cut.InvokeAsync(() => navegacion.NavigateTo("/trabajadores"));
        cut.WaitForAssertion(() => cut.FindAll("button.boton-asistente-ia").Should().ContainSingle());
    }

    [Fact]
    public void La_pista_de_primera_vez_desaparece_al_pulsar_Entendido_y_se_recuerda()
    {
        ConClave(true);
        _modulo.Setup<bool>("pistaVista", _ => true).SetResult(false);

        var cut = Render<BotonAsistenteIa>();

        var pista = cut.WaitForElement(".vega-pista");
        pista.GetAttribute("role").Should().Be("note");
        pista.TextContent.Should().Contain($"Soy Vega, el asistente de {Marca.Nombre}.")
            .And.Contain("siempre te enseño el plan antes de hacer nada");

        cut.Find(".vega-pista-entendido").Click();

        cut.FindAll(".vega-pista").Should().BeEmpty();
        _modulo.VerifyInvoke("marcarPistaVista");
    }

    [Fact]
    public void La_pista_no_sale_si_el_usuario_ya_la_cerro()
    {
        ConClave(true);
        _modulo.Setup<bool>("pistaVista", _ => true).SetResult(true);

        var cut = Render<BotonAsistenteIa>();

        // Que la consulta se hizo y que, ya con su respuesta y un render más, la pista sigue sin
        // salir: si el componente ignorara lo que dice vega.js, la pista aparecería aquí.
        cut.WaitForAssertion(() => _modulo.VerifyInvoke("pistaVista"));
        cut.Render();
        cut.FindAll(".vega-pista").Should().BeEmpty();
        _modulo.VerifyNotInvoke("marcarPistaVista");
    }

    [Fact]
    public void Abrir_Vega_tambien_retira_la_pista_para_siempre()
    {
        ConClave(true);
        _modulo.Setup<bool>("pistaVista", _ => true).SetResult(false);
        var cut = Render<BotonAsistenteIa>();
        cut.WaitForElement(".vega-pista");

        cut.Find("button.boton-asistente-ia").Click();

        cut.WaitForAssertion(() => cut.FindAll(".vega-pista").Should().BeEmpty());
        _modulo.VerifyInvoke("marcarPistaVista");
    }
}
