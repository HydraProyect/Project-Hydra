using System.Text.RegularExpressions;
using Bunit;
using CaeManager.Domain.Centros;
using CaeManager.Web.Features.Centros;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// Pulido de la lista de Centros frente a la maqueta (2026-11-07): el rótulo corto «Bloqueo CAE» solo en la lista, y la
/// fila lista para apilarse a 375 px sin cambiar su contenido ni su orden en el documento. El apilado en sí es CSS
/// (bUnit no calcula maquetación): aquí se fija lo que lo sostiene en el marcado y en la hoja fuente; el ancho real se mide
/// en el navegador a 375 px.
/// </summary>
public partial class CentrosListaPatronTests
{
    // ------------------------------------------------- «Bloqueo CAE» solo en la lista

    [Fact]
    public void El_estado_Bloqueado_se_rotula_Bloqueo_CAE_en_la_fila_y_en_la_franja_de_la_lista()
    {
        var cut = Renderizar(ConCentros(Centro("Centro Norte", EstadoCentro.Bloqueado)));

        cut.Find(".tarjeta-fila-acordeon-cabecera .celda-estado-centro").TextContent.Trim()
            .Should().StartWith("Bloqueo CAE").And.NotContain("plataforma");
        cut.BotonDeFranja("Bloqueo CAE").GetAttribute("aria-pressed")
            .Should().Be("false", "el botón de la franja lleva el mismo rótulo corto que la pastilla de la fila");
        cut.Markup.Should().NotContain("Bloqueo de la plataforma CAE");
    }

    [Fact]
    public void El_boton_Bloqueo_CAE_de_la_franja_sigue_filtrando_el_estado_Bloqueado()
    {
        var cut = Renderizar(ConCentros(Centro("Centro Norte", EstadoCentro.Bloqueado), Centro("Centro Sur")));

        cut.BotonDeFranja("Bloqueo CAE").Click();

        Services.GetRequiredService<NavigationManager>().Uri.Should().Contain("estado=Bloqueado");
    }

    [Fact]
    public void El_rotulo_largo_se_conserva_en_la_fuente_comun_que_comparten_las_demas_pantallas()
    {
        EstadoCentroUi.Texto(EstadoCentro.Bloqueado).Should().Be("Bloqueo de la plataforma CAE",
            "Centro 360, el panel, Clientes y la exportación siguen diciendo lo mismo que Cliente 360");
    }

    [Theory]
    [InlineData(EstadoCentro.Vencido, "Vencido")]
    [InlineData(EstadoCentro.Urgente, "Por vencer")]
    [InlineData(EstadoCentro.Faltante, "Pendiente")]
    [InlineData(EstadoCentro.SinGestionCae, "No requiere gestión CAE")]
    [InlineData(EstadoCentro.Vigente, "Vigente")]
    public void El_resto_de_estados_conserva_su_rotulo_en_la_fila(EstadoCentro estado, string esperado)
    {
        var cut = Renderizar(ConCentros(Centro("Centro Norte", estado)));

        cut.Find(".tarjeta-fila-acordeon-cabecera .celda-estado-centro").TextContent.Trim().Should().StartWith(esperado);
    }

    // ------------------------------------------------- fila apilable a 375 px

    /// <summary>
    /// El apilado recoloca con <c>order</c> y no reordena el documento: lectores de pantalla y teclado siguen leyendo
    /// Centro, Empresa, Cumplimiento y Estado, y el icono 360 cierra la fila. La celda del chevron existe con y sin
    /// desplegable porque es la pista de 24 px que sangra Empresa, Cumplimiento y Estado.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void La_fila_conserva_el_orden_de_sus_celdas_y_siempre_lleva_la_celda_del_chevron(bool seleccionMultiple)
    {
        var cut = Renderizar(ConDosClientes());
        if (seleccionMultiple)
            cut.Find("header.cabecera-pagina button[aria-label='Selección múltiple']").Click();

        foreach (var fila in cut.FindAll(".tarjeta-fila-acordeon-cabecera"))
        {
            string[] esperadas =
            [
                .. seleccionMultiple ? new[] { "seleccion-centro" } : [],
                "celda-chevron-centro", "celda-identidad-centro", "columna-empresa-centro",
                "columna-cumplimiento-centro", "celda-estado-centro", "tarjeta-fila-acordeon-acciones"
            ];
            fila.Children.Select(c => esperadas.FirstOrDefault(c.ClassList.Contains)).Should().Equal(esperadas);
        }
    }

    [Fact]
    public void La_cabecera_conserva_los_dos_rotulos_que_ordenan_y_la_casilla_de_pagina()
    {
        var cut = Renderizar(ConDosClientes());
        cut.Find("header.cabecera-pagina button[aria-label='Selección múltiple']").Click();

        var cabecera = cut.Find(".cabecera-columnas-centros");
        cabecera.QuerySelectorAll(".seleccion-centro input[type=checkbox]").Length.Should().Be(1);
        var rotulos = cabecera.QuerySelectorAll("button.cabecera-columna-orden").Select(b => b.TextContent.Trim()).ToList();
        rotulos.Should().HaveCount(2);
        rotulos[0].Should().StartWith("Empresa");
        rotulos[1].Should().StartWith("Cumplimiento");
    }

