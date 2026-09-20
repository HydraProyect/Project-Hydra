using CaeManager.Application.VistaDemo;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Components.Layout;
using CaeManager.Web.Services;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;

namespace CaeManager.Web.Tests;

/// <summary>
/// La cookie del selector de vista de demo es PETICIÓN, no autoridad: aquí se prueba que nada que no
/// sea un valor bien formado, emitido para esta misma cuenta y vigente llega siquiera a validarse, y
/// que el menú por vista solo puede ocultar. Que la petición valga (Tenant de demo, rol, Gestor
/// elegible) lo prueban los tests de integración de <c>VistaDemoLenteTests</c>.
/// </summary>
public class VistaDemoCookieTests
{
    private static readonly Guid Cuenta = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid OtraCuenta = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid Gestor = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");

    private static IDataProtectionProvider Proveedor() => new EphemeralDataProtectionProvider();

    [Theory]
    [InlineData(VistaDemo.Direccion, false)]
    [InlineData(VistaDemo.CoordinadorCae, false)]
    [InlineData(VistaDemo.GestorCae, true)]
    public void Un_valor_emitido_para_esta_cuenta_se_lee_de_vuelta(VistaDemo vista, bool conGestor)
    {
        var proveedor = Proveedor();
        var valor = VistaDemoCookie.Proteger(proveedor, Cuenta, vista, conGestor ? Gestor : null);

        VistaDemoCookie.LeerCargaUtil(proveedor, valor, Cuenta)
            .Should().Be((vista, conGestor ? Gestor : (Guid?)null));
    }

    [Fact]
    public void El_valor_de_otra_cuenta_no_vale()
    {
        var proveedor = Proveedor();
        var valor = VistaDemoCookie.Proteger(proveedor, OtraCuenta, VistaDemo.GestorCae, Gestor);

        VistaDemoCookie.LeerCargaUtil(proveedor, valor, Cuenta).Should().Be((null, null));
    }

    [Fact]
    public void Sin_usuario_autenticado_no_vale_ningun_valor()
    {
        var proveedor = Proveedor();
        var valor = VistaDemoCookie.Proteger(proveedor, Cuenta, VistaDemo.CoordinadorCae, null);

        VistaDemoCookie.LeerCargaUtil(proveedor, valor, null).Should().Be((null, null));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("texto-plano")]
    [InlineData("2|GestorCae")]
    public void Un_valor_ausente_o_sin_proteger_se_ignora(string? valor) =>
        VistaDemoCookie.LeerCargaUtil(Proveedor(), valor, Cuenta).Should().Be((null, null));

    [Fact]
    public void Un_valor_manipulado_se_ignora()
    {
        var proveedor = Proveedor();
        var valor = VistaDemoCookie.Proteger(proveedor, Cuenta, VistaDemo.GestorCae, Gestor);
        var manipulado = valor[..^4] + (valor.EndsWith("AAAA") ? "BBBB" : "AAAA");

        VistaDemoCookie.LeerCargaUtil(proveedor, manipulado, Cuenta).Should().Be((null, null));
    }

    [Fact]
    public void Un_valor_protegido_con_otro_llavero_se_ignora()
    {
        var valor = VistaDemoCookie.Proteger(Proveedor(), Cuenta, VistaDemo.GestorCae, Gestor);

        VistaDemoCookie.LeerCargaUtil(Proveedor(), valor, Cuenta).Should().Be((null, null));
    }

    [Fact]
    public void Un_valor_protegido_para_otro_proposito_se_ignora()
    {
        var proveedor = Proveedor();
        var ajeno = proveedor.CreateProtector("otro.proposito").ToTimeLimitedDataProtector()
            .Protect($"{Cuenta:N}|{(int)VistaDemo.CoordinadorCae}|{Guid.Empty:N}", TimeSpan.FromHours(1));

        VistaDemoCookie.LeerCargaUtil(proveedor, ajeno, Cuenta).Should().Be((null, null));
    }

