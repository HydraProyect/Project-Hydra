using Bunit;
using CaeManager.Web.Components.DesignSystem;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// S12 (lote 1): lo que el kit <c>ModalFormulario</c> garantiza por construcción y una pantalla ya no puede escribir mal:
/// «Cancelar» y la X preguntan igual (D-05), un solo primario con su estado de guardado (D-30), el aviso del formulario fuera
/// del cuerpo desplazable (D-20), el foco al primer error tras guardar (D-20) y el aviso de navegación dentro (D-05).
/// </summary>
public class ModalFormularioTests : BunitContext
{
    private const string Pregunta = "¿Descartar cambios?";

    private bool _visible = true;
    private bool _hayCambios;
    private bool _guardando;
    private string? _mensaje;
    private bool _erroresDeCampo;
    private int _guardados;
    private Func<Task> _alGuardar;

    public ModalFormularioTests()
    {
        _alGuardar = () => { _guardados++; return Task.CompletedTask; };
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLocalization();
    }

    private IRenderedComponent<ModalFormulario> Renderizar(Action<ComponentParameterCollectionBuilder<ModalFormulario>>? extra = null) =>
        Render<ModalFormulario>(p =>
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

    private static bool Preguntando(IRenderedComponent<ModalFormulario> cut) =>
        cut.FindAll("h2").Any(h => h.TextContent.Trim() == Pregunta);

    [Fact]
    public void Pinta_cabecera_cuerpo_y_pie_con_un_solo_primario()
    {
        var cut = Renderizar();

        cut.Find("h2").TextContent.Trim().Should().Be("Nuevo trabajador");
        cut.FindAll(".modal-cuerpo input").Should().HaveCount(2);
        var botones = cut.FindAll(".modal-pie button");
        botones.Select(b => b.TextContent.Trim()).Should().Equal("Cancelar", "Guardar");
        cut.FindAll(".modal-pie .boton-primario").Should().ContainSingle().Which.TextContent.Trim().Should().Be("Guardar");
        cut.FindAll(".modal-pie .boton-secundario").Should().ContainSingle().Which.TextContent.Trim().Should().Be("Cancelar");
    }

    [Fact]
    public async Task Cancelar_sin_cambios_cierra()
    {
        var cut = Renderizar();

        await cut.Find(".modal-pie .boton-secundario").ClickAsync(new MouseEventArgs());

        _visible.Should().BeFalse();
        Preguntando(cut).Should().BeFalse();
    }

    [Fact]
    public async Task Cancelar_con_cambios_pregunta_igual_que_la_X_y_no_cierra()
    {
        _hayCambios = true;
        var cut = Renderizar();

        await cut.Find(".modal-pie .boton-secundario").ClickAsync(new MouseEventArgs());

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

        await cut.Find(".modal-cerrar").ClickAsync(new MouseEventArgs());

        _visible.Should().BeTrue();
        Preguntando(cut).Should().BeTrue();
    }

    [Fact]
    public void Guardando_deshabilita_Cancelar_y_pone_el_spinner_en_Guardar()
    {
        _guardando = true;
        var cut = Renderizar();

        cut.Find(".modal-pie .boton-secundario").HasAttribute("disabled").Should().BeTrue();
        var guardar = cut.Find(".modal-pie .boton-primario");
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

        await cut.Find(".modal-superposicion").ClickAsync(new MouseEventArgs());

        _visible.Should().BeTrue("un clic fuera no puede tirar lo escrito mientras se guarda o hay un error a la vista");
    }

    [Fact]
    public async Task Sin_guardar_ni_errores_un_clic_fuera_cierra()
    {
        var cut = Renderizar();

        await cut.Find(".modal-superposicion").ClickAsync(new MouseEventArgs());

        _visible.Should().BeFalse();
    }

    [Fact]
    public void El_aviso_del_formulario_va_fuera_del_cuerpo_desplazable_y_con_rol_alert()
    {
        _mensaje = "Selecciona una empresa.";
        var cut = Renderizar();

        var aviso = cut.Find(".modal-aviso .alerta-formulario");
        aviso.GetAttribute("role").Should().Be("alert");
        aviso.TextContent.Trim().Should().Be("Selecciona una empresa.");
        cut.FindAll(".modal-cuerpo .alerta-formulario").Should().BeEmpty(
            "dentro del cuerpo se iría con el desplazamiento y no se vería (D-20)");
    }

    [Fact]
    public void Sin_mensaje_no_hay_aviso()
    {
        var cut = Renderizar();

        cut.FindAll(".modal-aviso").Should().BeEmpty();
        cut.FindAll(".alerta-formulario").Should().BeEmpty();
    }

    [Fact]
    public async Task Guardar_invoca_el_manejador_de_la_pantalla()
    {
        var cut = Renderizar();

        await cut.Find(".modal-pie .boton-primario").ClickAsync(new MouseEventArgs());

        _guardados.Should().Be(1);
    }

    [Fact]
    public async Task Tras_guardar_con_errores_de_campo_el_foco_va_al_primero()
    {
        _erroresDeCampo = true;
        var modulo = JSInterop.SetupModule("./js/formulario-foco.js");
        modulo.SetupVoid("enfocarPrimerError", _ => true).SetVoidResult();
        var cut = Renderizar();

        await cut.Find(".modal-pie .boton-primario").ClickAsync(new MouseEventArgs());

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

        var guardar = cut.Find(".modal-pie .boton-primario");
        guardar.HasAttribute("disabled").Should().BeTrue();
        guardar.GetAttribute("title").Should().Be("Espera a que termine la subida del archivo.");
    }

    [Fact]
    public void Sin_motivo_Guardar_esta_habilitado_y_sin_title()
    {
        var cut = Renderizar();

        var guardar = cut.Find(".modal-pie .boton-primario");
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

        cut.Find(".modal-pie fieldset").HasAttribute("disabled").Should().Be(guardando,
            "un fieldset disabled deshabilita a todos sus botones: la pantalla no puede olvidarlo");
    }

    [Fact]
    public async Task La_pregunta_de_salida_queda_encima_de_los_dialogos_que_abre_el_formulario()
    {
        // Mismo z-index: el último del DOM queda encima. Un aviso pintado antes de los diálogos deja la pregunta detrás de ellos y
        // la salida se bloquea sin sus botones (el modal de «vigencia anterior» de PlataformaTab).
        _hayCambios = true;
        var cut = Renderizar(p => p.Add(x => x.Dialogos, (RenderFragment)(b =>
            b.AddMarkupContent(0, "<div class=\"modal-contenido\"><h2>Vigencia anterior</h2></div>"))));
        var navegacion = Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>();

        await cut.InvokeAsync(() => navegacion.NavigateTo("/trabajadores"));

        var dialogos = cut.FindAll(".modal-contenido");
        dialogos.Should().HaveCount(3, "el modal del formulario, el diálogo que abre y la pregunta de salida conviven");
        dialogos[1].TextContent.Should().Contain("Vigencia anterior", "los diálogos del formulario van después del modal y antes de la pregunta");
        dialogos.Last().TextContent.Should().Contain("¿Salir sin guardar?", "la pregunta tiene que ser la última del DOM para quedar encima");
    }

    [Fact]
    public async Task Un_render_ajeno_al_guardado_con_errores_de_campo_visibles_no_roba_el_foco()
    {
        _erroresDeCampo = true;
        var modulo = JSInterop.SetupModule("./js/formulario-foco.js");
        var cut = Renderizar();

        // Un error que sale al perder el foco de un campo (validación en línea) o cualquier otro render: no hubo «Guardar».
        await cut.InvokeAsync(() => cut.Render(p => p.Add(x => x.Titulo, "Otro título")));
        cut.Render(p => p.Add(x => x.MensajeError, "Otro error"));

        modulo.Invocations.Should().BeEmpty("el foco al primer error solo se mueve al terminar un guardado");
    }

    [Fact]
    public async Task Escape_con_cambios_pregunta_y_sin_cambios_cierra()
    {
        _hayCambios = true;
        var cut = Renderizar();
        await cut.Find(".modal-contenido").KeyDownAsync(new KeyboardEventArgs { Key = "Escape" });
        _visible.Should().BeTrue();
        Preguntando(cut).Should().BeTrue();

        _hayCambios = false;
        await cut.FindAll("button").Single(b => b.TextContent.Trim() == "Seguir editando").ClickAsync(new MouseEventArgs());
        await cut.Find(".modal-contenido").KeyDownAsync(new KeyboardEventArgs { Key = "Escape" });
        _visible.Should().BeFalse();
    }

    [Fact]
    public async Task Tras_guardar_sin_errores_de_campo_no_se_mueve_el_foco()
    {
        var modulo = JSInterop.SetupModule("./js/formulario-foco.js");
        var cut = Renderizar();

        await cut.Find(".modal-pie .boton-primario").ClickAsync(new MouseEventArgs());

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
    public async Task Salir_y_descartar_cierra_el_modal_y_avisa_a_la_pantalla()
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

        cut.FindAll(".modal-pie button").Select(b => b.TextContent.Trim()).Should().Equal("Cancelar", "Añadir otro", "Guardar");
    }

    // ---- v2 (S12, lote 2b), T7: el cuerpo que aún no está listo.

    private static RenderFragment FragmentoVacio(string titulo) => b =>
    {
        b.OpenComponent<EstadoVacio>(0);
        b.AddComponentParameter(1, nameof(CaeManager.Web.Components.DesignSystem.EstadoVacio.Titulo), titulo);
        b.CloseComponent();
    };

    [Fact]
    public void Cargando_pinta_el_esqueleto_en_lugar_de_los_campos_y_deshabilita_Guardar_con_motivo()
    {
        var cut = Renderizar(p => p.Add(x => x.Cargando, true).Add(x => x.FilasCargando, 4));

        cut.FindAll(".modal-cuerpo input").Should().BeEmpty("los campos no se pintan sobre datos que no han llegado");
        cut.FindAll(".modal-cuerpo .esqueleto-fila").Should().HaveCount(4);
        var guardar = cut.Find(".modal-pie .boton-primario");
        guardar.HasAttribute("disabled").Should().BeTrue("un primario habilitado sobre un cuerpo que no cargó guardaría un borrador nulo");
        guardar.GetAttribute("title").Should().Be("Cargando…", "un «Guardar» deshabilitado sin motivo no existe (D-02, D-06)");
    }

    [Fact]
    public async Task Cargando_deja_Cancelar_y_la_X_funcionando()
    {
        var cut = Renderizar(p => p.Add(x => x.Cargando, true));

        await cut.Find(".modal-pie .boton-secundario").ClickAsync(new MouseEventArgs());

        _visible.Should().BeFalse("quien abre un modal que tarda debe poder salir");
    }

    [Fact]
    public void Cargando_usa_el_motivo_de_la_pantalla_si_lo_da()
    {
        var cut = Renderizar(p => p.Add(x => x.Cargando, true).Add(x => x.MotivoCargando, "Preparando el borrador…"));

        cut.Find(".modal-pie .boton-primario").GetAttribute("title").Should().Be("Preparando el borrador…");
    }

    [Fact]
    public void Al_terminar_de_cargar_vuelven_los_campos_y_Guardar_se_habilita()
    {
        var cut = Renderizar(p => p.Add(x => x.Cargando, true));

        cut.Render(p => p.Add(x => x.Cargando, false));

        cut.FindAll(".modal-cuerpo input").Should().HaveCount(2);
        cut.Find(".modal-pie .boton-primario").HasAttribute("disabled").Should().BeFalse();
        cut.FindAll(".modal-cuerpo .esqueleto-fila").Should().BeEmpty();
    }

    [Fact]
    public void SinContenido_sustituye_los_campos_y_deshabilita_Guardar_con_el_motivo_de_la_pantalla()
    {
        var cut = Renderizar(p => p
            .Add(x => x.SinContenido, FragmentoVacio("No pudimos cargar el borrador"))
            .Add(x => x.MotivoGuardarDeshabilitado, "No hay nada que enviar"));

        cut.FindAll(".modal-cuerpo input").Should().BeEmpty();
        cut.Find(".modal-cuerpo h3").TextContent.Trim().Should().Be("No pudimos cargar el borrador");
        var guardar = cut.Find(".modal-pie .boton-primario");
        guardar.HasAttribute("disabled").Should().BeTrue();
        guardar.GetAttribute("title").Should().Be("No hay nada que enviar");
    }

    [Fact]
    public void SinContenido_sin_motivo_para_Guardar_no_se_pinta()
    {
        var accion = () => Renderizar(p => p.Add(x => x.SinContenido, FragmentoVacio("Vacío")));

        accion.Should().Throw<InvalidOperationException>().WithMessage("*MotivoGuardarDeshabilitado*",
            "un primario mudo no puede salir: el kit lo rechaza al pintarlo (D-02, D-06)");
    }

    [Fact]
    public void Cargando_gana_a_SinContenido_y_no_exige_motivo_propio()
    {
        var cut = Renderizar(p => p.Add(x => x.Cargando, true).Add(x => x.SinContenido, FragmentoVacio("Vacío")));

        cut.FindAll(".modal-cuerpo .esqueleto-fila").Should().NotBeEmpty();
        cut.FindAll(".modal-cuerpo h3").Should().BeEmpty();
        cut.Find(".modal-pie .boton-primario").GetAttribute("title").Should().Be("Cargando…");
    }

    // ------------------------------------------------------------- Lo propio del Modal: Ancho, Bloqueante, ranura de aviso

    [Theory]
    [InlineData(AnchoModal.Pequeno, "modal-contenido-pequeno")]
    [InlineData(AnchoModal.Mediano, "modal-contenido-mediano")]
    [InlineData(AnchoModal.Grande, "modal-contenido-grande")]
    public void El_ancho_llega_al_modal(AnchoModal ancho, string clase)
    {
        var cut = Renderizar(p => p.Add(x => x.Ancho, ancho));

        cut.Find(".modal-contenido").ClassList.Should().Contain(clase);
    }

    [Fact]
    public void Sin_ancho_es_mediano_como_el_modal_historico()
    {
        Renderizar().Find(".modal-contenido").ClassList.Should().Contain("modal-contenido-mediano");
    }

    [Fact]
    public async Task Bloqueante_no_tiene_X_ni_Cancelar_ni_sale_con_Escape_ni_con_clic_fuera()
    {
        _hayCambios = true;
        var cut = Renderizar(p => p.Add(x => x.Bloqueante, true));

        cut.FindAll(".modal-cerrar").Should().BeEmpty("sin salida no hay X");
        cut.FindAll(".modal-pie button").Select(b => b.TextContent.Trim()).Should().Equal("Guardar"); // sin salida tampoco hay «Cancelar»
        cut.FindAll(".modal-pie .boton-primario").Should().ContainSingle();

        await cut.Find(".modal-contenido").KeyDownAsync(new KeyboardEventArgs { Key = "Escape" });
        await cut.Find(".modal-superposicion").ClickAsync(new MouseEventArgs());

        _visible.Should().BeTrue();
        Preguntando(cut).Should().BeFalse("un modal bloqueante no pregunta porque no se puede cerrar por fuera");
    }

    [Fact]
    public async Task Bloqueante_tampoco_se_cierra_con_Salir_y_descartar_de_la_navegacion_pero_avisa_a_la_pantalla()
    {
        _hayCambios = true;
        var avisos = 0;
        var cut = Renderizar(p => p.Add(x => x.Bloqueante, true).Add(x => x.AlDescartarPorNavegacion, EventCallback.Factory.Create(this, () => avisos++)));
        var navegacion = Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>();

        await cut.InvokeAsync(() => navegacion.NavigateTo("/trabajadores"));
        await cut.FindAll("button").Single(b => b.TextContent.Trim() == "Salir y descartar").ClickAsync(new MouseEventArgs());

        _visible.Should().BeTrue("un modal Bloqueante solo se sale guardando: la navegación no lo cierra por su cuenta");
        avisos.Should().Be(1, "la pantalla limpia su estado en AlDescartarPorNavegacion");
    }

    [Fact]
    public void Sin_Bloqueante_Cancelar_y_la_X_existen()
    {
        var cut = Renderizar();

        cut.FindAll(".modal-cerrar").Should().ContainSingle();
        cut.FindAll(".modal-pie button").Select(b => b.TextContent.Trim()).Should().Equal("Cancelar", "Guardar");
    }

    [Fact]
    public void La_ranura_de_aviso_va_fuera_del_cuerpo_y_convive_con_el_error_del_formulario()
    {
        _mensaje = "No se pudo guardar.";
        var cut = Renderizar(p => p.Add(x => x.Aviso, NotaFija("Afecta a 12 trabajadores.")));

        var aviso = cut.Find(".modal-aviso");
        aviso.QuerySelector(".alerta-formulario")!.TextContent.Trim().Should().Be("No se pudo guardar.");
        aviso.QuerySelector(".nota-fija")!.TextContent.Trim().Should().Be("Afecta a 12 trabajadores.");
        cut.FindAll(".modal-cuerpo .nota-fija").Should().BeEmpty("dentro del cuerpo se iría con el desplazamiento");
    }

    [Fact]
    public async Task Una_nota_fija_no_es_un_error_y_no_bloquea_el_cierre_con_clic_fuera()
    {
        var cut = Renderizar(p => p.Add(x => x.Aviso, NotaFija("Nota")));

        cut.FindAll(".modal-aviso .nota-fija").Should().ContainSingle();
        cut.FindAll(".modal-aviso .alerta-formulario").Should().BeEmpty("la nota no es un error");

        await cut.Find(".modal-superposicion").ClickAsync(new MouseEventArgs());

        _visible.Should().BeFalse("solo el error visible, errores de campo o guardar bloquean el cierre fuera");
    }

    [Fact]
    public void Sin_aviso_ni_error_no_hay_ranura()
    {
        Renderizar().FindAll(".modal-aviso").Should().BeEmpty();
    }

    [Fact]
    public void HayCambios_es_obligatorio_para_el_compilador()
    {
        var propiedad = typeof(ModalFormulario).GetProperty(nameof(ModalFormulario.HayCambios))!;

        propiedad.GetCustomAttributes(typeof(EditorRequiredAttribute), inherit: true).Should().NotBeEmpty(
            "sin [EditorRequired], un ModalFormulario sin guardián compila: es el agujero que el kit existe para cerrar");
    }

    [Fact]
    public void Cargando_sin_motivo_se_dice_en_la_cultura_activa()
    {
        var anterior = System.Globalization.CultureInfo.CurrentUICulture;
        try
        {
            System.Globalization.CultureInfo.CurrentUICulture = new System.Globalization.CultureInfo("ca-ES");
            var cut = Renderizar(p => p.Add(x => x.Cargando, true));

            cut.Find(".modal-pie .boton-primario").GetAttribute("title").Should().Be("Carregant…");
            cut.Find(".modal-cuerpo .esqueleto-lista").GetAttribute("aria-label").Should().Be("Carregant…");
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentUICulture = anterior;
        }
    }

    private static RenderFragment NotaFija(string texto) => b => b.AddMarkupContent(0, $"<p class=\"nota-fija\">{texto}</p>");
}
