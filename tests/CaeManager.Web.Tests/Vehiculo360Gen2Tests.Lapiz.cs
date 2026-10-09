using Bunit;
using CaeManager.Application.Vehiculos.Queries.ObtenerVehiculoPorId;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Components.Workspace;
using CaeManager.Web.Features.Vehiculos.Components;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// Patrón de listados sin menú «⋯» (decisión 2026-10-08), en el panel de Vehículo: el lápiz
/// de la cabecera en todas las pestañas, el icono 360 a la página completa y la tecla «e» de
/// la lista (<c>ContextWorkspaceService.AbrirEnEdicionAsync</c>).
/// </summary>
public partial class Vehiculo360Gen2Tests
{
    private const string LapizVehiculo = "button[aria-label='Editar información del vehículo']";

    private ContextWorkspaceService RegistrarWorkspace()
    {
        var workspace = new ContextWorkspaceService();
        Services.AddScoped(_ => workspace);
        return workspace;
    }

    private static bool EnEdicion(IRenderedComponent<VehiculoWorkspacePanel> cut) =>
        cut.FindAll(".workspace-acciones-edicion button").Any(b => b.TextContent.Trim() == "Guardar");

    [Fact]
    public void La_cabecera_lleva_el_lapiz_y_el_icono_360_a_la_pagina_completa()
    {
        var id = Guid.NewGuid();
        Registrar(new MediadorFalso()).Detalles[id] = Detalle(id, "Furgoneta de obra");

        var cut = RenderizarPanel(id);

        cut.Find(LapizVehiculo).GetAttribute("title").Should().Be("Editar (e)");
        cut.Find("a.boton-360-pagina").GetAttribute("href").Should().Be($"/vehiculos/{id}");
    }

    /// <summary>Desde otra pestaña el lápiz pide «Información», que es donde está el formulario.</summary>
    [Fact]
    public async Task Desde_otra_pestana_el_lapiz_lleva_a_Informacion()
    {
        var id = Guid.NewGuid();
        Registrar(new MediadorFalso()).Detalles[id] = Detalle(id, "Furgoneta de obra");
        var pedidas = new List<string>();
        // «historial» no se usa: su pestaña pide una puerta de datos que este test no registra.
        var cut = Render<VehiculoWorkspacePanel>(p => p.Add(x => x.EntidadId, id).Add(x => x.PestanaActiva, "otra")
            .Add(x => x.PestanaActivaChanged, EventCallback.Factory.Create<string>(this, pedidas.Add)));

        await cut.Find(LapizVehiculo).ClickAsync(new MouseEventArgs());

        pedidas.Should().Equal("informacion");
        cut.FindAll(LapizVehiculo).Should().BeEmpty("mientras se edita no hay lápiz");
    }

    [Fact]
    public async Task La_peticion_de_edicion_de_la_tecla_e_equivale_a_pulsar_el_lapiz()
    {
        var id = Guid.NewGuid();
        var workspace = RegistrarWorkspace();
        Registrar(new MediadorFalso()).Detalles[id] = Detalle(id, "Furgoneta de obra");
        var cut = RenderizarPanel(id);
        EnEdicion(cut).Should().BeFalse("control positivo: se parte de lectura");

        await cut.InvokeAsync(() => workspace.AbrirEnEdicionAsync(EntidadWorkspace.Vehiculo, id, "Furgoneta de obra"));

        EnEdicion(cut).Should().BeTrue();
    }

    [Fact]
    public async Task Una_peticion_que_llego_antes_de_montar_el_panel_se_atiende_al_cargar()
    {
        var id = Guid.NewGuid();
        var workspace = RegistrarWorkspace();
        await workspace.AbrirEnEdicionAsync(EntidadWorkspace.Vehiculo, id, "Furgoneta de obra");
        Registrar(new MediadorFalso()).Detalles[id] = Detalle(id, "Furgoneta de obra");

        var cut = RenderizarPanel(id);

        cut.WaitForAssertion(() => EnEdicion(cut).Should().BeTrue());
    }

    [Fact]
    public async Task Una_peticion_para_otra_ficha_no_pone_esta_en_edicion()
    {
        var id = Guid.NewGuid();
        var workspace = RegistrarWorkspace();
        Registrar(new MediadorFalso()).Detalles[id] = Detalle(id, "Furgoneta de obra");
        var cut = RenderizarPanel(id);

        await cut.InvokeAsync(() => workspace.AbrirEnEdicionAsync(EntidadWorkspace.Vehiculo, Guid.NewGuid(), "Camión grúa"));

        EnEdicion(cut).Should().BeFalse();
        cut.FindAll(LapizVehiculo).Should().ContainSingle();
    }

