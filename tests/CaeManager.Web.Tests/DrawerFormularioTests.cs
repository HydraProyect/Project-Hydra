using Bunit;
using CaeManager.Web.Components.DesignSystem;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// S12 (lote 1): lo que el kit <c>DrawerFormulario</c> garantiza por construcción y una pantalla ya no puede escribir mal:
/// «Cancelar» y la X preguntan igual (D-05), un solo primario con su estado de guardado (D-30), el aviso del formulario fuera
/// del cuerpo desplazable (D-20), el foco al primer error tras guardar (D-20) y el aviso de navegación dentro (D-05).
/// </summary>
public class DrawerFormularioTests : BunitContext
{
    private const string Pregunta = "¿Descartar cambios?";

    private bool _visible = true;
    private bool _hayCambios;
    private bool _guardando;
    private string? _mensaje;
    private bool _erroresDeCampo;
    private int _guardados;
    private Func<Task> _alGuardar;

    public DrawerFormularioTests()
    {
        _alGuardar = () => { _guardados++; return Task.CompletedTask; };
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLocalization();
    }

    private IRenderedComponent<DrawerFormulario> Renderizar(Action<ComponentParameterCollectionBuilder<DrawerFormulario>>? extra = null) =>
        Render<DrawerFormulario>(p =>
        {
            p.Add(x => x.Visible, _visible)
             .Add(x => x.VisibleChanged, v => _visible = v)
             .Add(x => x.Titulo, "Nuevo trabajador")
             .Add(x => x.HayCambios, () => _hayCambios)
             .Add(x => x.Guardando, _guardando)
             .Add(x => x.MensajeError, _mensaje)
             .Add(x => x.HayErroresDeCampo, _erroresDeCampo)
             .Add(x => x.TextoGuardar, "Guardar")
             .Add(x => x.TextoCancelar, "Cancelar")
             .Add(x => x.OnGuardar, EventCallback.Factory.Create(this, () => _alGuardar()))
             .AddChildContent("<input class=\"campo-input\" /><input class=\"campo-input campo-input-error\" />");
            extra?.Invoke(p);
        });

    private static bool Preguntando(IRenderedComponent<DrawerFormulario> cut) =>
        cut.FindAll("h2").Any(h => h.TextContent.Trim() == Pregunta);

    [Fact]
    public void Pinta_cabecera_cuerpo_y_pie_con_un_solo_primario()
    {
        var cut = Renderizar();

        cut.Find("h2").TextContent.Trim().Should().Be("Nuevo trabajador");
        cut.FindAll(".drawer-cuerpo input").Should().HaveCount(2);
        var botones = cut.FindAll(".drawer-pie button");
        botones.Select(b => b.TextContent.Trim()).Should().Equal("Cancelar", "Guardar");
        cut.FindAll(".drawer-pie .boton-primario").Should().ContainSingle().Which.TextContent.Trim().Should().Be("Guardar");
        cut.FindAll(".drawer-pie .boton-secundario").Should().ContainSingle().Which.TextContent.Trim().Should().Be("Cancelar");
    }

    [Fact]
    public async Task Cancelar_sin_cambios_cierra()
    {
        var cut = Renderizar();

        await cut.Find(".drawer-pie .boton-secundario").ClickAsync(new MouseEventArgs());

        _visible.Should().BeFalse();
        Preguntando(cut).Should().BeFalse();
    }

    [Fact]
    public async Task Cancelar_con_cambios_pregunta_igual_que_la_X_y_no_cierra()
    {
        _hayCambios = true;
        var cut = Renderizar();

        await cut.Find(".drawer-pie .boton-secundario").ClickAsync(new MouseEventArgs());

        _visible.Should().BeTrue("la salida del pie no tira lo escrito sin preguntar (D-05)");
        Preguntando(cut).Should().BeTrue();

        await cut.FindAll("button").Single(b => b.TextContent.Trim() == "Descartar cambios").ClickAsync(new MouseEventArgs());
        _visible.Should().BeFalse();
    }

    [Fact]
    public async Task La_X_con_cambios_pregunta_con_el_mismo_guardian()
    {
        _hayCambios = true;
        var cut = Renderizar();

        await cut.Find(".drawer-cerrar").ClickAsync(new MouseEventArgs());

        _visible.Should().BeTrue();
        Preguntando(cut).Should().BeTrue();
    }

