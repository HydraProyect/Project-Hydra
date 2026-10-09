using Bunit;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Features.AtajosGlobales;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// Resto del defecto C1 del piloto Outbound (lote 2). El menú dejó de ofrecer «Mi trabajo» al rol
/// Consulta, al que <c>/bandeja</c> y <c>/mi-trabajo</c> deniegan el acceso, pero los atajos «g b» y
/// «g m» seguían llevándolo allí y la chuleta («?») seguía anunciándolos.
///
/// <para>
/// <b>Qué observa y qué no.</b> Como <see cref="AtajosGlobalesTests"/>, llama a los métodos
/// <c>[JSInvokable]</c>: prueba lo que el componente hace con la tecla, no que el teclado la envíe
/// (<c>atajos-globales.js</c> sigue enviando las diez letras a cualquier cuenta). El control positivo
/// de cada caso —la misma tecla con una cuenta que sí tiene Mi trabajo— está en
/// <see cref="AtajosGlobalesTests"/>, cuya cuenta es un Gestor CAE.
/// </para>
/// </summary>
public class AtajosGlobalesSinMiTrabajoTests : BunitContext
{
    public AtajosGlobalesSinMiTrabajoTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLocalization();
    }

    [Theory]
    [InlineData("b")]
    [InlineData("m")]
    public void A_una_cuenta_sin_Mi_trabajo_el_atajo_no_la_lleva_alli(string tecla)
    {
        AddAuthorization().SetAuthorized("consulta").SetRoles(Roles.Consulta);
        var cut = Render<AtajosGlobales>();
        var navegacion = Services.GetRequiredService<NavigationManager>();
        var uriOriginal = navegacion.Uri;

        cut.Instance.IrA(tecla);

        navegacion.Uri.Should().Be(uriOriginal, "esas dos páginas deniegan el acceso al rol Consulta");
    }

    /// <summary>Control: el filtro quita Mi trabajo y nada más; el resto de «g + letra» sigue igual.</summary>
    [Fact]
    public void A_una_cuenta_sin_Mi_trabajo_los_demas_atajos_le_siguen_funcionando()
    {
        AddAuthorization().SetAuthorized("consulta").SetRoles(Roles.Consulta);
        var cut = Render<AtajosGlobales>();
        var navegacion = Services.GetRequiredService<NavigationManager>();

        cut.Instance.IrA("c");

        navegacion.Uri.Should().EndWith("/clientes");
    }

    [Fact]
    public async Task A_una_cuenta_sin_Mi_trabajo_la_chuleta_no_le_anuncia_esos_dos_atajos()
    {
        AddAuthorization().SetAuthorized("consulta").SetRoles(Roles.Consulta);
        var cut = Render<AtajosGlobales>();

        await cut.InvokeAsync(cut.Instance.AlternarAyuda);

        var teclas = cut.FindAll(".chuleta-atajos-fila kbd").Select(k => k.TextContent).ToList();
        teclas.Should().Contain("g c", "control: la sección Navegación está pintada");
        teclas.Should().NotContain("g b").And.NotContain("g m");
        cut.Markup.Should().NotContain("Ir a Mi trabajo");
    }

    /// <summary>
    /// Sin estado de autenticación en cascada (otro host, un arnés que no lo declara) no se sabe si
    /// la cuenta tiene Mi trabajo: se deja de ofrecer, nunca al revés.
    /// </summary>
    [Fact]
    public void Sin_estado_de_autenticacion_el_atajo_a_Mi_trabajo_no_navega()
    {
        var cut = Render<AtajosGlobales>();
        var navegacion = Services.GetRequiredService<NavigationManager>();
        var uriOriginal = navegacion.Uri;

        cut.Instance.IrA("b");

        navegacion.Uri.Should().Be(uriOriginal);
    }
}
