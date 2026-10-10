using Bunit;
using Bunit.TestDoubles;
using CaeManager.Application.Operaciones.ApoyoCartera.Commands;
using CaeManager.Application.Operaciones.ApoyoCartera.Queries;
using CaeManager.Application.Usuarios.Queries.ObtenerPersonasConCartera;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.Empresas.Pages;
using CaeManager.Web.Features.IncorporacionCartera.Components;
using FluentAssertions;

namespace CaeManager.Web.Tests;

/// <summary>
/// Listado de Empresas y las dos acciones de su cabecera «Gestor CAE»: qué hace la PÁGINA cuando una termina.
///
/// <para>
/// Tras «Desasignarme», quien mira ya no tiene acceso al Tenant propietario cuyos datos pinta la página: se
/// recarga entera (<c>forceLoad</c>) en su propia ruta, sin filtros, para que el arranque normal de la
/// aplicación resuelva de nuevo el Tenant activo y el acceso. Tras «+ Dar acceso» no: ahí solo cambia el dato
/// de la cabecera, que se relee sola.
/// </para>
///
/// <para>
/// Lo que estos tests NO observan: adónde aterriza el usuario tras la recarga. Eso lo decide el arranque de la
/// aplicación con la sesión real, no esta página; aquí se fija que la navegación se pide, con recarga
/// completa y a la ruta de la lista. Quién puede desasignarse lo decide <c>DesasignarmeDeApoyoCommand</c>.
/// </para>
/// </summary>
public partial class EmpresasListaGen2Tests
{
    private static readonly PersonaConCartera Marta = new(Guid.NewGuid(), "Marta Ibarra", "GestorCae", null);

    private BunitNavigationManager Historial => (BunitNavigationManager)Navegacion;

    private int RecargasCompletas() => Historial.History.Count(h => h.Options.ForceLoad);

    [Fact]
    public async Task Desasignarme_desde_la_cabecera_lleva_a_Mi_trabajo_con_el_aviso_de_acceso_perdido()
    {
        var tenant = Guid.NewGuid();
        Seleccion = new SeleccionEmpresaGestionadaDePrueba(tenant);
        var ajena = new CarterasDeOperacion(
            Guid.NewGuid(), tenant, "Talleres Norte", Marta, [new PersonaConCartera(Yo, "Nahia Urrutia", "GestorCae", null)]);
        var mio = new ApoyoDeCarteraDto(
            Guid.NewGuid(), tenant, "Talleres Norte", ajena.AsignacionOperacionId, Yo, "Nahia Urrutia", Marta.UsuarioId, Marta.Nombre, null);
        var mediador = new MediatorFalso
        {
            Almacen = { Empresa("Aislamientos Nervión S.L.") },
            Carteras = { ajena },
            Apoyos = new ApoyosDeCarteraDto([mio], [], [])
        };
        var cut = Renderizar(mediador, "empresas?q=nervion");

        await cut.InvokeAsync(() => cut.Find("[data-desasignarme-cabecera]").Click());

        mediador.Enviadas.OfType<DesasignarmeDeApoyoCommand>().Should().BeEmpty("quitarse un Tenant no se deshace: pregunta antes");
        RecargasCompletas().Should().Be(0, "preguntar no recarga");
        var dialogo = cut.FindComponents<DialogoConfirmacion>().Single(d => d.Instance.Visible);

        await cut.InvokeAsync(() => dialogo.Instance.OnConfirmar.InvokeAsync());

        mediador.Enviadas.OfType<DesasignarmeDeApoyoCommand>().Should().ContainSingle()
            .Which.Should().Be(new DesasignarmeDeApoyoCommand(mio.PropuestaId), "control: la acción se envió");
        Historial.History.Where(h => h.Options.ForceLoad).Should().ContainSingle(
            "quien mira ya no tiene acceso a este Tenant: la página no puede seguir pintando sus datos")
            .Which.Uri.Should().Be("/mi-trabajo?sinAcceso=true", "a Mi trabajo, con el aviso de que ya no hay acceso a ese Tenant");
        new Uri(Navegacion.Uri).PathAndQuery.Should().Be("/mi-trabajo?sinAcceso=true");
    }

    [Fact]
    public async Task Dar_acceso_desde_la_cabecera_no_recarga_la_pagina()
    {
        var tenant = Guid.NewGuid();
        Seleccion = new SeleccionEmpresaGestionadaDePrueba(tenant);
        var mia = new CarterasDeOperacion(
            Guid.NewGuid(), tenant, "Talleres Norte", new PersonaConCartera(Yo, "Nahia Urrutia", "GestorCae", null), []);
        var lucia = new DestinatarioDeApoyoDto(Guid.NewGuid(), "Lucía Garmendia");
        var mediador = new MediatorFalso
        {
            Almacen = { Empresa("Aislamientos Nervión S.L.") },
            Carteras = { mia },
            DestinatariosDeApoyo = { lucia }
        };
        var cut = Renderizar(mediador, "empresas?q=nervion");
        var panel = cut.FindComponent<PanelDarAcceso>();

        await cut.InvokeAsync(() => cut.Find("[data-dar-acceso-cabecera]").Click());
        panel.Find("select").Change(lucia.UsuarioId.ToString());
        await cut.InvokeAsync(() => panel.FindAll("button").Single(b => b.TextContent.Trim() == "Proponer").Click());

        mediador.Enviadas.OfType<ProponerApoyoCarteraCommand>().Should().ContainSingle()
            .Which.Should().Be(new ProponerApoyoCarteraCommand(mia.AsignacionOperacionId, lucia.UsuarioId), "control: la acción se envió");
        RecargasCompletas().Should().Be(0, "dar acceso no quita el acceso a quien mira: basta con releer la cabecera");
        new Uri(Navegacion.Uri).PathAndQuery.Should().Be("/empresas?q=nervion", "los filtros siguen donde estaban");
    }
}
