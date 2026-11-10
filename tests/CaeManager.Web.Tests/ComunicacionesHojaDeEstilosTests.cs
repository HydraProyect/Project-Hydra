using System.Text.RegularExpressions;
using FluentAssertions;

namespace CaeManager.Web.Tests;

/// <summary>
/// Maquetación de la bandeja de Comunicaciones en staging: los conmutadores de
/// ámbito se desbordaban, el recuento de ruido se partía en vertical, el nombre de
/// un documento citado quedaba en una letra y el botón nativo de archivos se veía
/// junto a «Adjuntar». bUnit no calcula CSS, así que se inspeccionan las hojas.
/// </summary>
public class ComunicacionesHojaDeEstilosTests
{
    private const string Bandeja = "Features/Comunicaciones/Pages/Bandeja.razor.css";
    private const string Composer = "Features/Comunicaciones/Components/ComposerBar.razor.css";

    [Fact]
    public void El_ambito_es_una_navegacion_vertical_sin_altura_fija_y_los_chips_de_filtro_no_se_reparten_el_ancho()
    {
        var css = Leer(Bandeja);
        // Los chips de filtro comparten .bandeja-toggle: no deben repartirse el ancho.
        Regla(css, @"\.bandeja-toggle").Should().NotMatchRegex(@"flex\s*:\s*1");
        var item = Regla(css, @"\.bandeja-nav-item");
        item.Should().MatchRegex(@"min-height\s*:", "el rótulo puede ocupar dos líneas en la columna estrecha");
        item.Should().NotMatchRegex(@"(?<!min-)(?<!max-)height\s*:\s*\d", "sin altura fija");
        Regla(css, @"\.bandeja-nav").Should().MatchRegex(@"flex-direction\s*:\s*column");
    }

    [Fact]
    public void Los_puntos_de_corte_de_la_maqueta_son_1099_1600_y_1760_y_el_movil_es_760()
    {
        var css = Leer(Bandeja);
        css.Should().Contain("@media (max-width: 1099px)").And.NotContain("max-width: 1023px");
        Regex.IsMatch(css, @"@media \(min-width: 1600px\)\s*\{[^@]*344px").Should().BeTrue("a 1600 px la lista del hilo pasa a 344 px");
        Regex.IsMatch(css, @"@media \(min-width: 1760px\)\s*\{[^@]*380px").Should().BeTrue("a 1760 px la lista del hilo pasa a 380 px");
        Regex.IsMatch(css, @"@media \(max-width: 760px\)\s*\{[^@]*bandeja-cabecera-pagina").Should().BeTrue("en móvil la cabecera se parte en filas");
    }

    [Fact]
    public void La_tabla_de_conversaciones_va_a_sangre_con_cabecera_fija_y_divisor_de_fila()
    {
        var css = Leer("Features/Comunicaciones/Components/TablaConversaciones.razor.css");
        css.Should().Contain("position: sticky").And.Contain("--color-fila-divisor").And.Contain(".tabla-prioridad-punto");
        Regla(css, @"\.tabla-conversaciones").Should().NotMatchRegex(@"border-radius\s*:\s*var");
    }
    [Fact]
    public void Los_radios_de_la_cabecera_y_la_lista_siguen_la_regla_unica_de_la_maqueta()
    {
        // Maqueta, «Radios, regla única»: `.it` y `.bus` en radius-lg (14 px); `.mini` en radius-md (10 px).
        var css = Leer(Bandeja);
        Regla(css, @"\.bandeja-nav-item").Should().MatchRegex(@"border-radius\s*:\s*var\(--radius-lg\)");
        Regla(css, @"\.bandeja-buscar").Should().MatchRegex(@"border-radius\s*:\s*var\(--radius-lg\)");
        Regla(css, @"\.bandeja-mini").Should().MatchRegex(@"border-radius\s*:\s*var\(--radius-md\)");
    }

