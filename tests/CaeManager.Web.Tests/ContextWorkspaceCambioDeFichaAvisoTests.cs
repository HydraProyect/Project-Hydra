using Bunit;
using CaeManager.Application.Common;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Components.Workspace;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CaeManager.Web.Tests;

/// <summary>
/// P1-E2b (lote 1, fichas 360): abrir otra ficha, volver, el breadcrumb o cerrar el panel
/// cambian la pila del Context Workspace ANTES de que cambie la URL, así que la ficha se
/// desmonta antes de que su NavigationLock vea nada. <see cref="ContextWorkspaceService"/>
/// pregunta primero a los avisos de la ficha visible (ámbito en cascada con nombre).
///
/// El panel real se sustituye por uno que solo pinta su aviso, con cambios o sin ellos a
/// voluntad del test; «Salir y descartar» no los limpia, para ver que la sincronización de
/// <c>?ctx=</c> que acompaña al cambio ya confirmado no vuelve a preguntar por sí misma.
/// </summary>
public class ContextWorkspaceCambioDeFichaAvisoTests : BunitContext
{
    private static readonly Guid FichaA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid FichaB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    private readonly EstadoPanel _estado = new();
    private readonly ContextWorkspaceService _workspace = new();

    public ContextWorkspaceCambioDeFichaAvisoTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        Services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        Services.AddSingleton<IAlertaOperativa>(new AlertaOperativaMuda());
        Services.AddLocalization();
        Services.AddSingleton(_workspace);
        Services.AddSingleton(new ToastService());
        ComponentFactories.Add(new PanelVehiculoFalso(_estado));
        SetRendererInfo(new RendererInfo("Server", isInteractive: true));
    }

    private sealed class AlertaOperativaMuda : IAlertaOperativa
    {
        public void Emitir(string mensaje, NivelAlertaOperativa nivel) { }
        public void CapturarExcepcion(Exception excepcion) { }
        public void DejarMigaDePan(string mensaje) { }
        public IDisposable IniciarAmbitoDeCaptura() => new Nada();
        private sealed class Nada : IDisposable { public void Dispose() { } }
    }

    private sealed class EstadoPanel
    {
        public bool HayCambios { get; set; }
    }

    private sealed class PanelVehiculoFalso(EstadoPanel estado) : IComponentFactory
    {
        public bool CanCreate(Type componentType) =>
            componentType == typeof(CaeManager.Web.Features.Vehiculos.Components.VehiculoWorkspacePanel);

        public IComponent Create(Type componentType) => new Panel { Estado = estado };

        private sealed class Panel : ComponentBase
        {
            public EstadoPanel Estado { get; init; } = default!;

            [Parameter(CaptureUnmatchedValues = true)] public Dictionary<string, object>? Resto { get; set; }

            protected override void BuildRenderTree(RenderTreeBuilder builder)
            {
                builder.OpenComponent<AvisoCambiosSinGuardar>(0);
                builder.AddAttribute(1, nameof(AvisoCambiosSinGuardar.HayCambios), (Func<bool>)(() => Estado.HayCambios));
                builder.CloseComponent();
            }
        }
    }

    private NavigationManager Navegacion => Services.GetRequiredService<NavigationManager>();

    /// <summary>La URL con los <c>:</c> de <c>?ctx=</c> sin escapar.</summary>
    private string UriLegible => Uri.UnescapeDataString(Navegacion.Uri);

    private async Task<IRenderedComponent<ContextWorkspace>> AbrirFichaAAsync(bool conCambios)
    {
        Navegacion.NavigateTo("/vehiculos");
        var cut = Render<ContextWorkspace>();
        await cut.InvokeAsync(() => _workspace.AbrirAsync(EntidadWorkspace.Vehiculo, FichaA, "Furgoneta A", "informacion"));
        UriLegible.Should().EndWith($"ctx=Vehiculo:{FichaA}:informacion");
        _estado.HayCambios = conCambios;
        return cut;
    }

    private static bool Pregunta(IRenderedComponent<ContextWorkspace> cut) =>
        cut.FindAll(".modal-pie button").Any(b => b.TextContent.Trim() == "Salir y descartar");

    [Fact]
    public async Task Abrir_otra_ficha_con_cambios_pregunta_y_seguir_editando_la_deja_como_estaba()
    {
        var cut = await AbrirFichaAAsync(conCambios: true);

        var abrir = cut.InvokeAsync(() => _workspace.AbrirAsync(EntidadWorkspace.Vehiculo, FichaB, "Furgoneta B", "informacion"));

        Pregunta(cut).Should().BeTrue("la ficha visible tiene cambios y se iba a desmontar");
        _workspace.FrameActual!.EntidadId.Should().Be(FichaA, "mientras pregunta, la pila no ha cambiado");

        await cut.PulsarEnElAvisoAsync("Seguir editando");
        await abrir;

        _workspace.FrameActual!.EntidadId.Should().Be(FichaA);
        _workspace.Pila.Should().ContainSingle();
        UriLegible.Should().EndWith($"ctx=Vehiculo:{FichaA}:informacion");
        Pregunta(cut).Should().BeFalse();
    }

    [Fact]
    public async Task Salir_y_descartar_abre_la_otra_ficha_y_la_sincronizacion_de_la_url_no_vuelve_a_preguntar()
    {
        var cut = await AbrirFichaAAsync(conCambios: true);

        var abrir = cut.InvokeAsync(() => _workspace.AbrirAsync(EntidadWorkspace.Vehiculo, FichaB, "Furgoneta B", "informacion"));
        await cut.PulsarEnElAvisoAsync("Salir y descartar");
        await abrir;

        _workspace.FrameActual!.EntidadId.Should().Be(FichaB);
        UriLegible.Should().EndWith($"ctx=Vehiculo:{FichaB}:informacion", "la URL sigue a la ficha abierta");
        Pregunta(cut).Should().BeFalse("el cambio ya estaba confirmado");
    }

    [Fact]
    public async Task Sin_cambios_abrir_otra_ficha_no_pregunta()
    {
        var cut = await AbrirFichaAAsync(conCambios: false);

        await cut.InvokeAsync(() => _workspace.AbrirAsync(EntidadWorkspace.Vehiculo, FichaB, "Furgoneta B", "informacion"));

        _workspace.FrameActual!.EntidadId.Should().Be(FichaB);
        UriLegible.Should().EndWith($"ctx=Vehiculo:{FichaB}:informacion");
        Pregunta(cut).Should().BeFalse();
    }

    [Fact]
    public async Task Cerrar_el_panel_con_cambios_pregunta()
    {
        var cut = await AbrirFichaAAsync(conCambios: true);

        var cerrar = cut.Find("button.workspace-cerrar").ClickAsync(new MouseEventArgs());

        Pregunta(cut).Should().BeTrue();
        _workspace.EstaAbierto.Should().BeTrue();

        await cut.PulsarEnElAvisoAsync("Salir y descartar");
        await cerrar;

        _workspace.EstaAbierto.Should().BeFalse();
        UriLegible.Should().NotContain("ctx=");
    }

    [Fact]
    public async Task Volver_a_la_ficha_anterior_con_cambios_pregunta()
    {
        var cut = await AbrirFichaAAsync(conCambios: false);
        await cut.InvokeAsync(() => _workspace.NavegarAAsync(EntidadWorkspace.Vehiculo, FichaB, "Furgoneta B", "informacion"));
        _estado.HayCambios = true;

        var volver = cut.InvokeAsync(() => _workspace.VolverAsync());

        Pregunta(cut).Should().BeTrue();
        await cut.PulsarEnElAvisoAsync("Seguir editando");
        await volver;

        _workspace.FrameActual!.EntidadId.Should().Be(FichaB);
        _workspace.Pila.Should().HaveCount(2);
    }

    [Fact]
    public async Task Cambiar_de_pestana_no_pregunta_a_un_aviso_fuera_de_las_pestanas()
    {
        var cut = await AbrirFichaAAsync(conCambios: true);

        await cut.InvokeAsync(() => _workspace.CambiarPestanaAsync("documentacion"));

        UriLegible.Should().EndWith($"ctx=Vehiculo:{FichaA}:documentacion");
        Pregunta(cut).Should().BeFalse("el estado del panel sobrevive al cambio de pestaña");
    }

    [Fact]
    public async Task Otra_ficha_por_la_url_pregunta_una_vez_y_el_estado_la_sigue_sin_volver_a_preguntar()
    {
        var cut = await AbrirFichaAAsync(conCambios: true);

        await cut.InvokeAsync(() => Navegacion.NavigateTo($"/vehiculos?ctx=Vehiculo:{FichaB}:informacion"));
        Pregunta(cut).Should().BeTrue("el NavigationLock del aviso ve la navegación a otra ficha");

        await cut.PulsarEnElAvisoAsync("Salir y descartar");

        UriLegible.Should().EndWith($"ctx=Vehiculo:{FichaB}:informacion");
        cut.WaitForAssertion(() => _workspace.FrameActual!.EntidadId.Should().Be(FichaB));
        Pregunta(cut).Should().BeFalse("seguir a la URL ya confirmada no es otra salida");
    }
}
