using System.Reflection;
using AngleSharp.Dom;
using Bunit;
using CaeManager.Web.Components.Account.Pages;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

public class PendienteDeRolGen2Tests : BunitContext
{
    public PendienteDeRolGen2Tests() => Services.AddLocalization();

    private sealed class AntiforgeryFalso : AntiforgeryStateProvider
    {
        public override AntiforgeryRequestToken? GetAntiforgeryToken() => new("token-de-prueba", "__RequestVerificationToken");
    }

    [Fact]
    public void La_cuenta_autenticada_sin_rol_explica_el_estado_sin_prometer_un_aviso_o_un_plazo()
    {
        var cut = Render<PendienteDeRol>();

        typeof(PendienteDeRol).GetCustomAttributes<AuthorizeAttribute>().Should().ContainSingle(
            "esta pantalla es para una persona ya autenticada");
        cut.Find("#pendiente-rol-titulo").TextContent.Trim().Should().Be("Asignación de rol pendiente");
        cut.Find(".pendiente-rol-mensaje").TextContent.Should().Contain("Has iniciado sesión correctamente")
            .And.Contain("todavía no tienes un rol asignado")
            .And.Contain("no hay ninguna pantalla disponible");
        var ayuda = cut.Find("details.pendiente-rol-ayuda");
        ayuda.QuerySelector("summary")!.TextContent.Trim().Should().Be("¿Quién me asigna el rol?");
        ayuda.TextContent.Should().Contain("usuario con rol Administrador de tu organización")
            .And.Contain("Pendientes de asignar");
        // Sin promesa de aviso ni de plazo, y sin «el administrador» a secas: el aviso por
        // correo solo sale en el primer acceso con Microsoft y es best-effort.
        cut.Markup.Should().NotContain("correo").And.NotContain("avisaremos").And.NotContain("próximos momentos")
            .And.NotContain("el administrador");
    }

    [Fact]
    public void Dice_con_que_cuenta_se_ha_entrado()
    {
        AddAuthorization().SetAuthorized("marta.reyes@consultoravega.es");

        var cut = Render<PendienteDeRol>();

        cut.Find(".pendiente-rol-cuenta-etiqueta").TextContent.Trim().Should().Be("Has entrado como");
        cut.Find(".pendiente-rol-cuenta-valor").TextContent.Trim().Should().Be("marta.reyes@consultoravega.es");
    }

    [Fact]
    public void La_unica_accion_ofrecida_es_el_post_real_de_cerrar_sesion()
    {
        Services.AddScoped<AntiforgeryStateProvider, AntiforgeryFalso>();

        var cut = Render<PendienteDeRol>();
        var formularios = cut.FindAll("form").ToList();
        var botones = cut.FindAll("button").ToList();

        formularios.Should().ContainSingle("el control positivo evita comprobar atributos en una colección vacía");
        botones.Should().ContainSingle("no se ofrece una acción sin destino");
        formularios[0].GetAttribute("method").Should().Be("post");
        formularios[0].GetAttribute("action").Should().Be("/cuenta/cerrar-sesion");
        string? Campo(IElement form, string nombre) => form.QuerySelector($"input[name='{nombre}']")?.GetAttribute("value");
        Campo(formularios[0], "__RequestVerificationToken").Should().Be("token-de-prueba");
        botones[0].TextContent.Trim().Should().Be("Cerrar sesión");
        cut.FindAll("a").Should().BeEmpty("la pantalla no ofrece navegación que el guard de acceso devolvería aquí");
    }
}
