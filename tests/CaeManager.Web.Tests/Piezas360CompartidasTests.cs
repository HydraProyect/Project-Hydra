using Bunit;
using CaeManager.Web.Components.DesignSystem;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using System.Text.RegularExpressions;

namespace CaeManager.Web.Tests;

/// <summary>
/// Contrato de las piezas que comparten las páginas 360 del 2026-10-08
/// (Vehículo, Subcontrata, Tipo de documento, Proyecto y Visita):
/// <see cref="CabeceraIdentidad"/>, <see cref="BandaAccion"/> con
/// <see cref="IncidenciaBanda"/>, <see cref="FiltroEstadosMultiple"/>, y la
/// fila en rejilla de <see cref="FilaRelacion"/> dentro de una
/// <see cref="ListaRelaciones"/> pintada como tarjeta.
///
/// Lo que estas pruebas NO miden: medidas ni colores. Eso es CSS y lo mide el
/// comparador de fidelidad (E2E) sobre estilos calculados; aquí se fija la
/// estructura que ese CSS y ese comparador necesitan.
/// </summary>
public class Piezas360CompartidasTests : BunitContext
{
    public Piezas360CompartidasTests()
    {
        Services.AddLocalization();
    }

    private static RenderFragment Texto(string texto) => b => b.AddContent(0, texto);

    private static RenderFragment Marca(string id) => b => b.AddMarkupContent(0, $"<span id='{id}'></span>");

    // ── CabeceraIdentidad ──────────────────────────────────────────────────

    [Fact]
    public void La_cabecera_de_identidad_es_la_pieza_que_busca_el_comparador_y_lleva_el_h1()
    {
        var cut = Render<CabeceraIdentidad>(p => p
            .Add(x => x.Titulo, "Camión grúa")
            .Add(x => x.Kicker, "Vehículo")
            .Add(x => x.Anillo, Marca("anillo"))
            .Add(x => x.Datos, Marca("datos"))
            .Add(x => x.Acciones, Marca("acciones")));

        var cabecera = cut.Find("header[data-pieza='cabecera-identidad']");
        cabecera.QuerySelector(".cabecera-identidad-kicker")!.TextContent.Should().Be("Vehículo");
        cut.FindAll("h1").Should().ContainSingle().Which.TextContent.Should().Be("Camión grúa");
        cabecera.QuerySelector(".cabecera-identidad-anillo #anillo").Should().NotBeNull();
        cabecera.QuerySelector(".cabecera-identidad-datos #datos").Should().NotBeNull();
        cabecera.QuerySelector(".cabecera-identidad-acciones #acciones").Should().NotBeNull();
    }

    [Fact]
    public void Sin_piezas_opcionales_la_cabecera_no_deja_huecos()
    {
        var cut = Render<CabeceraIdentidad>(p => p.Add(x => x.Titulo, "Reforma nave 3"));

        cut.FindAll(".cabecera-identidad-anillo, .cabecera-identidad-kicker, .cabecera-identidad-datos, .cabecera-identidad-acciones, .cabecera-identidad-titular")
            .Should().BeEmpty();
    }

    // El velo de cumplimiento vive en base.css y casa por :has() con las clases que pinta
    // AnilloCumplimiento. Lo que este test observa: que cada regla de color del velo encuentra
    // el anillo que dice buscar dentro de la cabecera, y que sin anillo ninguna casa. Lo que NO
    // observa: el degradado pintado; eso se mide en el navegador.
    [Theory]
    [InlineData(100, "--color-success-700")]
    [InlineData(75, "--color-warning-700")]
    [InlineData(10, "--color-danger-700")]
    public void El_velo_de_cumplimiento_toma_el_color_del_anillo_de_la_cabecera(int porcentaje, string colorEsperado)
    {
        var cut = Render<CabeceraIdentidad>(p => p
            .Add(x => x.Titulo, "Camión grúa")
            .Add(x => x.Anillo, AnilloDeCabecera(porcentaje)));

        var cabecera = cut.Find("header.velo-cumplimiento");
        var reglas = ReglasDeColorDelVelo();

        // Control positivo: el lector ve las tres reglas de color (verde, ámbar y rojo).
        reglas.Should().HaveCount(3);
        reglas.Where(r => cabecera.QuerySelector(r.SelectorDelAnillo) is not null)
            .Select(r => r.Color).Should().Equal(colorEsperado);
    }

