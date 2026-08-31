using Bunit;
using CaeManager.Web.Components;
using CaeManager.Web.Features.Configuracion.Pages;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;

namespace CaeManager.Web.Tests;

public class ConfiguracionTests : BunitContext
{
    [Fact]
    public void Subnavegacion_solo_apunta_a_rutas_internas_del_hub()
    {
        var cut = Render<Configuracion>(parametros => parametros
            .Add(p => p.EntradaRuta, "2fa"));

        var enlaces = cut.FindAll(".entrada-subnav");

        enlaces.Should().HaveCount(16);
        enlaces.Select(enlace => enlace.GetAttribute("href"))
            .Should().OnlyContain(ruta => ruta is not null && ruta.StartsWith("/configuracion/", StringComparison.Ordinal));
        enlaces.Select(enlace => enlace.GetAttribute("href"))
            .Should().NotContain(new[]
            {
                "/usuarios", "/roles", "/delegaciones", "/integraciones", "/importacion",
                "/tipos-documento", "/retencion", "/auditoria", "/auditoria-ia", "/comunicaciones/macros"
            });
    }

    [Fact]
    public void Deep_link_selecciona_la_entrada_y_mantiene_el_shell()
    {
        var cut = Render<Configuracion>(parametros => parametros
            .Add(p => p.EntradaRuta, "2fa"));

        cut.Find("h1").TextContent.Should().Be("Configuración");
        cut.Find(".entrada-subnav[aria-current='page'] .nombre-entrada-subnav")
            .TextContent.Should().Be("Verificación en dos pasos");
        cut.Find(".placeholder-configuracion").TextContent
            .Should().Contain("Pantalla pendiente de especificación");
    }

    [Fact]
    public void Modo_integrado_cambia_wrapper_y_nivel_del_titulo_sin_duplicar_componente()
    {
        var directa = Render<PaginaIntegrablePrueba>();
        directa.Find(".contenedor-pagina > h1").TextContent.Should().Be("Pantalla de prueba");

        var integrada = Render<PaginaIntegrablePrueba>(parametros => parametros
            .Add(p => p.IntegradaEnConfiguracion, true));
        integrada.Find(".contenido-panel-configuracion > h2").TextContent.Should().Be("Pantalla de prueba");
        integrada.FindAll("h1").Should().BeEmpty();
    }

    [Fact]
    public void Pantallas_reutilizadas_conservan_ruta_y_comparten_el_contrato_integrable()
    {
        Type[] tipos =
        [
            typeof(Features.Usuarios.Pages.Usuarios),
            typeof(Features.GestionRoles.Pages.Roles),
            typeof(Features.Delegaciones.Pages.Delegaciones),
            typeof(Features.ApiKeys.Pages.ClavesApi),
            typeof(Features.Comercial.Pages.EstadoComercial),
            typeof(Features.Integraciones.Pages.Conexiones),
            typeof(Features.Importacion.Pages.Importacion),
            typeof(Features.TiposDocumento.Pages.TiposDocumento),
            typeof(SeleccionarClienteLecturaIa),
            typeof(Features.Comunicaciones.Pages.Macros),
            typeof(Features.Retencion.Pages.Retencion),
            typeof(Features.Auditoria.Pages.Auditoria),
            typeof(Features.AuditoriaIa.Pages.AuditoriaIa)
        ];

        tipos.Should().OnlyContain(tipo => tipo.IsSubclassOf(typeof(PaginaIntegrableConfiguracionBase)));
        tipos.Should().OnlyContain(tipo => tipo.GetCustomAttributes(typeof(RouteAttribute), true).Length > 0);
    }

    private sealed class PaginaIntegrablePrueba : PaginaIntegrableConfiguracionBase
    {
        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenElement(0, "div");
            builder.AddAttribute(1, "class", ClaseContenedorPagina);
            builder.AddContent(2, TituloPaginaIntegrable("Pantalla de prueba"));
            builder.CloseElement();
        }
    }
}
