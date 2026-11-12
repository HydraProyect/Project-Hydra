using Bunit;
using Bunit.TestDoubles;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Components.Layout;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CaeManager.Web.Tests;

/// <summary>
/// Botón «Subir» de la cabecera (marco de Design, marco-talveg.js .tvm-subir; inventario visual 2026-11-07,
/// grupo A). No es un flujo nuevo: es un enlace a la pantalla de subida de documentos que ya existe
/// (<c>/documentos/subida-masiva</c>, la misma a la que lleva el atajo «u»).
/// </summary>
public class BotonSubirCabeceraTests : BunitContext
{
    public BotonSubirCabeceraTests() => Services.AddLocalization();

    [Theory]
    [InlineData(Roles.Administrador)]
    [InlineData(Roles.DireccionCae)]
    [InlineData(Roles.CoordinadorCae)]
    [InlineData(Roles.GestorCae)]
    public void Los_roles_que_suben_ven_un_enlace_primario_a_la_pantalla_de_subida(string rol)
    {
        AddAuthorization().SetAuthorized("usuario").SetRoles(rol);

        var cut = Render<BotonSubirCabecera>();

        var enlace = cut.Find("a[data-subir-cabecera]");
        enlace.GetAttribute("href").Should().Be("/documentos/subida-masiva");
        enlace.TextContent.Trim().Should().Be("Subir");
        enlace.GetAttribute("aria-label").Should().Be("Subir documentos", "con solo el icono (cabecera compacta) el enlace conserva su nombre accesible");
        enlace.QuerySelector("svg").Should().NotBeNull("lleva icono");
    }

    [Fact]
    public void El_rol_Consulta_no_ve_el_boton_porque_la_pantalla_de_subida_le_es_de_solo_lectura()
    {
        AddAuthorization().SetAuthorized("usuario").SetRoles(Roles.Consulta);

        Render<BotonSubirCabecera>().FindAll("a[data-subir-cabecera]").Should().BeEmpty();
    }

    [Fact]
    public void Sin_sesion_no_se_pinta_nada()
    {
        AddAuthorization().SetNotAuthorized();

        Render<BotonSubirCabecera>().Markup.Should().NotContain("data-subir-cabecera");
    }

    [Fact]
    public void Los_roles_que_ven_el_boton_los_admite_la_pagina_de_subida()
    {
        // El botón no concede nada: solo ofrece un destino. Si la página dejara de admitir un rol, el
        // botón no debe seguir ofreciéndoselo.
        var autorizados = typeof(CaeManager.Web.Features.Documentos.Pages.SubidaMasiva)
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
            .Cast<AuthorizeAttribute>()
            .SelectMany(a => (a.Roles ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .ToHashSet();

        var ofrecidos = BotonSubirCabecera.RolesQueSuben.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        autorizados.Should().NotBeEmpty("control positivo: la página declara sus roles");
        ofrecidos.Should().OnlyContain(r => autorizados.Contains(r));
    }
}