    [Fact]
    public void Sin_anillo_o_con_el_anillo_sin_universo_ninguna_regla_del_velo_casa()
    {
        var sinAnillo = Render<CabeceraIdentidad>(p => p.Add(x => x.Titulo, "Reforma nave 3"));
        var sinUniverso = Render<CabeceraIdentidad>(p => p
            .Add(x => x.Titulo, "Reforma nave 3")
            .Add(x => x.Anillo, AnilloDeCabecera(null)));

        foreach (var cabecera in new[] { sinAnillo.Find("header.velo-cumplimiento"), sinUniverso.Find("header.velo-cumplimiento") })
            ReglasDeColorDelVelo().Where(r => cabecera.QuerySelector(r.SelectorDelAnillo) is not null).Should().BeEmpty();
    }

    [Fact]
    public void La_cabecera_no_fija_su_fondo_con_el_atajo_que_borraria_el_velo()
    {
        var hoja = LeerDeWeb("Components", "DesignSystem", "CabeceraIdentidad.razor.css");

        hoja.Should().Contain("background-color:");
        Regex.IsMatch(hoja, @"(?m)^\s*background\s*:").Should().BeFalse(
            "el atajo background borra el background-image con el que base.css pinta el velo de cumplimiento");
    }

    private static RenderFragment AnilloDeCabecera(int? porcentaje) => b =>
    {
        b.OpenComponent<AnilloCumplimiento>(0);
        b.AddAttribute(1, nameof(AnilloCumplimiento.Porcentaje), porcentaje);
        b.AddAttribute(2, nameof(AnilloCumplimiento.Tamano), TamanoAnillo.Cabecera);
        b.CloseComponent();
    };

    /// <summary>Las reglas <c>.velo-cumplimiento:has(…) { --velo-cumplimiento-color: var(…) }</c> de base.css.</summary>
    private static List<(string SelectorDelAnillo, string Color)> ReglasDeColorDelVelo()
        => Regex
            .Matches(LeerDeWeb("wwwroot", "css", "base.css"),
                @"\.velo-cumplimiento:has\((?<anillo>[^()]+)\)\s*\{\s*--velo-cumplimiento-color:\s*var\((?<color>--[a-z0-9-]+)\);\s*\}")
            .Select(m => (m.Groups["anillo"].Value, m.Groups["color"].Value))
            .ToList();

