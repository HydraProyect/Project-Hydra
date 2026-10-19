using Bunit;
using CaeManager.Web.Components.DesignSystem;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// «Panel Formulario TALVEG»: fija valor a valor lo que la maqueta dicta para el panel lateral (cabecera, cuerpo, pie, pasos,
/// aviso, esqueleto, casillas). Cada aserción cita la regla de la maqueta; si cambia la maqueta, cambia aquí.
/// </summary>
public class PanelFormularioAnatomiaMaquetaTests : BunitContext
{
    public PanelFormularioAnatomiaMaquetaTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLocalization();
    }

    private IRenderedComponent<DrawerFormulario> Renderizar(Action<ComponentParameterCollectionBuilder<DrawerFormulario>>? extra = null) =>
        Render<DrawerFormulario>(p =>
        {
            p.Add(x => x.Visible, true)
             .Add(x => x.VisibleChanged, _ => { })
             .Add(x => x.Titulo, "Nuevo trabajador")
             .Add(x => x.HayCambios, () => false)
             .Add(x => x.TextoGuardar, "Guardar")
             .Add(x => x.TextoCancelar, "Cancelar")
             .Add(x => x.OnGuardar, EventCallback.Factory.Create(this, () => { }))
             .AddChildContent("<input class=\"campo-input\" />");
            extra?.Invoke(p);
        });

    private static string Hoja(string fichero)
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "CaeManager.slnx")))
            dir = Path.GetDirectoryName(dir);
        dir.Should().NotBeNull("se necesita la raíz del repositorio para leer la hoja de estilos");
        return File.ReadAllText(Path.Combine(dir!, "src", "CaeManager.Web", "Components", "DesignSystem", fichero));
    }

    private static string Normalizada(string css) => System.Text.RegularExpressions.Regex.Replace(css, @"\s+", " ");

    [Fact]
    public void El_aviso_del_panel_es_una_caja_con_borde_danger_icono_y_texto_de_cuerpo()
    {
        var cut = Renderizar(p => p.Add(x => x.MensajeError, "No se pudo guardar."));

        var aviso = cut.Find(".drawer-aviso .alerta-formulario");
        aviso.ClassList.Should().Contain("alerta-formulario-panel");
        aviso.QuerySelector("svg").Should().NotBeNull("la maqueta antepone el icono de error al texto");
        aviso.TextContent.Trim().Should().Be("No se pudo guardar.");

        var css = Normalizada(Hoja("AvisoFormulario.razor.css"));
        css.Should().Contain("padding: 12px 14px");
        css.Should().Contain("border: 1px solid var(--color-danger-500)");
        css.Should().Contain("border-radius: var(--radius-md)");
        css.Should().Contain("gap: 10px");
        css.Should().Contain("font: var(--font-body-md)");
    }

    [Fact]
    public void Fuera_del_panel_el_aviso_sigue_siendo_el_de_siempre()
    {
        var cut = Render<AvisoFormulario>(p => p.Add(x => x.Mensaje, "Falta el nombre."));

        cut.Find(".alerta-formulario").ClassList.Should().NotContain("alerta-formulario-panel");
        cut.FindAll("svg").Should().BeEmpty();
    }

    [Fact]
    public void El_esqueleto_del_panel_son_campos_de_etiqueta_de_12_px_y_bloque_de_48_px()
    {
        var cut = Renderizar(p => p.Add(x => x.Cargando, true).Add(x => x.FilasCargando, 4));

        cut.FindAll(".drawer-cuerpo .esqueleto-campo").Should().HaveCount(4);
        cut.FindAll(".drawer-cuerpo .esqueleto-campo > .esqueleto-etiqueta").Should().HaveCount(4);
        cut.FindAll(".drawer-cuerpo .esqueleto-fila").Should().HaveCount(4, "el recuento de filas no cambia");

        var css = Normalizada(Hoja("EstadoCargando.razor.css"));
        css.Should().Contain(".esqueleto-campo .esqueleto-fila { height: 48px; border-radius: var(--radius-md); }");
        css.Should().Contain(".esqueleto-etiqueta { height: 12px;");
    }

    [Fact]
    public void El_esqueleto_fuera_del_panel_no_cambia()
    {
        var cut = Render<EstadoCargando>(p => p.Add(x => x.Filas, 3));

        cut.FindAll(".esqueleto-campo").Should().BeEmpty();
        cut.FindAll(".esqueleto-fila").Should().HaveCount(3);
    }

    [Fact]
    public void El_pie_mide_los_botones_por_contenido_y_en_movil_es_una_rejilla_de_dos_columnas()
    {
        var drawer = Normalizada(Hoja("Drawer.razor.css"));

        // maqueta: pieStyle escritorio flex, gap 12, a la derecha; móvil grid 1fr 1fr gap 8.
        drawer.Should().Contain(".drawer-pie { flex: none; display: flex; align-items: flex-start; justify-content: flex-end; gap: var(--space-3); padding: var(--space-4) var(--space-6);");
        drawer.Should().Contain(".drawer-pie ::deep .boton-espera-boton { min-width: 0; }");
        drawer.Should().MatchRegex(@"@media \(max-width: 640px\) \{ \.drawer-pie \{ display: grid; grid-template-columns: 1fr 1fr; align-items: stretch; gap: var\(--space-2\); \}");

        var boton = Normalizada(Hoja("Boton.razor.css"));
        // maqueta .btn: min-height 40, padding 0 20, font label con 14 px.
        boton.Should().Contain(".boton-medio { padding: 0 1.25rem; min-height: 40px; font-size: 0.875rem; }");
    }

    [Fact]
    public void En_movil_el_pie_en_pasos_pone_Siguiente_arriba_Atras_despues_y_Guardar_y_terminar_al_final()
    {
        var css = Normalizada(Hoja("DrawerFormulario.razor.css"));

        css.Should().Contain(".formulario-drawer-siguiente { grid-column: span 2; order: 1; }");
        css.Should().Contain(".formulario-drawer-atras-intermedio { order: 2; }");
        css.Should().Contain(".formulario-drawer-terminar { order: 3; }");
    }

    [Fact]
    public void El_indicador_de_pasos_separa_boton_y_raya_6_px_y_la_raya_reparte_el_ancho()
    {
        var css = Normalizada(Hoja("IndicadorPasos.razor.css"));

        css.Should().Contain(".indicador-pasos-panel { align-items: center; gap: 6px; }");
        css.Should().Contain(".indicador-pasos-panel .indicador-pasos-item { display: contents; }");
        css.Should().Contain(".indicador-pasos-panel .indicador-pasos-item:not(:last-child)::after { flex: 1; height: 1px; min-width: var(--space-2); margin: 0; }");
    }

    [Fact]
    public void El_cuerpo_del_panel_pone_6_px_entre_etiqueta_y_control_y_el_control_oscuro_sobre_surface_subtle()
    {
        var css = Normalizada(Hoja("Drawer.razor.css"));

        css.Should().Contain(".drawer-cuerpo ::deep .campo { gap: 6px; }");
        css.Should().Contain(":root[data-theme=\"oscuro\"] .drawer-cuerpo ::deep .campo-input,");
        css.Should().Contain("background-color: var(--color-surface-subtle);");
    }

    [Fact]
    public void El_grupo_de_casillas_del_panel_son_filas_de_52_px_con_raya_y_casilla_de_18_px()
    {
        var css = Normalizada(Hoja("Drawer.razor.css"));

        css.Should().Contain(".drawer-cuerpo ::deep .lista-seleccion-multiple .campo-checkbox { min-height: 52px; padding: 8px 14px; gap: var(--space-3);");
        css.Should().Contain(".drawer-cuerpo ::deep .lista-seleccion-multiple .campo-checkbox input { width: 18px; height: 18px;");
    }
}
