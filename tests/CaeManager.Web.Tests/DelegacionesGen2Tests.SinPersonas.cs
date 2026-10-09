using System;
using System.Linq;
using Bunit;
using CaeManager.Application.Tenants.Queries.ObtenerDelegaciones;
using CaeManager.Infrastructure.Identity;
using FluentAssertions;
using Xunit;

namespace CaeManager.Web.Tests;

/// <summary>
/// FS-20: una delegación de Operador CAE externo sin personas decía «Sin personas autorizadas
/// todavía» y no ofrecía nada. Las personas no se añaden desde esta pantalla: las pone el
/// Operador CAE externo con una Asignación de Cartera desde <c>/usuarios</c>. El vacío dice
/// quién decide y enlaza a quien puede hacerlo.
///
/// <para>
/// <b>Lo que SÍ observa:</b> qué texto y qué enlace pinta la tarjeta según el lado de la
/// delegación, el rol de quien mira y el Tenant seleccionado. <b>Lo que NO observa:</b> la
/// autoridad sobre la cartera (<c>AutoridadSobreCarteraDeGestorCae</c>, Application.Tests) ni
/// que <c>/usuarios</c> admita esos roles (su <c>[Authorize]</c>).
/// </para>
/// </summary>
public partial class DelegacionesGen2Tests
{
    private static DelegacionDto DelegacionSinPersonas(bool soporte = false, bool activa = true, bool somosLaConsultora = true) =>
        Delegacion(soporte, activa, somosLaConsultora: somosLaConsultora) with { Operadores = [], ConsultoraNombre = "Operador Sur" };

    private void QuienMiraTieneRol(string rol) =>
        AddAuthorization().SetAuthorized("persona@operador.test").SetRoles(rol);

    [Theory]
    [InlineData(Roles.Administrador)]
    [InlineData(Roles.DireccionCae)]
    [InlineData(Roles.CoordinadorCae)]
    public void Sin_personas_el_Operador_CAE_externo_recibe_el_enlace_a_Usuarios(string rol)
    {
        QuienMiraTieneRol(rol);

        var (cut, _, _) = Renderizar(DelegacionSinPersonas());

        var vacio = cut.Find("[data-testid=sin-personas-operador]");
        vacio.TextContent.Should().Contain("Organización Norte", "nombra el Tenant propietario que nadie lleva en cartera");
        vacio.QuerySelectorAll("a").Should().ContainSingle()
            .Which.GetAttribute("href").Should().Be("/usuarios");
        cut.Markup.Should().NotContain("Sin personas autorizadas todavía");
    }

    [Theory]
    [InlineData(Roles.GestorCae)]
    [InlineData(Roles.Consulta)]
    public void Sin_personas_quien_no_puede_asignar_cartera_no_recibe_enlace_sino_a_quien_pedirlo(string rol)
    {
        QuienMiraTieneRol(rol);

        var (cut, _, _) = Renderizar(DelegacionSinPersonas());

        var vacio = cut.Find("[data-testid=sin-personas-operador]");
        vacio.QuerySelectorAll("a").Should().BeEmpty("/usuarios está cerrado para ese rol");
        vacio.TextContent.Should().Contain("Coordinador CAE");
    }

    [Fact]
    public void Sin_personas_con_otro_Tenant_seleccionado_no_hay_enlace_aunque_el_rol_lo_permita()
    {
        // La cartera se asigna desde la organización propia: con un Tenant propietario
        // seleccionado, /usuarios actuaría sobre ese Tenant.
        QuienMiraTieneRol(Roles.Administrador);
        _tenantSeleccionado = Guid.NewGuid();

        var (cut, _, _) = Renderizar(DelegacionSinPersonas());

        var vacio = cut.Find("[data-testid=sin-personas-operador]");
        vacio.QuerySelectorAll("a").Should().BeEmpty();
        vacio.TextContent.Should().Contain("en tu propia organización");
    }

    [Fact]
    public void Sin_personas_el_Tenant_propietario_lee_quien_decide_y_no_recibe_enlace()
    {
        QuienMiraTieneRol(Roles.Administrador);

        var (cut, _, _) = Renderizar(DelegacionSinPersonas(somosLaConsultora: false));

        cut.FindAll("[data-testid=sin-personas-operador]").Should().BeEmpty();
        var vacio = cut.Find("[data-testid=sin-personas-propietario]");
        vacio.TextContent.Should().Contain("Operador Sur", "las personas las decide el Operador CAE externo");
        vacio.QuerySelectorAll("a").Should().BeEmpty("el Tenant propietario no reparte la cartera del Operador CAE externo");
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void Soporte_o_delegacion_retirada_sin_personas_conservan_el_texto_neutro(bool soporte, bool activa)
    {
        QuienMiraTieneRol(Roles.Administrador);

        var (cut, _, _) = Renderizar(DelegacionSinPersonas(soporte, activa));

        cut.Markup.Should().Contain("Sin personas autorizadas todavía");
        cut.FindAll("[data-testid=sin-personas-operador]").Should().BeEmpty();
        cut.FindAll("a[href='/usuarios']").Should().BeEmpty();
    }
}
