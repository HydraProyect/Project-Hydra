using Bunit;
using CaeManager.Application.Documentos.Queries.ObtenerDocumentos;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// D-54 (2026-11-13): «Agrupar por tipo de documento», y el rótulo «Plataforma CAE» (D-15) y la vista recordada (D-19).
/// Lo que el listado tenía en main sin agrupar (la fila completa) se fija en <c>DocumentosGen2Tests.AgruparPorVinculo</c>.
/// </summary>
public partial class DocumentosGen2Tests
{
    [Fact]
    public async Task La_seleccion_multiple_nace_apagada_y_las_casillas_salen_solo_tras_pulsar_el_boton()
    {
        var e = ConVinculos();
        var (cut, _) = Renderizar(e.Mediador);
        cut.WaitForAssertion(() => cut.Find("tbody button.enlace-nombre-fila"));

        cut.FindAll("tbody input[type=checkbox]").Should().BeEmpty("sin pulsar el botón de la cabecera no hay casillas");
        cut.Find(".cabecera-pagina button[aria-label='Selección múltiple']").GetAttribute("aria-pressed")
            .Should().NotBe("true");

        await cut.Find(".cabecera-pagina button[aria-label='Selección múltiple']").ClickAsync(new MouseEventArgs());

        cut.FindAll("tbody input[type=checkbox]").Should().NotBeEmpty("tras pulsar el botón aparecen las casillas");
    }

    [Fact]
    public async Task Por_tipo_de_documento_la_cabecera_dice_tipo_ambito_y_numero_y_la_fila_lleva_solo_cinco_columnas()
    {
        var e = ConVinculos();
        var (cut, _) = Renderizar(e.Mediador);
        cut.WaitForAssertion(() => cut.Find("tbody button.enlace-nombre-fila"));

        await Agrupar(cut, "Por tipo de documento");

        cut.WaitForAssertion(() => cut.FindAll("tr.documentos-cabecera-grupo-tipo").Should().NotBeEmpty());
        foreach (var cabecera in cut.FindAll("tr.documentos-cabecera-grupo-tipo"))
        {
            cabecera.QuerySelector(".grupo-lista-nombre")!.TextContent.Trim().Should().NotBeEmpty();
            cabecera.QuerySelector(".grupo-lista-subtitulo")!.TextContent.Should().Contain("documento",
                "la cabecera dice el ámbito y cuántos documentos tiene el tipo");
        }
        cut.FindAll("table.documentos-tabla-visible thead th").Select(t => t.TextContent.Trim()).Where(t => t.Length > 0)
            .Should().Equal("Propietario", "Plataformas CAE", "Vence", "Estado documental", "Acciones");

        if (cut.FindAll("tbody tr:has(button.enlace-nombre-fila)").Count == 0)
        {
            await cut.Find("tr.documentos-cabecera-grupo-tipo button.grupo-lista-cabecera").ClickAsync(new MouseEventArgs());
        }
        var fila = cut.FindAll("tbody tr:has(.documentos-celda-acciones-fila)").First();
        fila.QuerySelectorAll("td").Length.Should().Be(5, "Propietario, Plataformas CAE, Vence, Estado documental y acciones");
    }

    [Fact]
    public async Task Elegir_agrupar_por_tipo_se_escribe_en_la_url_para_que_la_vista_recordada_la_restaure()
    {
        var e = ConVinculos();
        var (cut, _) = Renderizar(e.Mediador);
        cut.WaitForAssertion(() => cut.Find("tbody button.enlace-nombre-fila"));

        await Agrupar(cut, "Por tipo de documento");

        cut.WaitForAssertion(() => Services.GetRequiredService<NavigationManager>().Uri.Should().Contain("agrupar=tipo"));
    }

    [Fact]
    public void Llegar_con_agrupar_tipo_en_la_url_agrupa_por_tipo()
    {
        var (cut, _) = Renderizar(ConVinculos().Mediador, url: "documentos?agrupar=tipo");

        cut.WaitForAssertion(() => cut.FindAll("tr.documentos-cabecera-grupo-tipo").Should().NotBeEmpty());
        cut.Find("button[aria-label^='Agrupar']").GetAttribute("aria-label").Should().Be("Agrupar por: tipo de documento");
    }

    [Fact]
    public void Con_Plataforma_CAE_agrupada_el_logotipo_o_el_icono_generico_va_en_la_cabecera_del_grupo()
    {
        var (cut, _) = Renderizar(ConVinculos().Mediador, url: "documentos?agrupar=plataforma");

        cut.WaitForAssertion(() => cut.FindAll("tr.documentos-cabecera-grupo-vinculo").Should().NotBeEmpty());
        foreach (var cabecera in cut.FindAll("tr.documentos-cabecera-grupo-vinculo"))
        {
            cabecera.QuerySelector(".documentos-logo-plataforma").Should().NotBeNull(
                "logotipo si lo hay, icono genérico si no");
        }
    }
}