    private static string LeerDeWeb(params string[] partes)
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "CaeManager.slnx")))
            dir = Path.GetDirectoryName(dir);

        return File.ReadAllText(Path.Combine([dir!, "src", "CaeManager.Web", .. partes]));
    }

    [Fact]
    public void La_pieza_junto_al_titulo_va_en_la_misma_linea_que_el_h1()
    {
        var cut = Render<CabeceraIdentidad>(p => p
            .Add(x => x.Titulo, "Reforma nave 3")
            .Add(x => x.JuntoAlTitulo, Marca("estado")));

        var titular = cut.Find(".cabecera-identidad-titular");
        titular.QuerySelector("h1").Should().NotBeNull();
        titular.QuerySelector("#estado").Should().NotBeNull();
    }

    // ── BandaAccion / IncidenciaBanda ──────────────────────────────────────

    [Theory]
    [InlineData(TonoBanda.Peligro, "peligro")]
    [InlineData(TonoBanda.Advertencia, "advertencia")]
    [InlineData(TonoBanda.Neutro, "neutro")]
    public void La_banda_declara_su_tono_y_se_anuncia_como_estado(TonoBanda tono, string esperado)
    {
        var cut = Render<BandaAccion>(p => p.Add(x => x.Tono, tono).AddChildContent("el 02/09/2026."));

        var banda = cut.Find(".banda-accion");
        banda.GetAttribute("role").Should().Be("status");
        banda.GetAttribute("data-tono").Should().Be(esperado);
        banda.TextContent.Should().Contain("el 02/09/2026.");
    }

    [Fact]
    public void El_enlace_de_la_banda_llama_a_su_accion()
    {
        var pulsado = 0;
        var cut = Render<BandaAccion>(p => p
            .Add(x => x.TextoEnlace, "Renovar ITV")
            .Add(x => x.OnEnlace, () => pulsado++));

        var enlace = cut.Find("button.banda-accion-enlace");
        enlace.TextContent.Should().Be("Renovar ITV");
        enlace.Click();

        pulsado.Should().Be(1);
    }

    [Fact]
    public void Sin_accion_la_banda_no_pinta_enlace_aunque_tenga_rotulo()
    {
        // Es el rol Consulta: conserva el texto de la banda y pierde los botones.
        var cut = Render<BandaAccion>(p => p.Add(x => x.TextoEnlace, "Renovar ITV").AddChildContent("ITV vencida"));

        cut.FindAll("button").Should().BeEmpty();
    }

    [Fact]
    public void La_incidencia_con_accion_es_un_boton_y_sin_ella_es_texto()
    {
        var pulsado = 0;
        var conAccion = Render<IncidenciaBanda>(p => p.Add(x => x.Texto, "ITV vencida").Add(x => x.OnClick, () => pulsado++));

        var boton = conAccion.Find("button.incidencia-banda");
        boton.TextContent.Should().Be("ITV vencida");
        boton.Click();
        pulsado.Should().Be(1);

        var sinAccion = Render<IncidenciaBanda>(p => p.Add(x => x.Texto, "ITV vencida"));

        sinAccion.FindAll("button").Should().BeEmpty();
        sinAccion.Find("b").TextContent.Should().Be("ITV vencida");
    }

    // ── FiltroEstadosMultiple ──────────────────────────────────────────────

    private static readonly IReadOnlyList<OpcionEstadoRecuento> Opciones =
    [
        new("Vencido", "Vencido", 1),
        new("PorVencer", "Por vencer", 2),
        new("SinConfirmar", "Sin confirmar", 0),
        new("Vigente", "Vigente", 1),
    ];

    private IRenderedComponent<FiltroEstadosMultiple> RenderFiltro(
        IReadOnlySet<string> seleccion, Action<IReadOnlySet<string>> alCambiar) =>
        Render<FiltroEstadosMultiple>(p => p
            .Add(x => x.Opciones, Opciones)
            .Add(x => x.Total, 4)
            .Add(x => x.Seleccion, seleccion)
            .Add(x => x.SeleccionChanged, alCambiar));

    [Fact]
    public void El_filtro_pinta_Todos_y_solo_los_estados_presentes_cada_uno_con_aria_pressed()
    {
        var cut = RenderFiltro(new HashSet<string>(), _ => { });

        cut.Find("[role='group']").GetAttribute("aria-label").Should().Be("Filtrar por estado");
        var botones = cut.FindAll("button");
        botones.Select(b => b.TextContent).Should().Equal("Todos · 4", "Vencido · 1", "Por vencer · 2", "Vigente · 1");
        botones.Select(b => b.GetAttribute("aria-pressed")).Should().Equal("true", "false", "false", "false");
    }

    [Fact]
    public void Marcar_un_estado_lo_suma_a_la_seleccion_y_volver_a_marcarlo_lo_quita()
    {
        IReadOnlySet<string>? emitida = null;
        var cut = RenderFiltro(new HashSet<string> { "Vencido" }, s => emitida = s);

        cut.FindAll("button").Select(b => b.GetAttribute("aria-pressed"))
            .Should().Equal("false", "true", "false", "false");

        cut.FindAll("button")[2].Click();
        emitida.Should().BeEquivalentTo(["Vencido", "PorVencer"]);

        cut.FindAll("button")[1].Click();
        emitida.Should().BeEmpty();
    }

    [Fact]
    public void Todos_borra_la_seleccion()
    {
        IReadOnlySet<string>? emitida = null;
        var cut = RenderFiltro(new HashSet<string> { "Vencido", "Vigente" }, s => emitida = s);

        cut.FindAll("button")[0].Click();

        emitida.Should().NotBeNull().And.BeEmpty();
    }

    [Fact]
    public void Un_estado_marcado_que_se_queda_sin_filas_sigue_a_la_vista_para_poder_quitarlo()
    {
        var cut = RenderFiltro(new HashSet<string> { "SinConfirmar" }, _ => { });

        cut.FindAll("button").Select(b => b.TextContent).Should().Contain("Sin confirmar · 0");
    }

    // ── FilaRelacion en rejilla / ListaRelaciones en tarjeta ───────────────

    [Fact]
    public void Con_una_ranura_de_rejilla_la_fila_pinta_siempre_las_tres_columnas_fijas()
    {
        var cut = Render<FilaRelacion>(p => p
            .Add(x => x.Nombre, "ITV")
            .Add(x => x.NombreIcono, "documentos")
            .Add(x => x.Estado, Marca("pastilla")));

        var fila = cut.Find("li.fila-relacion.fila-relacion-rejilla[data-pieza='fila']");
        fila.Children.Select(c => c.ClassName).Should().Equal(
            "fila-relacion-identidad", "fila-relacion-cifra", "fila-relacion-estado", "fila-relacion-acciones");
        fila.QuerySelector(".fila-relacion-estado #pastilla").Should().NotBeNull();
        fila.QuerySelector(".fila-relacion-identidad .fila-relacion-icono").Should().NotBeNull();
        fila.HasAttribute("data-tono").Should().BeFalse("una fila sin problema no lleva tono");
        cut.FindAll(".fila-relacion-derecha").Should().BeEmpty();
    }

    [Fact]
    public void En_rejilla_el_boton_360_va_al_final_de_la_columna_de_acciones()
    {
        var abiertos = 0;
        var cut = Render<FilaRelacion>(p => p
            .Add(x => x.Nombre, "Planta Barakaldo")
            .Add(x => x.Acciones, Marca("renovar"))
            .Add(x => x.OnAbrir360, () => abiertos++));

        var acciones = cut.Find(".fila-relacion-acciones");
        acciones.Children.Select(c => string.IsNullOrEmpty(c.Id) ? c.ClassName : c.Id).Should().Equal("renovar", "boton-360");

        cut.Find("button.boton-360").Click();
        abiertos.Should().Be(1);
    }

    [Theory]
    [InlineData(TonoFila.Peligro, "peligro")]
    [InlineData(TonoFila.Advertencia, "advertencia")]
    public void La_fila_con_problema_declara_su_tono(TonoFila tono, string esperado)
    {
        var cut = Render<FilaRelacion>(p => p
            .Add(x => x.Nombre, "ITV")
            .Add(x => x.Tono, tono)
            .Add(x => x.Estado, Marca("pastilla")));

        cut.Find("[data-pieza='fila']").GetAttribute("data-tono").Should().Be(esperado);
    }

    [Fact]
    public void Sin_ranuras_de_rejilla_la_fila_sigue_siendo_la_de_Cliente_360_y_Empresa_360()
    {
        var cut = Render<FilaRelacion>(p => p
            .Add(x => x.Nombre, "Planta Barakaldo")
            .Add(x => x.Derecha, Marca("estado")));

        cut.FindAll(".fila-relacion-rejilla, .fila-relacion-cifra, .fila-relacion-estado, .fila-relacion-acciones")
            .Should().BeEmpty();
        cut.Find(".fila-relacion-derecha #estado").Should().NotBeNull();
    }

    [Fact]
    public void El_avatar_gana_a_las_iniciales_y_al_icono()
    {
        var cut = Render<FilaRelacion>(p => p
            .Add(x => x.Nombre, "Sarah Connor")
            .Add(x => x.Iniciales, "SC")
            .Add(x => x.NombreIcono, "documentos")
            .Add(x => x.Avatar, Marca("avatar")));

        cut.Find(".fila-relacion-casilla #avatar").Should().NotBeNull();
        cut.FindAll(".fila-relacion-avatar, .fila-relacion-icono").Should().BeEmpty();
    }

    [Fact]
    public void La_fila_desplegable_avisa_con_aria_expanded_y_solo_pinta_el_bloque_desplegada()
    {
        bool? pedido = null;
        var cut = Render<FilaRelacion>(p => p
            .Add(x => x.Nombre, "Reconocimiento médico")
            .Add(x => x.Estado, Marca("pastilla"))
            .Add(x => x.Desplegable, Marca("centros"))
            .Add(x => x.DesplegadaChanged, (bool v) => pedido = v));

        var boton = cut.Find("button.fila-relacion-desplegar");
        boton.GetAttribute("aria-expanded").Should().Be("false");
        boton.GetAttribute("aria-label").Should().Be("Desplegar Reconocimiento médico");
        cut.FindAll(".fila-relacion-desplegado").Should().BeEmpty();

        boton.Click();
        pedido.Should().BeTrue();

        cut.Render(p => p.Add(x => x.Desplegada, true));

        cut.Find("button.fila-relacion-desplegar").GetAttribute("aria-expanded").Should().Be("true");
        cut.Find(".fila-relacion-desplegado #centros").Should().NotBeNull();
    }

    [Fact]
    public void La_lista_en_tarjeta_es_una_tarjeta_para_el_comparador_con_encabezado_y_pie()
    {
        var cut = Render<ListaRelaciones>(p => p
            .Add(l => l.Etiqueta, "Documentos del vehículo")
            .Add(l => l.EnTarjeta, true)
            .Add(l => l.Encabezado, Texto("Mostrando 4 de 4"))
            .Add(l => l.Pie, Texto("Solo aparecen documentos registrados."))
            .AddChildContent<FilaRelacion>(f => f.Add(x => x.Nombre, "ITV").Add(x => x.Estado, Marca("pastilla"))));

        var tarjeta = cut.Find("div.lista-relaciones-tarjeta[data-pieza='tarjeta']");
        tarjeta.QuerySelector(".lista-relaciones-encabezado")!.TextContent.Should().Be("Mostrando 4 de 4");
        tarjeta.QuerySelector(".lista-relaciones-pie")!.TextContent.Should().Be("Solo aparecen documentos registrados.");
        var lista = tarjeta.QuerySelector("ul.lista-relaciones.lista-relaciones-sin-caja")!;
        lista.GetAttribute("aria-label").Should().Be("Documentos del vehículo");
        lista.QuerySelectorAll("li[data-pieza='fila']").Should().ContainSingle();
    }

    [Fact]
    public void Sin_EnTarjeta_la_lista_sigue_siendo_la_caja_de_siempre()
    {
        var cut = Render<ListaRelaciones>(p => p
            .Add(l => l.Encabezado, Texto("no se pinta"))
            .AddChildContent<FilaRelacion>(f => f.Add(x => x.Nombre, "Planta Barakaldo")));

        cut.FindAll("[data-pieza='tarjeta'], .lista-relaciones-encabezado, .lista-relaciones-sin-caja").Should().BeEmpty();
        cut.Find("ul.lista-relaciones").Should().NotBeNull();
    }
}