    [Fact]
    public void Guardando_deshabilita_Cancelar_y_pone_el_spinner_en_Guardar()
    {
        _guardando = true;
        var cut = Renderizar();

        cut.Find(".drawer-pie .boton-secundario").HasAttribute("disabled").Should().BeTrue();
        var guardar = cut.Find(".drawer-pie .boton-primario");
        guardar.HasAttribute("disabled").Should().BeTrue("Cargando deshabilita el botón: sin doble clic");
        guardar.QuerySelector(".boton-spinner").Should().NotBeNull();
    }

    [Theory]
    [InlineData(true, null, false)]
    [InlineData(false, "No se pudo guardar.", false)]
    [InlineData(false, null, true)]
    public async Task No_se_cierra_con_un_clic_fuera_mientras_se_guarda_o_hay_un_error_visible(bool guardando, string? mensaje, bool erroresDeCampo)
    {
        _guardando = guardando;
        _mensaje = mensaje;
        _erroresDeCampo = erroresDeCampo;
        var cut = Renderizar();

        await cut.Find(".drawer-superposicion").MouseDownAsync(new MouseEventArgs());
        await cut.Find(".drawer-superposicion").ClickAsync(new MouseEventArgs());

        _visible.Should().BeTrue("un clic fuera no puede tirar lo escrito mientras se guarda o hay un error a la vista");
    }

    [Fact]
    public async Task Sin_guardar_ni_errores_un_clic_fuera_cierra()
    {
        var cut = Renderizar();

        await cut.Find(".drawer-superposicion").MouseDownAsync(new MouseEventArgs());
        await cut.Find(".drawer-superposicion").ClickAsync(new MouseEventArgs());

        _visible.Should().BeFalse();
    }

    [Fact]
    public void El_aviso_del_formulario_va_fuera_del_cuerpo_desplazable_y_con_rol_alert()
    {
        _mensaje = "Selecciona una empresa.";
        var cut = Renderizar();

        var aviso = cut.Find(".drawer-aviso .alerta-formulario");
        aviso.GetAttribute("role").Should().Be("alert");
        aviso.TextContent.Trim().Should().Be("Selecciona una empresa.");
        cut.FindAll(".drawer-cuerpo .alerta-formulario").Should().BeEmpty(
            "dentro del cuerpo se iría con el desplazamiento y no se vería (D-20)");
    }

    [Fact]
    public void Sin_mensaje_no_hay_aviso()
    {
        var cut = Renderizar();

        cut.FindAll(".drawer-aviso").Should().BeEmpty();
        cut.FindAll(".alerta-formulario").Should().BeEmpty();
    }

    [Fact]
    public async Task Guardar_invoca_el_manejador_de_la_pantalla()
    {
        var cut = Renderizar();

        await cut.Find(".drawer-pie .boton-primario").ClickAsync(new MouseEventArgs());

        _guardados.Should().Be(1);
    }

    [Fact]
    public async Task Tras_guardar_con_errores_de_campo_el_foco_va_al_primero()
    {
        _erroresDeCampo = true;
        var modulo = JSInterop.SetupModule("./js/formulario-foco.js");
        modulo.SetupVoid("enfocarPrimerError", _ => true).SetVoidResult();
        var cut = Renderizar();

        await cut.Find(".drawer-pie .boton-primario").ClickAsync(new MouseEventArgs());

        cut.WaitForAssertion(() => modulo.VerifyInvoke("enfocarPrimerError"));
    }

    [Fact]
    public void Un_ciclo_de_guardado_de_una_accion_extra_que_deja_errores_de_campo_tambien_lleva_el_foco()
    {
        var modulo = JSInterop.SetupModule("./js/formulario-foco.js");
        modulo.SetupVoid("enfocarPrimerError", _ => true).SetVoidResult();
        _guardando = true;
        var cut = Renderizar();

        // «Añadir otro» no pasa por OnGuardar: la pantalla pone Guardando y, al terminar, deja errores de campo.
        cut.Render(p => p.Add(x => x.Guardando, false).Add(x => x.HayErroresDeCampo, true));

        cut.WaitForAssertion(() => modulo.VerifyInvoke("enfocarPrimerError"));
    }

    [Fact]
    public async Task Un_segundo_Guardar_con_el_primero_en_vuelo_no_invoca_otra_vez_a_la_pantalla()
    {
        var respuesta = new TaskCompletionSource();
        _alGuardar = () => { _guardados++; return respuesta.Task; };
        var cut = Renderizar();
        var guardar = cut.FindComponents<Boton>().Single(b => b.Find("button").TextContent.Trim() == "Guardar");

        var primero = cut.InvokeAsync(() => guardar.Instance.OnClick.InvokeAsync());
        var segundo = cut.InvokeAsync(() => guardar.Instance.OnClick.InvokeAsync());
        _guardados.Should().Be(1, "la guarda de doble clic vive en el kit y no depende de que la pantalla la recuerde");

        respuesta.SetResult();
        await Task.WhenAll(primero, segundo);
    }

