using Bunit;
using CaeManager.Application.Comercial.Commands.RegistrarSuscripcionTenant;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// P1-E2b: salir de /estado-comercial con un Id de suscripción de Stripe pegado y sin
/// vincular pregunta antes, también mientras se confirma. Abrir el formulario sin escribir
/// nada no pregunta, y la suscripción ya vinculada no deja nada que perder.
/// </summary>
public partial class EstadoComercialGen2Tests
{
    private NavigationManager Navegacion => Services.GetRequiredService<NavigationManager>();

    [Fact]
    public async Task Aviso_abrir_vincular_sin_escribir_no_pregunta_y_con_el_id_pegado_si()
    {
        var (cut, _, _) = Montar(new Escenario());
        await BotonDeFila(cut, TenantBeitia).ClickAsync(new MouseEventArgs());
        Dialogo(cut, TituloFormulario).Should().NotBeNull("el test necesita el formulario abierto");

        await cut.SalirYComprobarQueNoPreguntaAsync(Navegacion, "el formulario vacío no tiene nada que perder");

        await EscribirIdAsync(cut, "sub_beitia");

        await cut.SalirYComprobarQuePreguntaAsync(Navegacion);
    }

    [Fact]
    public async Task Aviso_mientras_se_confirma_la_vinculacion_salir_pregunta()
    {
        var (cut, _, _) = Montar(new Escenario());
        await AbrirFormularioYPedirConfirmacionAsync(cut, TenantBeitia, "sub_beitia");
        Dialogo(cut, TituloConfirmarVincular).Should().NotBeNull("el test necesita la confirmación abierta");

        await cut.SalirYComprobarQuePreguntaAsync(Navegacion);
    }

    [Fact]
    public async Task Aviso_vinculada_la_suscripcion_salir_no_pregunta()
    {
        var (cut, mediador, _) = Montar(new Escenario());
        await AbrirFormularioYPedirConfirmacionAsync(cut, TenantBeitia, "sub_beitia");
        await BotonDe(Dialogo(cut, TituloConfirmarVincular)!, "Vincular suscripción").ClickAsync(new MouseEventArgs());
        Enviados<RegistrarSuscripcionTenantCommand>(mediador).Should().ContainSingle("si no se vinculó, el test no mide nada");

        await cut.SalirYComprobarQueNoPreguntaAsync(Navegacion, "la suscripción ya está vinculada");
    }
}
