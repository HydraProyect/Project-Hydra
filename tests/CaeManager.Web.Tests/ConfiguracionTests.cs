using Bunit;
using CaeManager.Web.Components;
using CaeManager.Web.Features.Configuracion.Pages;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

public class ConfiguracionTests : BunitContext
{
    [Fact]
    public void Subnavegacion_solo_apunta_a_rutas_internas_del_hub()
    {
        // "plataforma" es la única entrada con TipoPanel null (enlace de salida
        // puro): evita que bUnit intente montar un panel real (Usuarios, Roles...)
        // que exigiría registrar sus dependencias de DI en este test.
        var cut = Render<Configuracion>(parametros => parametros
            .Add(p => p.EntradaRuta, "plataforma"));

        var enlaces = cut.FindAll(".entrada-subnav");

        // Delegaciones y Estado comercial ya no son panel del hub (ver
        // NavMenu.razor, grupo "Plataforma"): su autoridad es de capacidad
        // AdminPlataforma, no del rol Administrador que gatea este hub —
        // 16 bajó a 14. H-4 retira "2fa" (sin política de obligatoriedad) y
        // H-2/DEC-2 añade "plataforma" (enlace de salida puro): 14 se mantiene.
        enlaces.Should().HaveCount(14);
        enlaces.Select(enlace => enlace.GetAttribute("href"))
            .Should().OnlyContain(ruta => ruta != null && ruta.StartsWith("/configuracion/", StringComparison.Ordinal));
        enlaces.Select(enlace => enlace.GetAttribute("href"))
            .Should().NotContain(new[]
            {
                "/usuarios", "/roles", "/delegaciones", "/integraciones", "/importacion",
                "/tipos-documento", "/retencion", "/auditoria", "/auditoria-ia", "/comunicaciones/macros",
                "/configuracion/delegaciones", "/configuracion/estado-comercial"
            });
    }

    [Fact]
    public void Deep_link_a_un_enlace_de_salida_puro_redirige_a_su_ruta_literal()
    {
        // "plataforma" es un enlace de salida puro (TipoPanel null,
        // EsPaginaIntegrable false — ver Configuracion.razor.cs). Si el hub
        // se lo queda y muestra el placeholder "Pantalla pendiente de
        // especificación", miente: /configuracion/plataforma sí existe. Este
        // caso solo se alcanza vía "?entry=plataforma" o un EntradaRuta
        // forzado (el clic normal de la subnav ya usa la ruta literal, que
        // gana en el router y ni siquiera instancia este componente) —
        // hallazgo de la coordinadora del turno tras revisar la primera
        // versión de esta PR, que sí dejaba pasar el mensaje falso.
        Render<Configuracion>(parametros => parametros
            .Add(p => p.EntradaRuta, "plataforma"));

        var navegacion = Services.GetRequiredService<NavigationManager>();
        navegacion.Uri.Should().EndWith("/configuracion/plataforma");
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