    [Fact]
    public void Guardar_solo_se_deshabilita_con_un_motivo_que_sale_como_title()
    {
        var cut = Renderizar(p => p.Add(x => x.MotivoGuardarDeshabilitado, "Espera a que termine la subida del archivo."));

        var guardar = cut.Find(".drawer-pie .boton-primario");
        guardar.HasAttribute("disabled").Should().BeTrue();
        guardar.GetAttribute("title").Should().Be("Espera a que termine la subida del archivo.");
    }

    [Fact]
    public void Sin_motivo_Guardar_esta_habilitado_y_sin_title()
    {
        var cut = Renderizar();

        var guardar = cut.Find(".drawer-pie .boton-primario");
        guardar.HasAttribute("disabled").Should().BeFalse();
        guardar.HasAttribute("title").Should().BeFalse();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Las_acciones_extra_se_deshabilitan_solas_mientras_se_guarda(bool guardando)
    {
        _guardando = guardando;
        var cut = Renderizar(p => p.Add(x => x.PieExtra, (RenderFragment)(b =>
        {
            b.OpenComponent<Boton>(0);
            b.AddComponentParameter(1, nameof(Boton.Variante), VarianteBoton.Secundario);
            b.AddComponentParameter(2, nameof(Boton.ChildContent), (RenderFragment)(c => c.AddContent(0, "Añadir otro")));
            b.CloseComponent();
        })));

        cut.Find(".drawer-pie fieldset").HasAttribute("disabled").Should().Be(guardando,
            "un fieldset disabled deshabilita a todos sus botones: la pantalla no puede olvidarlo");
    }

    [Fact]
    public async Task Tras_guardar_sin_errores_de_campo_no_se_mueve_el_foco()
    {
        var modulo = JSInterop.SetupModule("./js/formulario-foco.js");
        var cut = Renderizar();

        await cut.Find(".drawer-pie .boton-primario").ClickAsync(new MouseEventArgs());

        modulo.Invocations.Should().BeEmpty();
    }

    [Fact]
    public async Task Salir_por_la_aplicacion_con_cambios_pregunta_y_sin_cambios_no()
    {
        var navegacion = Services.GetRequiredService<NavigationManager>();
        _hayCambios = true;
        var cut = Renderizar();

        await cut.SalirYComprobarQuePreguntaAsync(navegacion);

        _hayCambios = false;
        var otro = Renderizar();
        await otro.SalirYComprobarQueNoPreguntaAsync(navegacion, "sin cambios no hay nada que preguntar");
    }

    [Fact]
    public async Task Cerrado_no_pregunta_al_navegar_aunque_HayCambios_diga_true()
    {
        _visible = false;
        _hayCambios = true;
        var cut = Renderizar();
        var navegacion = Services.GetRequiredService<NavigationManager>();

        await cut.SalirYComprobarQueNoPreguntaAsync(navegacion, "el formulario cerrado ya no tiene nada que perder");
    }

    [Fact]
    public async Task Salir_y_descartar_cierra_el_drawer_y_avisa_a_la_pantalla()
    {
        var navegacion = Services.GetRequiredService<NavigationManager>();
        _hayCambios = true;
        var descartado = false;
        var cut = Renderizar(p => p.Add(x => x.AlDescartarPorNavegacion, () => descartado = true));
        await cut.SalirYComprobarQuePreguntaAsync(navegacion);

        await cut.FindAll("button").Single(b => b.TextContent.Trim() == "Salir y descartar").ClickAsync(new MouseEventArgs());

        _visible.Should().BeFalse();
        descartado.Should().BeTrue();
    }

    [Fact]
    public void Las_acciones_extra_van_entre_Cancelar_y_Guardar()
    {
        var cut = Renderizar(p => p.Add(x => x.PieExtra, (RenderFragment)(b =>
        {
            b.OpenComponent<Boton>(0);
            b.AddComponentParameter(1, nameof(Boton.Variante), VarianteBoton.Secundario);
            b.AddComponentParameter(2, nameof(Boton.ChildContent), (RenderFragment)(c => c.AddContent(0, "Añadir otro")));
            b.CloseComponent();
        })));

        cut.FindAll(".drawer-pie button").Select(b => b.TextContent.Trim()).Should().Equal("Cancelar", "Añadir otro", "Guardar");
    }
}
