using System.Security.Claims;
using CaeManager.Application.Common;
using CaeManager.Web.Services.Autorizacion;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Xunit;

namespace CaeManager.Web.Tests.Autorizacion;

/// <summary>
/// La puerta de las fichas 360 con «Nota interna»: entra el equipo de la página por rol, o la Sesión Privilegiada con
/// acceso total al Tenant. Sin ninguna de las dos, no entra nadie (fallo cerrado).
/// </summary>
public class RequisitoEquipoOSoporteConAccesoTotalHandlerTests
{
    [Theory]
    [InlineData("Administrador")]
    [InlineData("GestorCae")]
    public async Task El_equipo_de_la_pagina_entra_por_rol_sin_sesion(string rol)
    {
        var resultado = await Evaluar(PoliticasFichas360.RolesTipoDocumento, rol: rol, accesoTotal: false);

        resultado.Should().BeTrue();
    }

    /// <summary>Soporte TALVEG: sin rol de negocio (el middleware le quita los claims), con acceso total al Tenant.</summary>
    [Fact]
    public async Task Sesion_privilegiada_con_acceso_total_entra_sin_rol_de_negocio()
    {
        var resultado = await Evaluar(PoliticasFichas360.RolesTipoDocumento, rol: null, accesoTotal: true);

        resultado.Should().BeTrue();
    }

    /// <summary>Fallo cerrado: sin rol de equipo y sin sesión de acceso total, la página no se abre.</summary>
    [Fact]
    public async Task Sin_rol_de_equipo_y_sin_sesion_no_entra_nadie()
    {
        var resultado = await Evaluar(PoliticasFichas360.RolesTipoDocumento, rol: null, accesoTotal: false);

        resultado.Should().BeFalse();
    }

    [Fact]
    public async Task El_usuario_de_Cliente_no_entra_aunque_tenga_rol_de_Cliente_y_no_hay_sesion()
    {
        var resultado = await Evaluar(PoliticasFichas360.RolesTipoDocumento, rol: "Cliente", accesoTotal: false);

        resultado.Should().BeFalse();
    }

    /// <summary>
    /// La lista de cada página es la suya: Consulta lee la ficha Vehículo 360 y no la de Tipo de documento 360, que solo
    /// abre el equipo de gestión (igual que la consulta de la aplicación).
    /// </summary>
    [Fact]
    public async Task Consulta_entra_en_Vehiculo_360_pero_no_en_Tipo_de_documento_360()
    {
        (await Evaluar(PoliticasFichas360.RolesVehiculo, rol: "Consulta", accesoTotal: false)).Should().BeTrue();
        (await Evaluar(PoliticasFichas360.RolesTipoDocumento, rol: "Consulta", accesoTotal: false)).Should().BeFalse();
    }

    /// <summary>
    /// Cuadro de dirección: Dirección, Coordinación y Administrador del Tenant por rol; el administrador de TALVEG solo
    /// con Sesión Privilegiada de acceso total. Gestor CAE, Consulta y Cliente no entran.
    /// </summary>
    [Theory]
    [InlineData("DireccionCae", false, true)]
    [InlineData("CoordinadorCae", false, true)]
    [InlineData("Administrador", false, true)]
    [InlineData(null, true, true)]
    [InlineData(null, false, false)]
    [InlineData("GestorCae", false, false)]
    [InlineData("Consulta", false, false)]
    [InlineData("Cliente", false, false)]
    public async Task Cuadro_de_direccion_admite_a_los_administradores_y_a_la_sesion_privilegiada(
        string? rol, bool accesoTotal, bool entra)
    {
        var resultado = await Evaluar(PoliticaCuadroDireccion.Roles, rol, accesoTotal);

        resultado.Should().Be(entra);
    }

    private static async Task<bool> Evaluar(string[] rolesDeEquipo, string? rol, bool accesoTotal)
    {
        var identidad = rol is null
            ? new ClaimsIdentity("prueba")
            : new ClaimsIdentity([new Claim(ClaimTypes.Role, rol)], "prueba", ClaimTypes.Name, ClaimTypes.Role);
        var usuario = new ClaimsPrincipal(identidad);
        var requisito = new RequisitoEquipoOSoporteConAccesoTotal(rolesDeEquipo);
        var contexto = new AuthorizationHandlerContext([requisito], usuario, resource: null);

        await new RequisitoEquipoOSoporteConAccesoTotalHandler(new PoliticaFalsa(accesoTotal)).HandleAsync(contexto);

        return contexto.HasSucceeded;
    }

    private sealed class PoliticaFalsa(bool accesoTotal) : IPoliticaNotaInterna
    {
        public Task<bool> PuedeLeerAsync(CancellationToken cancellationToken = default) => Task.FromResult(accesoTotal);
        public Task<bool> TieneAccesoTotalAlTenantAsync(CancellationToken cancellationToken = default) => Task.FromResult(accesoTotal);
    }
}
