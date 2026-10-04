using Bunit;
using CaeManager.Application.Common;
using CaeManager.Infrastructure.AsistenteIa;
using CaeManager.Web.Features.AsistenteIa;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CaeManager.Web.Tests;

/// <summary>
/// El botón del chat "Pregúntale a {Marca.Nombre}" no debe mostrarse en absoluto sin
/// Anthropic:ApiKey configurada — mismo principio que Sentry/Backups
/// (funciona sin configurar quedando inerte, en vez de mostrar algo roto).
/// </summary>
public class BotonAsistenteIaTests : BunitContext
{
    public BotonAsistenteIaTests()
    {
        Services.AddLocalization();
    }

    [Fact]
    public void No_se_renderiza_nada_si_no_hay_ApiKey_configurada()
    {
        Services.AddScoped<AsistenteIaService>();
        Services.AddSingleton<IOptions<AnthropicOptions>>(Options.Create(new AnthropicOptions()));
        Services.AddSingleton<IOptions<TypeSafeOptions>>(Options.Create(new TypeSafeOptions()));
        Services.AddScoped<DisponibilidadAsistente>();

        var cut = Render<BotonAsistenteIa>();

        cut.Markup.Should().BeEmpty();
    }

    [Fact]
    public void Se_renderiza_el_boton_si_hay_ApiKey_configurada()
    {
        Services.AddScoped<AsistenteIaService>();
        Services.AddSingleton<IOptions<AnthropicOptions>>(Options.Create(new AnthropicOptions { ApiKey = "sk-ant-clave-de-prueba" }));
        Services.AddSingleton<IOptions<TypeSafeOptions>>(Options.Create(new TypeSafeOptions()));
        Services.AddScoped<DisponibilidadAsistente>();

        var cut = Render<BotonAsistenteIa>();

        cut.Find("button.boton-asistente-ia").GetAttribute("data-tooltip").Should().Be($"Pregúntale a {Marca.Nombre}",
            "el texto del tooltip sale de TextosAsistenteIa (clave «Titulo»)");
    }
}