    /// <summary>
    /// Lo que la vista de main ya tenía y no se toca: el Cliente con el código bajo el nombre (sin agrupar), la Visita que
    /// cierra esa línea, el icono 360 y el nombre que abre la vista rápida. Los fijan también
    /// <c>La_fila_lleva_las_columnas_Empresa_y_Cumplimiento_...</c> y las pruebas de Visita de <c>CentrosListaGen2Tests</c>.
    /// </summary>
    [Fact]
    public void La_fila_conserva_el_icono_360_el_nombre_que_abre_la_vista_rapida_y_la_segunda_linea()
    {
        var centro = CentroDe("Planta Bilbao", ClientePegaso, EmpresaLimpiezas);
        var cut = Renderizar(ConCentros(centro));

        var fila = cut.Find(".tarjeta-fila-acordeon-cabecera");
        fila.QuerySelector(".tarjeta-fila-acordeon-acciones a")!.GetAttribute("href").Should().Be($"/centros/{centro.Id}");
        fila.QuerySelector(".celda-identidad-centro .enlace-nombre-fila")!.TextContent.Trim().Should().Be("Planta Bilbao");
        fila.QuerySelector(".celda-meta-centro")!.TextContent.Should().Contain("Pegaso Cliente S.L. · C-001");
    }

    // ------------------------------------------------- hoja de estilos (bUnit no calcula maquetación)

    private static string CssLista()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "CaeManager.slnx")))
            dir = Path.GetDirectoryName(dir);
        dir.Should().NotBeNull("se necesita la raíz del repositorio");
        var css = File.ReadAllText(Path.Combine(dir!, "src", "CaeManager.Web", "Features", "Centros", "Pages", "Centros.razor.css"));
        return Regex.Replace(css, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
    }

    /// <summary>Cuerpo del bloque <c>@media (max-width: 900px) { ... }</c> de la hoja de la lista.</summary>
    private static string BloqueMovil()
    {
        var css = CssLista();
        var inicio = css.IndexOf("@media (max-width: 900px) {", StringComparison.Ordinal);
        inicio.Should().BeGreaterThanOrEqualTo(0, "la lista tiene su bloque para pantalla estrecha");
        var abre = css.IndexOf('{', inicio);
        var profundidad = 0;
        for (var i = abre; i < css.Length; i++)
        {
            if (css[i] == '{') profundidad++;
            else if (css[i] == '}' && --profundidad == 0) return css[(abre + 1)..i];
        }
        throw new InvalidOperationException("Bloque @media sin cerrar.");
    }

    [Fact]
    public void A_375_px_la_fila_y_la_cabecera_dejan_de_ser_una_rejilla_de_seis_pistas_y_se_apilan()
    {
        var movil = BloqueMovil();

        Regex.IsMatch(movil, @"\.marco-lista-centros \.rejilla-centros\s*\{[^}]*display\s*:\s*flex[^}]*flex-wrap\s*:\s*wrap")
            .Should().BeTrue("la rejilla de escritorio mide más de 800 px: en móvil es una caja flexible que envuelve");
        Regex.IsMatch(movil, @"\.tarjeta-listado-contenido > \.marco-lista-centros\s*\{[^}]*min-width\s*:\s*0")
            .Should().BeTrue("lista-design.css da 860 px mínimos al marco por debajo de 900 px: sin quitarlos la tarjeta desplaza en horizontal");
        Regex.IsMatch(movil, @"\.columna-empresa-centro,[^{]*\.columna-cumplimiento-centro,[^{]*\.celda-estado-centro\s*\{[^}]*flex\s*:\s*1 1 100%")
            .Should().BeTrue("Empresa, Cumplimiento y Estado ocupan cada uno su línea");
        Regex.IsMatch(movil, @"\.celda-identidad-centro\s*\{[^}]*min-width\s*:\s*0")
            .Should().BeTrue("un nombre largo no estira la fila");
        movil.Should().Contain(":not(.columna-cumplimiento-centro)",
            "la cabecera conserva solo la casilla de página y los dos rótulos que ordenan");
    }

    /// <summary>
    /// La fila desplegada (Trabajadores con incidencia) en una caja estrecha: la rejilla de escritorio reserva 230 px para el
    /// nombre y dejaba el estado cortado fuera de la caja de 280 px.
    /// </summary>
    [Fact]
    public void La_fila_desplegada_de_Trabajadores_tiene_su_variante_para_caja_estrecha()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "CaeManager.slnx")))
            dir = Path.GetDirectoryName(dir);
        var css = File.ReadAllText(Path.Combine(dir!, "src", "CaeManager.Web", "Features", "Centros", "Components", "AcordeonAsignacionesCentro.razor.css"));

        var inicio = css.IndexOf("@container (max-width: 520px) {", StringComparison.Ordinal);
        inicio.Should().BeGreaterThan(css.IndexOf("minmax(230px, auto) auto minmax(0, 1fr) auto", StringComparison.Ordinal),
            "la variante estrecha va después de la rejilla de escritorio del listado para ganarle por orden");
        var bloque = css[inicio..];
        bloque.Should().Contain("grid-template-columns: minmax(0, 1fr) auto");
        bloque.Should().Contain(".fila-relacion-estado");
        bloque.Should().Contain(".fila-relacion-acciones");
    }

    [Fact]
    public void La_rejilla_de_escritorio_no_cambia_fuera_del_bloque_movil()
    {
        var css = CssLista().Replace(BloqueMovil(), string.Empty);

        css.Should().Contain("grid-template-columns: 24px minmax(200px, 2fr) minmax(180px, 1.5fr) minmax(130px, 1fr) minmax(170px, 1.3fr) 40px;");
        css.Should().Contain("grid-template-columns: 28px 24px minmax(200px, 2fr) minmax(180px, 1.5fr) minmax(130px, 1fr) minmax(170px, 1.3fr) 40px;");
    }
}
