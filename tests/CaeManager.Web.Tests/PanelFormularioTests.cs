using Bunit;
using CaeManager.Web.Components.DesignSystem;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// «Panel Formulario TALVEG»: la anatomía común del panel lateral y lo opcional que se le añade (subtítulo, ancho, modo pasos,
/// sección plegable, alta en línea del selector). Lo que ya hacía el kit (Cancelar/Guardar, guardián) lo fija
/// <c>DrawerFormularioTests</c>; aquí, que nada de eso cambia cuando no se usa lo nuevo y que lo nuevo hace lo que dice la maqueta.
/// </summary>
public class PanelFormularioTests : BunitContext
{
    private static readonly IReadOnlyList<PasoDefinicion> Tres =
        [new("basico", "Lo básico"), new("trabajo", "Trabajo"), new("contacto", "Contacto")];

    private string _paso = "basico";
    private readonly HashSet<string> _vistos = [];
    private int _guardados;
    private int _siguientes;
    private int _atras;
    private string? _seleccionado;
    private bool _visible = true;

    public PanelFormularioTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLocalization();
    }

    private IRenderedComponent<DrawerFormulario> Renderizar(Action<ComponentParameterCollectionBuilder<DrawerFormulario>>? extra = null) =>
        Render<DrawerFormulario>(p =>
        {
            p.Add(x => x.Visible, _visible)
             .Add(x => x.VisibleChanged, v => _visible = v)
             .Add(x => x.Titulo, "Nuevo trabajador")
             .Add(x => x.HayCambios, () => false)
             .Add(x => x.TextoGuardar, "Guardar")
             .Add(x => x.TextoCancelar, "Cancelar")
             .Add(x => x.OnGuardar, EventCallback.Factory.Create(this, () => _guardados++))
             .AddChildContent("<input class=\"campo-input\" />");
            extra?.Invoke(p);
        });

    private IRenderedComponent<DrawerFormulario> RenderizarPasos() =>
        Renderizar(p => p
            .Add(x => x.Pasos, Tres)
            .Add(x => x.PasoActual, _paso)
            .Add(x => x.PasosCompletados, _vistos)
            .Add(x => x.OnSiguiente, EventCallback.Factory.Create(this, () => _siguientes++))
            .Add(x => x.OnAtras, EventCallback.Factory.Create(this, () => _atras++))
            .Add(x => x.OnPasoSeleccionado, EventCallback.Factory.Create<string>(this, c => _seleccionado = c)));

    private static string[] Botones(IRenderedComponent<DrawerFormulario> cut) =>
        cut.FindAll(".drawer-pie button").Select(b => b.TextContent.Trim()).ToArray();

    // ------------------------------------------------------------ cabecera, ancho y pie sin lo nuevo

    [Fact]
    public void Sin_lo_nuevo_el_panel_es_el_de_siempre_con_cabecera_X_y_pie_Cancelar_Guardar()
    {
        var cut = Renderizar();

        cut.Find(".drawer-header h2").TextContent.Trim().Should().Be("Nuevo trabajador");
        cut.Find(".drawer-header button.drawer-cerrar").GetAttribute("aria-label").Should().Be("Cerrar");
        cut.FindAll(".drawer-subtitulo").Should().BeEmpty("sin Subtitulo no hay segunda línea");
        cut.FindAll(".drawer-pasos").Should().BeEmpty();
        cut.Find(".drawer-panel").ClassList.Should().NotContain("drawer-panel-ancho", "el ancho estándar es el por defecto");
        Botones(cut).Should().Equal("Cancelar", "Guardar");
        cut.FindAll(".formulario-drawer-accion").Should().BeEmpty("el pie sin pasos no lleva envoltorios");
        cut.FindAll(".drawer-pie .boton-primario").Should().ContainSingle().Which.TextContent.Trim().Should().Be("Guardar");
    }

    [Fact]
    public void El_subtitulo_sale_bajo_el_titulo_y_el_ancho_se_elige()
    {
        var cut = Renderizar(p => p.Add(x => x.Subtitulo, "Empresa: Montajes Levante S.L.").Add(x => x.Ancho, AnchoDrawer.Ancho));

        cut.Find(".drawer-titulos .drawer-subtitulo").TextContent.Trim().Should().Be("Empresa: Montajes Levante S.L.");
        cut.Find(".drawer-titulos h2").TextContent.Trim().Should().Be("Nuevo trabajador");
        cut.Find(".drawer-panel").ClassList.Should().Contain("drawer-panel-ancho");
    }

    // ------------------------------------------------------------ modo pasos

    [Fact]
    public async Task Primer_paso_ofrece_Cancelar_Guardar_y_terminar_y_Siguiente_con_un_solo_primario()
    {
        var cut = RenderizarPasos();

        Botones(cut).Should().Equal("Cancelar", "Guardar y terminar", "Siguiente");
        cut.FindAll(".drawer-pie .boton-primario").Should().ContainSingle().Which.TextContent.Trim().Should().Be("Siguiente");
        cut.Find(".drawer-pasos ol").GetAttribute("aria-label").Should().Be("Pasos del formulario");
        cut.FindAll(".drawer-pasos .indicador-pasos-item").Should().HaveCount(3);
        cut.Find(".drawer-pasos [aria-current='step']").TextContent.Should().Contain("Lo básico");

        await cut.FindAll(".drawer-pie button").Single(b => b.TextContent.Trim() == "Siguiente").ClickAsync(new MouseEventArgs());
        _siguientes.Should().Be(1);
        _guardados.Should().Be(0, "«Siguiente» no guarda: los pasos ordenan el formulario, no lo parten en transacciones");
    }

    [Fact]
    public async Task Paso_intermedio_ofrece_Atras_en_lugar_de_Cancelar_y_Guardar_y_terminar_guarda()
    {
        _paso = "trabajo";
        var cut = RenderizarPasos();

        Botones(cut).Should().Equal("Atrás", "Guardar y terminar", "Siguiente");

        await cut.FindAll(".drawer-pie button").Single(b => b.TextContent.Trim() == "Atrás").ClickAsync(new MouseEventArgs());
        _atras.Should().Be(1);
        _visible.Should().BeTrue("«Atrás» no cierra");

        await cut.FindAll(".drawer-pie button").Single(b => b.TextContent.Trim() == "Guardar y terminar").ClickAsync(new MouseEventArgs());
        _guardados.Should().Be(1, "«Guardar y terminar» es el mismo OnGuardar que «Guardar»");
    }

    [Fact]
    public async Task Ultimo_paso_ofrece_Atras_y_Guardar_como_unico_primario()
    {
        _paso = "contacto";
        var cut = RenderizarPasos();

        Botones(cut).Should().Equal("Atrás", "Guardar");
        cut.FindAll(".drawer-pie .boton-primario").Should().ContainSingle().Which.TextContent.Trim().Should().Be("Guardar");

        await cut.Find(".drawer-pie .boton-primario").ClickAsync(new MouseEventArgs());
        _guardados.Should().Be(1);
        _siguientes.Should().Be(0);
    }

    [Fact]
    public async Task Un_paso_completado_se_puede_revisitar_desde_el_indicador_y_uno_futuro_no()
    {
        _paso = "contacto";
        _vistos.Add("basico");
        _vistos.Add("trabajo");
        var cut = RenderizarPasos();

        var botones = cut.FindAll(".drawer-pasos button");
        botones.Select(b => b.TextContent.Trim()).Should().Equal("Lo básico", "Trabajo");

        await botones[0].ClickAsync(new MouseEventArgs());
        _seleccionado.Should().Be("basico");

        _paso = "basico";
        _vistos.Clear();
        cut = RenderizarPasos();
        cut.FindAll(".drawer-pasos button").Should().BeEmpty("sin pasos completados no hay a dónde volver");
    }

    [Fact]
    public void El_indicador_de_movil_resume_el_paso_con_una_barra_por_paso()
    {
        _paso = "trabajo";
        _vistos.Add("basico");
        var cut = RenderizarPasos();

        cut.Find(".indicador-pasos-movil-texto").TextContent.Trim().Should().Be("Paso 2 de 3 · Trabajo");
        cut.FindAll(".indicador-pasos-barra").Should().HaveCount(3);
        cut.FindAll(".indicador-pasos-barra-hecha").Should().ContainSingle();
    }

    [Fact]
    public void Con_un_solo_paso_no_hay_modo_pasos()
    {
        var cut = Renderizar(p => p.Add(x => x.Pasos, new List<PasoDefinicion> { new("a", "A") }).Add(x => x.PasoActual, "a"));

        cut.FindAll(".drawer-pasos").Should().BeEmpty();
        Botones(cut).Should().Equal("Cancelar", "Guardar");
    }

    // ------------------------------------------------------------ sección plegable

    [Fact]
    public async Task Seccion_plegable_plegada_enseña_el_resumen_y_se_abre_con_un_clic()
    {
        var cut = Render<SeccionPlegable>(p => p
            .Add(x => x.Titulo, "Datos básicos")
            .Add(x => x.Resumen, "Montajes Levante S.L. · B46218753")
            .AddChildContent("<input class=\"campo-input\" />"));

        cut.Find("button.seccion-plegable-cabecera").GetAttribute("aria-expanded").Should().Be("false");
        cut.Find(".seccion-plegable-resumen").TextContent.Trim().Should().Be("Montajes Levante S.L. · B46218753");
        cut.FindAll(".seccion-plegable-contenido input").Should().BeEmpty();

        await cut.Find("button.seccion-plegable-cabecera").ClickAsync(new MouseEventArgs());

        cut.Find("button.seccion-plegable-cabecera").GetAttribute("aria-expanded").Should().Be("true");
        cut.FindAll(".seccion-plegable-resumen").Should().BeEmpty("abierta, el resumen sobra");
        cut.FindAll(".seccion-plegable-contenido input").Should().ContainSingle();
    }

    [Theory]
    [InlineData(1, "1 error")]
    [InlineData(3, "3 errores")]
    public void Seccion_plegable_con_errores_se_abre_sola_y_se_marca(int errores, string pastilla)
    {
        var cut = Render<SeccionPlegable>(p => p
            .Add(x => x.Titulo, "Contactos")
            .Add(x => x.NumeroErrores, errores)
            .AddChildContent("<input class=\"campo-input\" />"));

        cut.Find("button.seccion-plegable-cabecera").GetAttribute("aria-expanded").Should().Be("true");
        cut.Find(".seccion-plegable").ClassList.Should().Contain("seccion-plegable-con-errores");
        cut.Find(".seccion-plegable-errores").TextContent.Trim().Should().Be(pastilla);
    }

    [Fact]
    public void Seccion_plegable_se_abre_cuando_aparecen_errores_despues()
    {
        var cut = Render<SeccionPlegable>(p => p.Add(x => x.Titulo, "Contactos").AddChildContent("<input />"));
        cut.Find("button.seccion-plegable-cabecera").GetAttribute("aria-expanded").Should().Be("false");

        cut.Render(p => p.Add(x => x.Titulo, "Contactos").Add(x => x.NumeroErrores, 2).AddChildContent("<input />"));

        cut.Find("button.seccion-plegable-cabecera").GetAttribute("aria-expanded").Should().Be("true");
    }

    // ------------------------------------------------------------ selector con alta en línea

    private string _valor = string.Empty;
    private readonly List<string> _creados = [];

    private IRenderedComponent<CampoSelectAvanzado> RenderizarSelector(bool permitirCrear = true, bool conAlta = true, Func<string, string>? texto = null) =>
        Render<CampoSelectAvanzado>(p =>
        {
            p.Add(x => x.Etiqueta, "Oficio")
             .Add(x => x.Id, "oficio")
             .Add(x => x.Opciones, new List<OpcionSelect> { new("sol", "Soldador"), new("ele", "Electricista") })
             .Add(x => x.Valor, _valor)
             .Add(x => x.ValorChanged, v => _valor = v)
             .Add(x => x.Buscable, true)
             .Add(x => x.PermitirCrear, permitirCrear);
            if (conAlta)
                p.Add(x => x.OnCrear, (string t) => { _creados.Add(t); return Task.FromResult<OpcionSelect?>(new OpcionSelect("nuevo-" + t, t)); });
            if (texto is not null) p.Add(x => x.TextoCrear, texto);
        });

    private static async Task Escribir(IRenderedComponent<CampoSelectAvanzado> cut, string texto)
    {
        if (cut.FindAll("input.csa-buscador").Count == 0) await cut.Find("button#oficio").ClickAsync(new MouseEventArgs());
        await cut.Find("input.csa-buscador").InputAsync(new ChangeEventArgs { Value = texto });
    }

    [Fact]
    public async Task Selector_ofrece_Crear_al_final_cuando_lo_escrito_no_coincide_y_lo_elige_sin_salir()
    {
        var cut = RenderizarSelector(texto: t => $"Crear oficio «{t}»");

        await Escribir(cut, "Andamiero");

        cut.Find("li.csa-crear").TextContent.Should().Contain("Crear oficio «Andamiero»");
        cut.Find("li.csa-crear").ClassList.Should().Contain("csa-activa", "sin otra opción, Intro la ejecuta");
        await cut.Find("input.csa-buscador").KeyDownAsync(new KeyboardEventArgs { Key = "Enter" });

        _creados.Should().Equal("Andamiero");
        _valor.Should().Be("nuevo-Andamiero", "la opción creada queda elegida");

        // La pantalla rebinda el valor sin añadir la opción a Opciones: el selector la conserva y se ve elegida.
        cut.Render(p => p.Add(x => x.Valor, _valor));
        cut.Find("button#oficio").TextContent.Should().Contain("Andamiero", "la opción creada se ve elegida aunque Opciones aún no la traiga");
    }

    [Fact]
    public async Task Selector_con_coincidencia_exacta_no_ofrece_Crear_y_con_parciales_Intro_elige_la_resaltada()
    {
        var cut = RenderizarSelector();

        await Escribir(cut, "soldador");
        cut.FindAll("li.csa-crear").Should().BeEmpty("hay una opción con exactamente ese texto");

        await Escribir(cut, "Sold");
        cut.FindAll("li.csa-crear").Should().ContainSingle("coincide solo en parte");
        await cut.Find("input.csa-buscador").KeyDownAsync(new KeyboardEventArgs { Key = "Enter" });
        _valor.Should().Be("sol", "Intro elige la opción resaltada, no crea");
        _creados.Should().BeEmpty();
    }

    [Fact]
    public async Task Selector_sin_permiso_o_sin_alta_no_ofrece_la_fila()
    {
        var sinPermiso = RenderizarSelector(permitirCrear: false);
        await Escribir(sinPermiso, "Andamiero");
        sinPermiso.FindAll("li.csa-crear").Should().BeEmpty("sin permiso de catálogo la fila no aparece");

        var sinAlta = RenderizarSelector(conAlta: false);
        await Escribir(sinAlta, "Andamiero");
        sinAlta.FindAll("li.csa-crear").Should().BeEmpty();
    }
}