    [Fact]
    public async Task Un_rol_de_Consulta_no_entra_en_edicion_con_la_tecla_e()
    {
        this.ConRolDeEscritura(Roles.Consulta);
        var id = Guid.NewGuid();
        var workspace = RegistrarWorkspace();
        Registrar(new MediadorFalso()).Detalles[id] = Detalle(id, "Furgoneta de obra");
        var cut = RenderizarPanel(id);
        cut.Find("a.boton-360-pagina").Should().NotBeNull("control positivo: la cabecera está pintada");

        await cut.InvokeAsync(() => workspace.AbrirEnEdicionAsync(EntidadWorkspace.Vehiculo, id, "Furgoneta de obra"));

        workspace.ConsumirEdicionSolicitada(EntidadWorkspace.Vehiculo, id).Should().BeFalse(
            "control positivo: la petición llegó al panel, que la consumió sin atenderla");
        EnEdicion(cut).Should().BeFalse("EditarVehiculoCommand está denegado a Consulta");
    }

    /// <summary>
    /// Una petición de edición no sobrevive a la carga fallida ni a una ficha que ya no existe:
    /// si se quedara pendiente, un «Reintentar» con éxito entraría en edición sin pedirlo.
    /// </summary>
    [Theory]
    [InlineData(true)]   // la consulta lanza
    [InlineData(false)]  // la consulta responde sin detalle (otro usuario lo eliminó)
    public async Task Si_la_ficha_no_carga_o_ya_no_existe_la_peticion_de_edicion_se_descarta(bool lanza)
    {
        var id = Guid.NewGuid();
        var workspace = RegistrarWorkspace();
        await workspace.AbrirEnEdicionAsync(EntidadWorkspace.Vehiculo, id, "Furgoneta de obra");
        var m = Registrar(new MediadorFalso());
        if (lanza)
            m.Fallar = x => x is ObtenerVehiculoPorIdQuery ? new InvalidOperationException("Fallo simulado de la carga.") : null;

        var cut = RenderizarPanel(id);

        cut.FindAll(LapizVehiculo).Should().BeEmpty("control positivo: sin detalle no hay cabecera");
        workspace.ConsumirEdicionSolicitada(EntidadWorkspace.Vehiculo, id).Should().BeFalse("la carga sin ficha la descartó");

        m.Fallar = null;
        m.Detalles[id] = Detalle(id, "Furgoneta de obra");
        await cut.FindAll("button").Single(b => b.TextContent.Trim() == "Reintentar").ClickAsync(new MouseEventArgs());

        cut.Find(LapizVehiculo).Should().NotBeNull("control positivo: el reintento cargó la ficha");
        EnEdicion(cut).Should().BeFalse("nadie pidió editar tras la carga sin ficha");
    }

    /// <summary>
    /// A cargada, se abre B y se vuelve a A: mientras la segunda carga de A no responde, el
    /// detalle en memoria es el de la primera visita. Una «e» en ese hueco espera a la carga, y
    /// el formulario se rellena con lo recién leído, no con la copia anterior.
    /// </summary>
    [Fact]
    public async Task La_tecla_e_durante_una_recarga_espera_y_edita_los_datos_recien_cargados()
    {
        var a = Guid.NewGuid(); var b = Guid.NewGuid();
        var workspace = RegistrarWorkspace();
        var m = Registrar(new MediadorFalso());
        m.Detalles[a] = Detalle(a, "Furgoneta de obra");
        var cut = RenderizarPanel(a);
        cut.Find(".workspace-titulo-entidad").TextContent.Trim().Should().Be("Furgoneta de obra", "control positivo: primera visita cargada");
        var segundaCargaDeA = new TaskCompletionSource();
        m.Retener = x => x is ObtenerVehiculoPorIdQuery q ? (q.Id == a ? segundaCargaDeA.Task : new TaskCompletionSource().Task) : null;
        cut.Render(p => p.Add(x => x.EntidadId, b));
        cut.Render(p => p.Add(x => x.EntidadId, a));

        await cut.InvokeAsync(() => workspace.AbrirEnEdicionAsync(EntidadWorkspace.Vehiculo, a, "Furgoneta de obra"));

        EnEdicion(cut).Should().BeFalse("la recarga sigue en curso");

        m.Detalles[a] = Detalle(a, "Furgoneta nueva");
        await cut.InvokeAsync(segundaCargaDeA.SetResult);

        cut.WaitForAssertion(() => EnEdicion(cut).Should().BeTrue("la petición se atiende al terminar la carga"));
        cut.FindAll("input").Select(i => i.GetAttribute("value")).Should().Contain("Furgoneta nueva").And.NotContain("Furgoneta de obra");
    }
}
