using Bunit;
using CaeManager.Application.Documentos.Queries.ObtenerDocumentos;
using CaeManager.Application.Documentos.Queries.ObtenerPlataformasEnUso;
using CaeManager.Domain.Documentos;
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

    /// <summary>
    /// Por tipo: el grupo es el Id del tipo de TALVEG (dos tipos con el mismo nombre son dos grupos), van primero los que
    /// tienen vencidos, nacen abiertos solo esos, el pie cuenta documentos y tipos, la tabla es de una línea y el 360 va
    /// en la cabecera, no en la fila.
    /// </summary>
    [Fact]
    public void Por_tipo_agrupa_por_id_ordena_por_gravedad_abre_solo_el_que_tiene_vencidos_y_lleva_el_360_en_la_cabecera()
    {
        var idGrave = Guid.NewGuid();
        var idLeve = Guid.NewGuid();
        var idMismoNombre = Guid.NewGuid();
        var grave = DocumentoDeFila() with { Id = Guid.NewGuid(), TipoDocumentoId = idGrave, TipoDocumentoNombre = "Zeta seguro", Estado = EstadoDocumento.Vencido };
        var graveVigente = grave with { Id = Guid.NewGuid(), Estado = EstadoDocumento.Vigente };
        var leve = DocumentoDeFila() with { Id = Guid.NewGuid(), TipoDocumentoId = idLeve, TipoDocumentoNombre = "Alfa médico", Estado = EstadoDocumento.Vigente };
        var mismoNombre = leve with { Id = Guid.NewGuid(), TipoDocumentoId = idMismoNombre };

        var (cut, _) = Renderizar(ConDocumentos(leve, mismoNombre, graveVigente, grave), url: "documentos?agrupar=tipo");

        cut.WaitForAssertion(() => cut.FindAll("tr.documentos-cabecera-grupo-tipo").Should().HaveCount(3,
            "agrupa por el Id del tipo: «Alfa médico» con dos Ids son dos grupos"));
        var cabeceras = cut.FindAll("tr.documentos-cabecera-grupo-tipo");
        cabeceras[0].GetAttribute("data-grupo").Should().Be(idGrave.ToString(), "el de vencidos va primero, aunque llegó el último");
        cabeceras.Skip(1).Select(c => c.GetAttribute("data-grupo")).Should().BeEquivalentTo([idLeve.ToString(), idMismoNombre.ToString()]);
        cabeceras[0].QuerySelector(".grupo-lista-subtitulo")!.TextContent.Should().Contain("2 documentos");

        var filasVisibles = cut.FindAll("tbody tr:has(.documentos-celda-acciones-fila)");
        filasVisibles.Should().HaveCount(2, "solo el tipo con un documento vencido nace abierto");
        cut.Markup.Should().Contain("Mostrando 4 documentos de 3 tipos");
        cut.Find("table.documentos-tabla-por-tipo").ClassList.Should().Contain("documentos-fila-una-linea");

        cabeceras[0].QuerySelector("a.documentos-tipo-360, .documentos-tipo-360 a")!.GetAttribute("href")
            .Should().Be($"/documentos/tipos/{idGrave}", "el Tipo 360 va en la cabecera del grupo");
        filasVisibles.SelectMany(f => f.QuerySelectorAll("[href^='/documentos/tipos/']")).Should().BeEmpty(
            "el botón 360 de la fila se quita porque ya está en la cabecera");
    }

    /// <summary>La cabecera por Plataforma CAE pinta el logotipo del catálogo si la plataforma tiene código con marca, y el icono genérico si no.</summary>
    [Fact]
    public void Con_Plataforma_CAE_agrupada_el_logotipo_sale_con_codigo_con_marca_y_el_icono_generico_sin_el()
    {
        var e = ConVinculos();
        e.Mediador.Plataformas = [new PlataformaEnUsoDto(NalandaId, "Nalanda", "nalanda"), new PlataformaEnUsoDto(CtaimaId, "Ctaima")];

        var (cut, _) = Renderizar(e.Mediador, url: "documentos?agrupar=plataforma");

        cut.WaitForAssertion(() => cut.FindAll("tr.documentos-cabecera-grupo-vinculo").Should().NotBeEmpty());
        var conLogo = CabeceraDeVinculo(cut, NalandaId.ToString()).QuerySelector("img.documentos-logo-plataforma");
        conLogo.Should().NotBeNull("Nalanda tiene código con marca");
        conLogo!.GetAttribute("src").Should().Be("/img/plataformas/nalanda.jpg");
        var sinLogo = CabeceraDeVinculo(cut, CtaimaId.ToString());
        sinLogo.QuerySelector("img.documentos-logo-plataforma").Should().BeNull("sin código no hay logotipo");
        sinLogo.QuerySelector(".documentos-logo-plataforma-generico").Should().NotBeNull("icono genérico");
    }

    [Fact]
    public void Con_Plataforma_CAE_agrupada_el_logotipo_o_el_icono_generico_va_en_la_cabecera_del_grupo()
    {
        var (cut, _) = Renderizar(ConVinculos().Mediador, url: "documentos?agrupar=plataforma");

        cut.WaitForAssertion(() => cut.FindAll("tr.documentos-cabecera-grupo-vinculo").Should().NotBeEmpty());
        foreach (var cabecera in cut.FindAll("tr.documentos-cabecera-grupo-vinculo")
                     .Where(c => c.GetAttribute("data-grupo") != ObtenerDocumentosQuery.GrupoSin))
        {
            cabecera.QuerySelector(".documentos-logo-plataforma").Should().NotBeNull(
                "logotipo si lo hay, icono genérico si no");
        }
    }

    /// <summary>Artboard Agrupar-plataforma: «Sin Plataforma CAE» no lleva icono y el logotipo es un cuadrado de 22 px con borde.</summary>
    [Fact]
    public void El_grupo_Sin_Plataforma_CAE_no_lleva_icono_y_el_logotipo_es_cuadrado_con_borde()
    {
        var e = ConVinculos();
        e.Mediador.Plataformas = [new PlataformaEnUsoDto(NalandaId, "Nalanda", "nalanda")];

        var (cut, _) = Renderizar(e.Mediador, url: "documentos?agrupar=plataforma");

        cut.WaitForAssertion(() => cut.FindAll("tr.documentos-cabecera-grupo-vinculo").Should().NotBeEmpty());
        CabeceraDeVinculo(cut, ObtenerDocumentosQuery.GrupoSin).QuerySelector(".documentos-logo-plataforma").Should().BeNull(
            "el grupo «Sin Plataforma CAE» no lleva icono");
        CabeceraDeVinculo(cut, NalandaId.ToString()).QuerySelector("img.documentos-logo-plataforma")!.ClassList
            .Should().Contain("documentos-logo-plataforma-medio");
    }

    /// <summary>Anchos del colgroup de los artboards (D-54) y cabecera de grupo con insignias de tono, en femenino y con botones del dibujo.</summary>
    [Fact]
    public void Por_Plataforma_CAE_la_tabla_lleva_el_colgroup_del_artboard_y_la_cabecera_resume_en_insignias_en_femenino()
    {
        var (cut, _) = Renderizar(ConVinculos().Mediador, url: "documentos?agrupar=plataforma");

        cut.WaitForAssertion(() => cut.FindAll("tr.documentos-cabecera-grupo-vinculo").Should().NotBeEmpty());
        cut.FindAll("table.documentos-tabla-visible colgroup col").Select(c => c.GetAttribute("style")!.Replace(" ", ""))
            .Should().Equal("width:14%", "width:23%", "width:13%", "width:10%", "width:12%", "width:15%", "width:232px");

        var cabecera = CabeceraDeVinculo(cut, NalandaId.ToString());
        cabecera.QuerySelector(".grupo-lista-maqueta").Should().NotBeNull();
        var rechazada = cabecera.QuerySelectorAll(".grupo-lista-resumen-estado .badge").Single(b => b.TextContent.Contains("rechazada"));
        rechazada.ClassList.Should().Contain("badge-peligro");
        cabecera.QuerySelectorAll(".grupo-lista-punto").Should().BeEmpty("el resumen va en insignias, no en texto con punto");
    }

    [Fact]
    public void Por_Cliente_la_tabla_lleva_el_colgroup_del_artboard_y_no_dibuja_la_columna_de_destinos()
    {
        var (cut, _) = Renderizar(ConVinculos().Mediador, url: "documentos?agrupar=cliente");

        cut.WaitForAssertion(() => cut.FindAll("tr.documentos-cabecera-grupo-vinculo").Should().NotBeEmpty());
        cut.FindAll("table.documentos-tabla-visible colgroup col").Select(c => c.GetAttribute("style")!.Replace(" ", ""))
            .Should().Equal("width:30%", "width:30%", "width:13%", "width:14%", "width:232px");
        cut.FindAll("table.documentos-tabla-visible .documentos-celda-expandir").Should().BeEmpty("el artboard no dibuja la columna chevron «Destino»");
    }

    [Fact]
    public void Por_tipo_la_cabecera_va_en_una_linea_y_la_tabla_lleva_el_colgroup_del_artboard()
    {
        var (cut, _) = Renderizar(ConVinculos().Mediador, url: "documentos?agrupar=tipo");

        cut.WaitForAssertion(() => cut.FindAll("tr.documentos-cabecera-grupo-tipo").Should().NotBeEmpty());
        cut.FindAll("table.documentos-tabla-visible colgroup col").Select(c => c.GetAttribute("style")!.Replace(" ", ""))
            .Should().Equal("width:24%", "width:30%", "width:13%", "width:14%", "width:190px");
        foreach (var cabecera in cut.FindAll("tr.documentos-cabecera-grupo-tipo"))
        {
            cabecera.QuerySelector(".grupo-lista-bloque").Should().BeNull("nombre y «Trabajador · 3 documentos» van en la misma línea");
            cabecera.QuerySelector(".grupo-lista-subtitulo-linea").Should().NotBeNull();
        }
    }

    /// <summary>D-54: el propietario cortado con «…» lleva el nombre entero en el tooltip.</summary>
    [Fact]
    public void Al_agrupar_el_propietario_de_la_fila_lleva_su_nombre_entero_en_el_tooltip()
    {
        var documento = DocumentoDeFila() with { Id = Guid.NewGuid(), PropietarioNombre = "Instalaciones y Montajes Eléctricos del Cantábrico S.A.", Estado = EstadoDocumento.Vencido,
            DestinosEnCentros = [new(Guid.NewGuid(), "Centro Norte", "Empresa de prueba", "Nalanda", EstadoDocumento.Vencido)] };

        var (cut, _) = Renderizar(ConDocumentos(documento), url: "documentos?agrupar=tipo");

        cut.WaitForAssertion(() => cut.Find("tbody button.documentos-propietario-nombre"));
        cut.Find("tbody button.documentos-propietario-nombre").GetAttribute("title")
            .Should().Be("Instalaciones y Montajes Eléctricos del Cantábrico S.A.");
    }

    /// <summary>Desplegables.dc.html: el menú «Agrupar» lleva título.</summary>
    [Fact]
    public async Task El_menu_Agrupar_se_abre_con_su_titulo()
    {
        var (cut, _) = Renderizar(ConVinculos().Mediador);
        cut.WaitForAssertion(() => cut.Find("tbody button.enlace-nombre-fila"));

        await cut.Find("button[aria-label^='Agrupar']").ClickAsync(new MouseEventArgs());

        cut.Find(".menu-acciones-panel-titulado .menu-acciones-titulo").TextContent.Trim().Should().Be("Agrupar");
        cut.FindAll(".menu-acciones-panel-titulado").Should().ContainSingle("solo «Agrupar» es un panel titulado: el resto de menús conserva su aspecto");
    }

    /// <summary>
    /// Sin columna «Destino» al agrupar no puede quedar una fila hija abierta sin chevron que la contraiga: se desplegó sin agrupar
    /// y, al agrupar por tipo, la fila de Centros no se pinta.
    /// </summary>
    [Fact]
    public async Task Al_agrupar_no_quedan_filas_de_destinos_abiertas_sin_chevron_que_las_contraiga()
    {
        var documento = DocumentoDeFila() with
        {
            Id = Guid.NewGuid(),
            Estado = EstadoDocumento.Vencido,
            DestinosEnCentros = [new(Guid.NewGuid(), "Centro Norte", "Empresa de prueba", "Nalanda", EstadoDocumento.Vencido)]
        };
        var (cut, _) = Renderizar(ConDocumentos(documento));
        cut.WaitForAssertion(() => cut.Find("tbody button.documentos-expandir"));
        await cut.Find("tbody button.documentos-expandir").ClickAsync(new MouseEventArgs());
        cut.WaitForAssertion(() => cut.FindAll("tbody tr.documentos-fila-hijos").Should().ContainSingle("sin agrupar, el Documento desplegado enseña sus Centros"));

        await Agrupar(cut, "Por tipo de documento");

        cut.WaitForAssertion(() => cut.FindAll("tr.documentos-cabecera-grupo-tipo").Should().NotBeEmpty());
        cut.FindAll("tbody tr.documentos-fila-hijos").Should().BeEmpty("agrupado por tipo no hay chevron para contraerla");
    }
}
