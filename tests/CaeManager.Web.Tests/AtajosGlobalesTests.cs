using Bunit;
using CaeManager.Web.Features.AtajosGlobales;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// Fase D ("Atajos globales tipo Linear"): la parte de JS interop
/// (atajos-globales.js) queda fuera — JSInterop.Mode = Loose deja pasar la
/// importación del módulo sin exigir que el test la configure a mano; lo
/// que se prueba aquí es la lógica de C# que reciben los métodos
/// [JSInvokable] cuando el JS ya decidió qué tecla se pulsó.
/// </summary>
public class AtajosGlobalesTests : BunitContext
{
    public AtajosGlobalesTests() => JSInterop.Mode = JSRuntimeMode.Loose;

    [Fact]
    public void IrA_navega_al_destino_de_la_letra()
    {
        var cut = Render<AtajosGlobales>();
        var navegacion = Services.GetRequiredService<NavigationManager>();

        cut.Instance.IrA("b");

        navegacion.Uri.Should().EndWith("/bandeja");
    }

    [Fact]
    public void IrA_con_una_letra_sin_destino_no_navega()
    {
        var cut = Render<AtajosGlobales>();
        var navegacion = Services.GetRequiredService<NavigationManager>();
        var uriOriginal = navegacion.Uri;

        cut.Instance.IrA("z");

        navegacion.Uri.Should().Be(uriOriginal);
    }

    [Fact]
    public void CrearAqui_anade_accion_crear_en_una_pagina_con_creacion_rapida()
    {
        var navegacion = Services.GetRequiredService<NavigationManager>();
        navegacion.NavigateTo("clientes");
        var cut = Render<AtajosGlobales>();

        cut.Instance.CrearAqui();

        navegacion.Uri.Should().EndWith("/clientes?accion=crear");
    }

    [Fact]
    public void CrearAqui_no_hace_nada_fuera_de_una_pagina_con_creacion_rapida()
    {
        var navegacion = Services.GetRequiredService<NavigationManager>();
        navegacion.NavigateTo("dashboard-ejecutivo");
        var cut = Render<AtajosGlobales>();
        var uriOriginal = navegacion.Uri;

        cut.Instance.CrearAqui();

        navegacion.Uri.Should().Be(uriOriginal);
    }

    [Fact]
    public async Task AlternarAyuda_muestra_y_oculta_el_chuleta()
    {
        var cut = Render<AtajosGlobales>();
        cut.Markup.Should().NotContain("Atajos de teclado");

        await cut.InvokeAsync(cut.Instance.AlternarAyuda);
        cut.Markup.Should().Contain("Atajos de teclado");

        await cut.InvokeAsync(cut.Instance.AlternarAyuda);
        cut.Markup.Should().NotContain("Atajos de teclado");
    }
}