    [Fact]
    public void La_tabla_solo_exige_860_px_cuando_la_columna_central_los_tiene()
    {
        // 1440 px = menú de 260 + relleno de 2 x 32 + filtros de 240 -> 876 px, 861 con la barra vertical de 15 px: sin margen,
        // por eso el corte está en 1460 px. Entre 1181 y 1459 px el
        // mínimo de 860 hacía desbordar la tabla con una barra horizontal.
        var css = Leer("Features/Comunicaciones/Components/TablaConversaciones.razor.css");
        Regex.IsMatch(css, @"@media \(min-width: 1460px\)\s*\{\s*\.tabla-conversaciones\s*\{\s*min-width: 860px").Should().BeTrue();
        Regex.Matches(css, @"min-width: 860px").Should().ContainSingle("el mínimo de 860 px solo vive en el tramo ancho");
        css.Should().NotContain("1181px").And.NotContain("1180px");
        Regex.IsMatch(css, @"@media \(max-width: 1459px\)\s*\{\s*\.tabla-conversaciones-fila\s*\{\s*grid-template-columns: 76px").Should().BeTrue(
            "por debajo de 1460 px la tabla pasa a la composición apilada");
    }

    [Fact]
    public void La_prioridad_de_la_tabla_lleva_el_color_en_el_punto_y_la_fila_usa_el_fondo_de_hover_de_la_maqueta()
    {
        var css = Leer("Features/Comunicaciones/Components/TablaConversaciones.razor.css");
        var prioridad = Regla(css, @"\.tabla-prioridad");
        prioridad.Should().MatchRegex(@"gap\s*:\s*4px").And.MatchRegex(@"color\s*:\s*var\(--color-text\)");
        Regla(css, @"\.tabla-prioridad-punto").Should().Contain("var(--color-border-control)");
        Regla(css, @"\.tabla-prioridad-urgente \.tabla-prioridad-punto,\s*\.tabla-prioridad-critica \.tabla-prioridad-punto").Should().Contain("var(--color-danger-500)");
        Regla(css, @"\.tabla-conversaciones-fila:has\(button\):hover,\s*\.tabla-conversaciones-fila:focus-within")
            .Should().Contain("var(--color-fila-hover)");
        Regla(css, @"\.tabla-conversaciones-asunto button").Should().Contain("var(--color-primary-500)");
    }

    [Fact]
    public void Las_tarjetas_del_hilo_siguen_la_maqueta_sin_avatar_ni_columna_estrecha()
    {
        var css = Leer("Features/Comunicaciones/Components/UnifiedTimeline.razor.css");
        css.Should().NotContain(".timeline-avatar").And.NotContain("78%").And.NotContain("min(620px");
        var hilo = Regla(css, @"\.timeline-mensajes");
        hilo.Should().MatchRegex(@"padding\s*:\s*12px 16px").And.MatchRegex(@"gap\s*:\s*12px");
        Regla(css, @"\.timeline-mensaje-saliente").Should().Contain("var(--color-border-strong)");
        Regla(css, @"\.timeline-mensaje-remitente").Should().MatchRegex(@"font\s*:\s*600 14px/20px");
        Regla(css, @"\.timeline-mensaje-cuerpo").Should().Contain("max-width: 68ch");
        Regla(css, @"\.timeline-nota-interna-cuerpo").Should().Contain("max-width: 68ch");
    }

    [Fact]
    public void Los_estados_vacio_y_de_error_de_la_bandeja_siguen_el_vacio_de_la_maqueta()
    {
        var css = Leer(Bandeja);
        var vacio = Regla(css, @"\.bandeja-centro ::deep \.estado-vacio,\s*\.bandeja-lista ::deep \.estado-vacio");
        vacio.Should().MatchRegex(@"padding\s*:\s*32px").And.MatchRegex(@"min-height\s*:\s*200px");
        Regla(css, @"\.bandeja-centro ::deep \.estado-vacio h3,\s*\.bandeja-lista ::deep \.estado-vacio h3")
            .Should().MatchRegex(@"font\s*:\s*600 18px/26px");
        // «Al día» y el vacío comparten bloque centrado, 8 px entre piezas.
        var bloque = Regla(css, @"\.bandeja-vacio-bloque");
        bloque.Should().MatchRegex(@"gap\s*:\s*8px").And.MatchRegex(@"justify-content\s*:\s*center");
        // El 60vh de la lista en móvil es del modo hilo: en la tabla empujaba la primera fila fuera de la pantalla.
        Regex.IsMatch(css, @"@media \(max-width: 760px\)\s*\{[^@]*\.bandeja-modo-hilo \.bandeja-filtros\s*\{\s*min-height: 60vh").Should().BeTrue();
        Regex.IsMatch(css, @"(?m)^\s*\.bandeja-filtros\s*\{\s*min-height: 60vh").Should().BeFalse();
    }

