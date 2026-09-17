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

    /// <summary>
    /// HO-006-01 (REC-006): "p" → proyectos y "i" → incidencias son las dos
    /// únicas de las siete áreas nuevas con letra directa (criterio
    /// declarado en <see cref="CatalogoAtajos.DestinosNavegacion"/> — las
    /// otras cinco solo se alcanzan por la paleta). Esto prueba el lado C#
    /// del atajo; no prueba que el teclado real llegue a dispararlo — eso
    /// depende de que 'p'/'i' estén también en el TECLAS_DESTINO de
    /// atajos-globales.js, comprobado aparte en
    /// <see cref="CatalogoAtajosSincronizadoConJsTests"/> precisamente
    /// porque este test, al llamar a IrA() directamente, pasaría igual con
    /// el atajo desconectado del JS.
    /// </summary>
    [Theory]
    [InlineData("p", "/proyectos")]
    [InlineData("i", "/incidencias")]
    public void IrA_navega_a_las_dos_areas_nuevas_con_letra_directa(string tecla, string rutaEsperada)
    {
        var cut = Render<AtajosGlobales>();
        var navegacion = Services.GetRequiredService<NavigationManager>();

        cut.Instance.IrA(tecla);

        navegacion.Uri.Should().EndWith(rutaEsperada);
    }

    /// <summary>
    /// § 7.2 del handoff: "un atajo que no se anuncia no existe" — la chuleta
    /// ("?") debe listar "g p" y "g i", comprobado por prueba y no por
    /// inspección del código.
    /// </summary>
    [Fact]
    public async Task AlternarAyuda_lista_los_atajos_directos_de_las_areas_nuevas()
    {
        var cut = Render<AtajosGlobales>();

        await cut.InvokeAsync(cut.Instance.AlternarAyuda);

        cut.Markup.Should().Contain("g p").And.Contain("Ir a Proyectos");
        cut.Markup.Should().Contain("g i").And.Contain("Ir a Incidencias");
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

    /// <summary>
    /// I-1 (AUDITORIA-USUARIO-AVANZADO-POST-GEN2-2026-09-16): "n" comparaba
    /// solo el primer segmento de la ruta, así que dentro de un flujo propio
    /// anidado bajo una base con creación rápida sacaba al usuario de él
    /// (Alta Cliente y Detección Trabajadores, confirmados con efecto real) o
    /// abría un alta sin relación con la ficha abierta (los 360). Las rutas de
    /// esta teoría son las ocho enumeradas por la auditoría.
    /// </summary>
    [Theory]
    [InlineData("clientes/alta-guiada")]
    [InlineData("clientes/11111111-1111-1111-1111-111111111111/lectura-ia")]
    [InlineData("empresas/11111111-1111-1111-1111-111111111111/deteccion-trabajadores")]
    [InlineData("centros/11111111-1111-1111-1111-111111111111")]
    [InlineData("trabajadores/11111111-1111-1111-1111-111111111111")]
    [InlineData("documentos/revision-ia")]
    [InlineData("documentos/subida-masiva")]
    [InlineData("documentos/importar")]
    public void CrearAqui_no_hace_nada_en_una_subruta_de_una_base_con_creacion_rapida(string ruta)
    {
        var navegacion = Services.GetRequiredService<NavigationManager>();
        navegacion.NavigateTo(ruta);
        var cut = Render<AtajosGlobales>();
        var uriOriginal = navegacion.Uri;

        cut.Instance.CrearAqui();

        navegacion.Uri.Should().Be(uriOriginal);
    }

    /// <summary>
    /// La otra mitad del contrato de I-1: sobre la lista misma "n" sigue
    /// disparando, también con filtros en la URL o con barra final — si este
    /// caso no estuviera, un <c>CrearAqui</c> que no hiciera nunca nada
    /// pasaría la teoría de arriba entera.
    /// </summary>
    [Theory]
    [InlineData("clientes", "/clientes?accion=crear")]
    [InlineData("empresas/", "/empresas?accion=crear")]
    [InlineData("centros?q=nave&estado=Activo", "/centros?accion=crear")]
    [InlineData("trabajadores", "/trabajadores?accion=crear")]
    [InlineData("documentos", "/documentos?accion=crear")]
    public void CrearAqui_sigue_disparando_sobre_la_base_exacta(string ruta, string esperado)
    {
        var navegacion = Services.GetRequiredService<NavigationManager>();
        navegacion.NavigateTo(ruta);
        var cut = Render<AtajosGlobales>();

        cut.Instance.CrearAqui();

        navegacion.Uri.Should().EndWith(esperado);
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
