using CaeManager.Application.Usuarios;
using CaeManager.Infrastructure.Identity;
using FluentAssertions;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// <see cref="AutoridadSobreCuentas"/> repite con literales los nombres de rol de
/// <see cref="Roles"/>, porque Application no referencia Infrastructure.Identity.
/// Si divergen, un rol nuevo en Identity no se podría conceder desde /usuarios ni
/// /roles (el Command lo daría por inexistente), o uno retirado seguiría
/// aceptándose; y quien administra cuentas dejaría de coincidir con el
/// <c>[Authorize]</c> de /usuarios.
/// </summary>
public class RolesDeCuentasParidadTests
{
    [Fact]
    public void Los_roles_que_los_Commands_de_cuentas_aceptan_son_los_de_Identity()
    {
        AutoridadSobreCuentas.RolesExistentes.Should().BeEquivalentTo(Roles.Todos);
    }

    [Fact]
    public void Los_literales_de_rol_de_AutoridadSobreCuentas_existen_en_Identity()
    {
        Roles.Todos.Should().Contain(
        [
            AutoridadSobreCuentas.Administrador, AutoridadSobreCuentas.RolGestorCae, AutoridadSobreCuentas.RolCliente,
        ]);
    }

    [Fact]
    public void Quien_gestiona_cuentas_en_Application_es_quien_abre_la_pagina_de_usuarios()
    {
        var atributo = typeof(CaeManager.Web.Features.Usuarios.Pages.Usuarios)
            .GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute), inherit: true)
            .Cast<Microsoft.AspNetCore.Authorization.AuthorizeAttribute>()
            .Single(a => a.Roles is not null);

        atributo.Roles!.Split(',', StringSplitOptions.TrimEntries)
            .Should().BeEquivalentTo(AutoridadSobreCuentas.RolesQueGestionanCuentas,
                "si la página se abre a un rol que los Commands rechazan, o al revés, la autorización vuelve a vivir en la página");
    }
}
