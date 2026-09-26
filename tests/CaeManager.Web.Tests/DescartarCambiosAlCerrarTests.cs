using Bunit;
using CaeManager.Web.Components.DesignSystem;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// P1-E2b (decisión del propietario, 2026-09-26): cerrar un Drawer o un Modal con la X,
/// Escape o un clic en el fondo mientras su formulario tiene cambios sin guardar pregunta
/// «¿Descartar cambios?»; sin cambios, cierra directamente. Vive una sola vez en Drawer y
/// Modal: cada formulario solo les pasa su HayCambios.
/// </summary>
public class DescartarCambiosAlCerrarTests : BunitContext
{
    public enum Contenedor { Drawer, Modal }

    private const string Pregunta = "¿Descartar cambios?";

    public DescartarCambiosAlCerrarTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLocalization();
    }

    private bool _visible = true;
    private bool _hayCambios;

    private IRenderedComponent<IComponent> Renderizar(Contenedor contenedor) => contenedor switch
    {
        Contenedor.Drawer => Render<Drawer>(p => p
            .Add(x => x.Visible, _visible)
            .Add(x => x.VisibleChanged, v => _visible = v)
            .Add(x => x.Titulo, "Nueva empresa")
            .Add(x => x.HayCambios, () => _hayCambios)
            .AddChildContent("<p>Formulario</p>")),
        _ => Render<Modal>(p => p
            .Add(x => x.Visible, _visible)
            .Add(x => x.VisibleChanged, v => _visible = v)
            .Add(x => x.Titulo, "Guardar filtro")
            .Add(x => x.HayCambios, () => _hayCambios)
            .AddChildContent("<p>Formulario</p>")),
    };

    private static string Clase(Contenedor contenedor) => contenedor == Contenedor.Drawer ? "drawer" : "modal";

    /// <summary>Los tres gestos de cierre que no son una decisión explícita del formulario.</summary>
    public static TheoryData<Contenedor, string> Gestos() => new()
    {
        { Contenedor.Drawer, "x" }, { Contenedor.Drawer, "escape" }, { Contenedor.Drawer, "fondo" },
        { Contenedor.Modal, "x" }, { Contenedor.Modal, "escape" }, { Contenedor.Modal, "fondo" },
    };

    private static async Task CerrarConAsync(IRenderedComponent<IComponent> cut, Contenedor contenedor, string gesto)
    {
        var clase = Clase(contenedor);
        var panel = contenedor == Contenedor.Drawer ? ".drawer-panel" : ".modal-contenido";
        switch (gesto)
        {
            case "x":
                await cut.Find($".{clase}-cerrar").ClickAsync(new MouseEventArgs());
                break;
            case "escape":
                await cut.Find(panel).KeyDownAsync(new KeyboardEventArgs { Key = "Escape" });
                break;
            default:
                // El Drawer exige que el mousedown también empiece en el fondo (seleccionar texto no cierra).
                if (contenedor == Contenedor.Drawer)
                    await cut.Find($".{clase}-superposicion").MouseDownAsync(new MouseEventArgs());
                await cut.Find($".{clase}-superposicion").ClickAsync(new MouseEventArgs());
                break;
        }
    }

    private static bool Preguntando(IRenderedComponent<IComponent> cut) =>
        cut.FindAll("h2").Any(h => h.TextContent.Trim() == Pregunta);

    [Theory]
    [MemberData(nameof(Gestos))]
    public async Task Sin_cambios_cierra_directamente(Contenedor contenedor, string gesto)
    {
        var cut = Renderizar(contenedor);

        await CerrarConAsync(cut, contenedor, gesto);

        _visible.Should().BeFalse();
        Preguntando(cut).Should().BeFalse();
    }

    [Theory]
    [MemberData(nameof(Gestos))]
    public async Task Con_cambios_pregunta_y_seguir_editando_no_cierra(Contenedor contenedor, string gesto)
    {
        _hayCambios = true;
        var cut = Renderizar(contenedor);

        await CerrarConAsync(cut, contenedor, gesto);

        _visible.Should().BeTrue("con cambios sin guardar se pregunta antes de cerrar");
        Preguntando(cut).Should().BeTrue();
        await cut.FindAll("button").Single(b => b.TextContent.Trim() == "Seguir editando").ClickAsync(new MouseEventArgs());

        _visible.Should().BeTrue();
        Preguntando(cut).Should().BeFalse();
    }

    [Theory]
    [InlineData(Contenedor.Drawer)]
    [InlineData(Contenedor.Modal)]
    public async Task Con_cambios_descartar_cierra(Contenedor contenedor)
    {
        _hayCambios = true;
        var cut = Renderizar(contenedor);

        await CerrarConAsync(cut, contenedor, "x");
        await cut.FindAll("button").Single(b => b.TextContent.Trim() == "Descartar cambios").ClickAsync(new MouseEventArgs());

        _visible.Should().BeFalse();
    }

    /// <summary>Escape sobre la propia pregunta es «seguir editando», no un segundo cierre.</summary>
    [Theory]
    [InlineData(Contenedor.Drawer)]
    [InlineData(Contenedor.Modal)]
    public async Task Escape_en_la_pregunta_sigue_editando(Contenedor contenedor)
    {
        _hayCambios = true;
        var cut = Renderizar(contenedor);
        await CerrarConAsync(cut, contenedor, "x");

        await cut.FindAll(".modal-contenido").Last().KeyDownAsync(new KeyboardEventArgs { Key = "Escape" });

        _visible.Should().BeTrue();
        Preguntando(cut).Should().BeFalse();
    }

    /// <summary>Sin HayCambios (quien aún no lo pasa) el cierre es el de siempre.</summary>
    [Fact]
    public async Task Sin_HayCambios_la_X_cierra_como_siempre()
    {
        var cut = Render<Drawer>(p => p
            .Add(x => x.Visible, true)
            .Add(x => x.VisibleChanged, v => _visible = v)
            .Add(x => x.Titulo, "Nueva empresa"));

        await cut.Find(".drawer-cerrar").ClickAsync(new MouseEventArgs());

        _visible.Should().BeFalse();
    }

    /// <summary>Cerrado desde fuera mientras preguntaba: al reabrirlo no vuelve a aparecer la pregunta.</summary>
    [Fact]
    public async Task Cerrado_desde_fuera_mientras_pregunta_no_la_arrastra_al_reabrir()
    {
        _hayCambios = true;
        var cut = Render<Drawer>(p => p
            .Add(x => x.Visible, true)
            .Add(x => x.VisibleChanged, v => _visible = v)
            .Add(x => x.Titulo, "Nueva empresa")
            .Add(x => x.HayCambios, () => _hayCambios));
        await cut.Find(".drawer-cerrar").ClickAsync(new MouseEventArgs());

        cut.Render(p => p.Add(x => x.Visible, false));
        cut.Render(p => p.Add(x => x.Visible, true));

        Preguntando(cut).Should().BeFalse();
    }

    /// <summary>Un Modal Bloqueante sigue sin cerrarse con Escape ni el fondo aunque haya cambios: tampoco pregunta.</summary>
    [Fact]
    public async Task Bloqueante_con_cambios_ni_cierra_ni_pregunta()
    {
        _hayCambios = true;
        var cut = Render<Modal>(p => p
            .Add(x => x.Visible, true)
            .Add(x => x.VisibleChanged, v => _visible = v)
            .Add(x => x.Titulo, "Paso obligatorio")
            .Add(x => x.Bloqueante, true)
            .Add(x => x.HayCambios, () => _hayCambios));

        await cut.Find(".modal-contenido").KeyDownAsync(new KeyboardEventArgs { Key = "Escape" });
        await cut.Find(".modal-superposicion").ClickAsync(new MouseEventArgs());

        _visible.Should().BeTrue();
        Preguntando(cut).Should().BeFalse();
    }

    /// <summary>
    /// Con CerrarAlHacerClicFuera=false (p. ej. un guardado en curso) Escape y el fondo no
    /// cierran; la X sí se ofrece, y con cambios pregunta en vez de tirarlos.
    /// </summary>
    [Fact]
    public async Task Sin_cierre_por_fuera_Escape_no_hace_nada_y_la_X_pregunta()
    {
        _hayCambios = true;
        var cut = Render<Drawer>(p => p
            .Add(x => x.Visible, true)
            .Add(x => x.VisibleChanged, v => _visible = v)
            .Add(x => x.Titulo, "Nueva empresa")
            .Add(x => x.CerrarAlHacerClicFuera, false)
            .Add(x => x.HayCambios, () => _hayCambios));

        await cut.Find(".drawer-panel").KeyDownAsync(new KeyboardEventArgs { Key = "Escape" });
        Preguntando(cut).Should().BeFalse();

        await cut.Find(".drawer-cerrar").ClickAsync(new MouseEventArgs());
        Preguntando(cut).Should().BeTrue();
        _visible.Should().BeTrue();
    }
}
