using Bunit;
using CaeManager.Web.Components.DesignSystem;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// S12 (lote 2b), T10: «un solo guardián de navegación por ámbito». Un <c>DrawerFormulario</c> lleva su propio
/// <c>AvisoCambiosSinGuardar</c> y una pantalla puede llevar además el suyo de página (el que cubría el drawer antes del kit, o
/// un modal, o una edición en línea). Con dos NavigationLock con cambios, cada uno preguntaba por su cuenta y quien editaba
/// contestaba dos veces. Estos casos fijan lo que el aviso garantiza por construcción, sea cual sea el cableado de la pantalla:
/// una navegación (o un cambio de pestaña) se pregunta una vez; «Salir y descartar» descarta a todos los que tenían cambios y
/// la navegación llega sin una segunda pregunta; «Seguir editando» no descarta a nadie.
/// </summary>
public class AvisoCambiosSinGuardarUnaPreguntaTests : BunitContext
{
    private const string Destino = "/trabajadores";

    private bool _kitHayCambios = true;
    private bool _paginaHayCambios = true;
    private int _descartesKit;
    private int _descartesPagina;
    private bool _visible = true;

    public AvisoCambiosSinGuardarUnaPreguntaTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLocalization();
    }

    private RenderFragment Kit => b =>
    {
        b.OpenComponent<DrawerFormulario>(0);
        b.AddAttribute(1, nameof(DrawerFormulario.Visible), _visible);
        b.AddAttribute(2, nameof(DrawerFormulario.VisibleChanged), EventCallback.Factory.Create<bool>(this, v => _visible = v));
        b.AddAttribute(3, nameof(DrawerFormulario.Titulo), "Nuevo");
        b.AddAttribute(4, nameof(DrawerFormulario.HayCambios), (Func<bool>)(() => _kitHayCambios));
        b.AddAttribute(5, nameof(DrawerFormulario.TextoGuardar), "Guardar");
        b.AddAttribute(6, nameof(DrawerFormulario.TextoCancelar), "Cancelar");
        b.AddAttribute(7, nameof(DrawerFormulario.AlDescartarPorNavegacion), EventCallback.Factory.Create(this, () => _descartesKit++));
        b.CloseComponent();
    };

    private RenderFragment AvisoDePagina => b =>
    {
        b.OpenComponent<AvisoCambiosSinGuardar>(0);
        b.AddAttribute(1, nameof(AvisoCambiosSinGuardar.HayCambios), (Func<bool>)(() => _paginaHayCambios));
        b.AddAttribute(2, nameof(AvisoCambiosSinGuardar.AlDescartar), EventCallback.Factory.Create(this, () => _descartesPagina++));
        b.CloseComponent();
    };

    private IRenderedComponent<IComponent> KitYAvisoDePagina(bool avisoDePaginaPrimero = false) =>
        Render(b =>
        {
            if (avisoDePaginaPrimero)
            {
                b.AddContent(0, AvisoDePagina);
                b.AddContent(1, Kit);
            }
            else
            {
                b.AddContent(2, Kit);
                b.AddContent(3, AvisoDePagina);
            }
        });

    private static int Preguntas(IRenderedComponent<IComponent> cut) =>
        cut.FindAll(".modal-pie button").Count(b => b.TextContent.Trim() == "Salir y descartar");

    private static Task Pulsar(IRenderedComponent<IComponent> cut, string texto) =>
        cut.FindAll(".modal-pie button").Single(b => b.TextContent.Trim() == texto).ClickAsync(new MouseEventArgs());

    private async Task IntentarSalirAsync(IRenderedComponent<IComponent> cut)
    {
        var navegacion = Services.GetRequiredService<NavigationManager>();
        await cut.InvokeAsync(() => navegacion.NavigateTo(Destino));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Con_cambios_en_el_kit_y_en_el_aviso_de_pagina_pregunta_una_sola_vez(bool avisoDePaginaPrimero)
    {
        var cut = KitYAvisoDePagina(avisoDePaginaPrimero);
        var origen = Services.GetRequiredService<NavigationManager>().Uri;

        await IntentarSalirAsync(cut);

        Services.GetRequiredService<NavigationManager>().Uri.Should().Be(origen, "con cambios la navegación se detiene");
        Preguntas(cut).Should().Be(1, "una navegación, una pregunta, sea cual sea el orden en que se montaron los avisos");
        cut.FindAll(".modal-pie").Should().ContainSingle();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Salir_y_descartar_descarta_a_los_dos_y_llega_sin_segunda_pregunta(bool avisoDePaginaPrimero)
    {
        var cut = KitYAvisoDePagina(avisoDePaginaPrimero);
        // Peor caso: ninguno de los dos AlDescartar limpia lo que su HayCambios mira, así que solo un pase único evita la segunda pregunta.

        await IntentarSalirAsync(cut);
        await Pulsar(cut, "Salir y descartar");

        Services.GetRequiredService<NavigationManager>().Uri.Should().EndWith(Destino);
        _descartesKit.Should().Be(1, "el kit descarta lo suyo");
        _descartesPagina.Should().Be(1, "el aviso de página descarta lo suyo");
        Preguntas(cut).Should().Be(0, "la navegación confirmada no vuelve a preguntar");
    }

    [Fact]
    public async Task Seguir_editando_no_descarta_a_nadie_y_el_siguiente_intento_pregunta_una_vez_otra_vez()
    {
        var cut = KitYAvisoDePagina();
        var navegacion = Services.GetRequiredService<NavigationManager>();
        var origen = navegacion.Uri;

        await IntentarSalirAsync(cut);
        await Pulsar(cut, "Seguir editando");

        navegacion.Uri.Should().Be(origen);
        (_descartesKit + _descartesPagina).Should().Be(0);
        Preguntas(cut).Should().Be(0);

        await IntentarSalirAsync(cut);

        Preguntas(cut).Should().Be(1, "lo reclamado en una navegación no se arrastra a la siguiente");
        await Pulsar(cut, "Salir y descartar");
        navegacion.Uri.Should().EndWith(Destino);
        (_descartesKit, _descartesPagina).Should().Be((1, 1), "tras seguir editando no quedó ningún adherido de la pregunta anterior");
    }

    [Fact]
    public async Task Si_solo_tiene_cambios_uno_pregunta_y_el_otro_no_descarta()
    {
        _paginaHayCambios = false;
        var cut = KitYAvisoDePagina();

        await IntentarSalirAsync(cut);

        Preguntas(cut).Should().Be(1);
        await Pulsar(cut, "Salir y descartar");

        Services.GetRequiredService<NavigationManager>().Uri.Should().EndWith(Destino);
        _descartesKit.Should().Be(1);
        _descartesPagina.Should().Be(0, "sin cambios no hay nada que descartar: sus efectos no se disparan");
    }

    [Fact]
    public async Task Sin_cambios_en_ninguno_no_pregunta()
    {
        _kitHayCambios = false;
        _paginaHayCambios = false;
        var cut = KitYAvisoDePagina();

        await IntentarSalirAsync(cut);

        Services.GetRequiredService<NavigationManager>().Uri.Should().EndWith(Destino);
        Preguntas(cut).Should().Be(0);
    }

    /// <summary>Aviso de página que se puede desmontar sin tocar el resto: <see cref="Mostrar"/> a false lo retira.</summary>
    private sealed class AvisoQueSeDesmonta : ComponentBase
    {
        [Parameter] public bool Mostrar { get; set; }
        [Parameter] public Func<bool> HayCambios { get; set; } = () => false;
        [Parameter] public EventCallback AlDescartar { get; set; }

        protected override void BuildRenderTree(RenderTreeBuilder b)
        {
            if (!Mostrar) return;
            b.OpenComponent<AvisoCambiosSinGuardar>(0);
            b.AddAttribute(1, nameof(AvisoCambiosSinGuardar.HayCambios), HayCambios);
            b.AddAttribute(2, nameof(AvisoCambiosSinGuardar.AlDescartar), AlDescartar);
            b.CloseComponent();
        }
    }

    [Fact]
    public async Task Un_aviso_desmontado_con_cambios_no_se_adhiere_ni_descarta()
    {
        var host = Render<AvisoQueSeDesmonta>(p => p
            .Add(x => x.Mostrar, true)
            .Add(x => x.HayCambios, () => true)
            .Add(x => x.AlDescartar, EventCallback.Factory.Create(this, () => _descartesPagina++)));
        var cut = Render(b => b.AddContent(0, Kit));
        host.Render(p => p.Add(x => x.Mostrar, false));

        await IntentarSalirAsync(cut);
        await Pulsar(cut, "Salir y descartar");

        Services.GetRequiredService<NavigationManager>().Uri.Should().EndWith(Destino);
        _descartesKit.Should().Be(1);
        _descartesPagina.Should().Be(0, "un aviso que ya no está montado no tiene nada que descartar: su pantalla se fue");
    }

    [Fact]
    public async Task Un_aviso_de_otro_circuito_no_se_adhiere()
    {
        // Cada circuito tiene su NavigationManager: el registro de avisos montados no mezcla usuarios.
        var descartesAjenos = 0;
        using var otroCircuito = new BunitContext();
        otroCircuito.JSInterop.Mode = JSRuntimeMode.Loose;
        otroCircuito.Services.AddLocalization();
        otroCircuito.Render(b =>
        {
            b.OpenComponent<AvisoCambiosSinGuardar>(0);
            b.AddAttribute(1, nameof(AvisoCambiosSinGuardar.HayCambios), (Func<bool>)(() => true));
            b.AddAttribute(2, nameof(AvisoCambiosSinGuardar.AlDescartar), EventCallback.Factory.Create(this, () => descartesAjenos++));
            b.CloseComponent();
        });
        var cut = KitYAvisoDePagina();

        await IntentarSalirAsync(cut);
        await Pulsar(cut, "Salir y descartar");

        descartesAjenos.Should().Be(0, "lo que escribe otra persona en otra sesión no se descarta al salir de esta");
        (_descartesKit, _descartesPagina).Should().Be((1, 1));
    }

    // ---- Cambiar de pestaña o de ficha: el mismo criterio por ámbito, no por aviso.

    private IRenderedComponent<IComponent> DosAvisosEnUnAmbito(AmbitoCambiosSinGuardar ambito) =>
        Render(b =>
        {
            b.OpenComponent<CascadingValue<AmbitoCambiosSinGuardar>>(0);
            b.AddAttribute(1, "Value", ambito);
            b.AddAttribute(2, "IsFixed", true);
            b.AddAttribute(3, "ChildContent", (RenderFragment)(inner =>
            {
                inner.AddContent(0, Kit);
                inner.AddContent(1, AvisoDePagina);
            }));
            b.CloseComponent();
        });

    [Fact]
    public async Task El_ambito_pregunta_una_vez_y_al_descartar_descartan_los_dos()
    {
        var ambito = new AmbitoCambiosSinGuardar();
        var cut = DosAvisosEnUnAmbito(ambito);

        var confirmacion = cut.InvokeAsync(() => ambito.ConfirmarAbandonoAsync());

        Preguntas(cut).Should().Be(1, "cambiar de pestaña con dos avisos con cambios pregunta una vez, no dos");
        confirmacion.IsCompleted.Should().BeFalse();
        await Pulsar(cut, "Salir y descartar");

        (await confirmacion).Should().BeTrue();
        (_descartesKit, _descartesPagina).Should().Be((1, 1));
        Preguntas(cut).Should().Be(0);
    }

    [Fact]
    public async Task El_ambito_con_seguir_editando_no_cambia_y_no_descarta()
    {
        var ambito = new AmbitoCambiosSinGuardar();
        var cut = DosAvisosEnUnAmbito(ambito);

        var confirmacion = cut.InvokeAsync(() => ambito.ConfirmarAbandonoAsync());
        await Pulsar(cut, "Seguir editando");

        (await confirmacion).Should().BeFalse();
        (_descartesKit + _descartesPagina).Should().Be(0);
    }

    [Fact]
    public async Task El_ambito_sin_cambios_deja_pasar_sin_preguntar()
    {
        _kitHayCambios = false;
        _paginaHayCambios = false;
        var ambito = new AmbitoCambiosSinGuardar();
        var cut = DosAvisosEnUnAmbito(ambito);

        (await cut.InvokeAsync(() => ambito.ConfirmarAbandonoAsync())).Should().BeTrue();

        Preguntas(cut).Should().Be(0);
    }
}