    [Fact]
    public void Un_valor_caducado_se_ignora()
    {
        var proveedor = Proveedor();
        var caducado = proveedor.CreateProtector("CaeManager.Web.VistaDemoCookie.v1").ToTimeLimitedDataProtector()
            .Protect($"{Cuenta:N}|{(int)VistaDemo.CoordinadorCae}|{Guid.Empty:N}", TimeSpan.FromMilliseconds(1));
        Thread.Sleep(50);

        VistaDemoCookie.LeerCargaUtil(proveedor, caducado, Cuenta).Should().Be((null, null));
    }

    [Theory]
    [InlineData("{0:N}|9|{1:N}")] // vista fuera del enum
    [InlineData("{0:N}|abc|{1:N}")] // vista no numérica
    [InlineData("{0:N}|2|{2}")] // vista Gestor con Gestor no parseable
    [InlineData("{0:N}|2|{3:N}")] // vista Gestor sin Gestor (Guid.Empty)
    [InlineData("{0:N}|1|{1:N}")] // vista Coordinador con Gestor
    [InlineData("{0:N}|0|{1:N}")] // vista Dirección con Gestor
    [InlineData("{0:N}|1")] // carga incompleta
    [InlineData("{0:N}|1|{3:N}|sobra")] // carga con campos de más
    public void Una_carga_bien_firmada_pero_incoherente_se_ignora(string plantilla)
    {
        var proveedor = Proveedor();
        var carga = string.Format(plantilla, Cuenta, Gestor, "no-es-un-guid", Guid.Empty);
        var valor = proveedor.CreateProtector("CaeManager.Web.VistaDemoCookie.v1").ToTimeLimitedDataProtector()
            .Protect(carga, TimeSpan.FromHours(1));

        VistaDemoCookie.LeerCargaUtil(proveedor, valor, Cuenta).Should().Be((null, null));
    }

    // ---------------------------------------------------------------- Menú por vista

    [Theory]
    [InlineData(null)]
    [InlineData(VistaDemo.Direccion)]
    public void Direccion_y_sin_lente_dejan_el_menu_como_esta(VistaDemo? vista)
    {
        MenuPorVista.Acotar(vista, "Administrador,DireccionCae").Should().Be("Administrador,DireccionCae");
        MenuPorVista.Acotar(vista, Roles.CoordinadorCae).Should().Be(Roles.CoordinadorCae);
    }

    [Fact]
    public void La_vista_Coordinador_oculta_lo_que_no_es_del_Coordinador_y_deja_lo_que_lo_es()
    {
        var conCoordinador = $"{Roles.Administrador},{Roles.CoordinadorCae}";
        MenuPorVista.Acotar(VistaDemo.CoordinadorCae, conCoordinador).Should().Be(conCoordinador);
        MenuPorVista.Acotar(VistaDemo.CoordinadorCae, $"{Roles.Administrador},{Roles.DireccionCae}")
            .Should().Be(MenuPorVista.RolInexistente);
    }

    [Fact]
    public void La_vista_Gestor_oculta_lo_que_no_es_del_Gestor_y_deja_lo_que_lo_es()
    {
        var conGestor = $"{Roles.Administrador}, {Roles.GestorCae}";
        MenuPorVista.Acotar(VistaDemo.GestorCae, conGestor).Should().Be(conGestor);
        MenuPorVista.Acotar(VistaDemo.GestorCae, $"{Roles.Administrador},{Roles.CoordinadorCae}")
            .Should().Be(MenuPorVista.RolInexistente);
    }

    [Theory]
    [InlineData(VistaDemo.CoordinadorCae)]
    [InlineData(VistaDemo.GestorCae)]
    public void El_menu_por_vista_nunca_anade_un_rol_que_la_lista_no_traia(VistaDemo vista)
    {
        // "Solo puede ocultar": el resultado es la lista original o el rol inexistente, nunca otra cosa.
        foreach (var roles in new[] { "Administrador", Roles.CoordinadorCae, Roles.GestorCae, "Administrador,Consulta", "" })
            new[] { roles, MenuPorVista.RolInexistente }.Should().Contain(MenuPorVista.Acotar(vista, roles));

        Roles.Todos.Should().NotContain(MenuPorVista.RolInexistente, "ningún usuario real puede tener el rol que oculta");
    }
}