    [Fact]
    public void El_pie_de_Buzones_es_un_texto_de_12_px_apagado_como_en_la_maqueta()
    {
        var css = Leer("Features/Integraciones/Pages/Conexiones.razor.css");
        var pie = Regla(css, @"\.pie-buzones");
        pie.Should().MatchRegex(@"font\s*:\s*500 12px/16px").And.Contain("var(--color-text-muted)");
    }

    [Fact]
    public void Los_badges_de_la_linea_de_estado_de_la_fila_no_se_parten()
    {
        var regla = Regla(Leer(Bandeja), @"\.bandeja-lista ::deep \.bandeja-fila-linea3 > :not\(\.bandeja-fila-preview\)");
        regla.Should().MatchRegex(@"flex-shrink\s*:\s*0");
        regla.Should().MatchRegex(@"white-space\s*:\s*nowrap");
    }

    [Fact]
    public void Los_anchos_de_bandeja_lista_y_contexto_siguen_la_maqueta()
    {
        var css = Leer(Bandeja);
        Regla(css, @"\.bandeja-modo-tabla").Should().Contain("--bandeja-ancho-lista: 240px");
        Regla(css, @"\.bandeja-modo-hilo").Should().Contain("--bandeja-ancho-lista: 304px");
        Regla(css, @"\.bandeja-modo-hilo:has\(\.bandeja-cliente\)").Should().Contain("344px");
    }

    [Fact]
    public void El_documento_citado_apila_nombre_y_meta_y_deja_envolver_el_nombre()
    {
        var css = Leer(Bandeja);
        Regla(css, @"\.bandeja-documento-citado").Should().MatchRegex(@"flex-direction\s*:\s*column");
        Regla(css, @"\.bandeja-documento-citado-nombre").Should().NotMatchRegex(@"white-space\s*:\s*nowrap");
        Regla(css, @"\.bandeja-documento-citado-meta").Should().NotMatchRegex(@"flex-shrink\s*:\s*0");
    }

    [Fact]
    public void El_input_de_archivos_del_composer_se_oculta_con_deep()
    {
        var css = Leer(Composer);
        Regla(css, @"\.composer-boton-adjuntar ::deep input\[type=""file""\]").Should().MatchRegex(@"opacity\s*:\s*0");
        Regex.IsMatch(css, @"(?m)^\.composer-boton-adjuntar input\[type=""file""\]").Should().BeFalse(
            "sin ::deep la regla no alcanza al <input> del componente InputFile");
    }

    /// <summary>Cuerpo de la regla cuyo selector es exactamente <paramref name="selector"/>. Falla si no existe (control positivo).</summary>
    private static string Regla(string css, string selector)
    {
        var m = Regex.Match(css, @"(?m)^" + selector + @"\s*\{(?<c>[^}]*)\}");
        m.Success.Should().BeTrue($"la regla «{selector}» debe existir");
        return Regex.Replace(m.Groups["c"].Value, @"/\*.*?\*/", "", RegexOptions.Singleline);
    }

    private static string Leer(string relativa)
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "CaeManager.slnx")))
            dir = Path.GetDirectoryName(dir);
        dir.Should().NotBeNull("se necesita la raíz del repositorio para leer la hoja de estilos");
        return File.ReadAllText(Path.Combine(dir!, "src", "CaeManager.Web", relativa));
    }
}
