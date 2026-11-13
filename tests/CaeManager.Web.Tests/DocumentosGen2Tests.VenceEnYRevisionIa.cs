using System.Text.Json;
using AngleSharp.Dom;
using Bunit;
using CaeManager.Application.Configuracion.Commands.GuardarFiltro;
using CaeManager.Application.Configuracion.Queries;
using CaeManager.Application.Documentos.Queries.ObtenerDocumentos;
using CaeManager.Application.Documentos.Queries.ObtenerRevisionesIaPendientes;
using CaeManager.Domain.Documentos;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Components.DesignSystem;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using PaginaDocumentos = CaeManager.Web.Features.Documentos.Pages.Documentos;

namespace CaeManager.Web.Tests;

/// <summary>
/// Los filtros «Vence en» (D-21) y «Pendiente de revisión IA» del listado de Documentos, 1:1 con el lienzo
/// «Documentos · listado sin pestañas». «Vence en» viaja como número de días (relativo, nunca una fecha) en la URL, en la
/// vista recordada y en el filtro guardado; «Pendiente de revisión IA» solo existe para los roles que acceden a la
/// pestaña Revisión IA.
/// </summary>
public partial class DocumentosGen2Tests
{
    private static IElement PastillaRevisionIa(IRenderedComponent<PaginaDocumentos> cut) =>
        cut.FindAll(".barra-filtros-pastillas button.pastilla-interruptor").Single();

    private string UrlActual() => Services.GetRequiredService<NavigationManager>().Uri;

    // ------------------------------------------------------------- «Vence en»

    [Fact]
    public async Task Vence_en_ofrece_cualquier_fecha_y_los_plazos_relativos_del_lienzo()
    {
        var (cut, _) = Renderizar(ConOpcionesDeFiltro());

        var pastilla = PastillaDocumentoFase1(cut, "Vence en");
        await pastilla.ClickAsync(new MouseEventArgs());
        var panelId = PastillaDocumentoFase1(cut, "Vence en").GetAttribute("aria-controls")!;

        cut.Find("#" + panelId).QuerySelectorAll("[role=menuitemradio]").Select(b => b.TextContent.Trim())
            .Should().Equal("Cualquier fecha", "7 días", "30 días", "90 días");
    }

    [Fact]
    public async Task Vence_en_viaja_como_dias_en_la_consulta_y_en_la_URL_y_su_chip_lo_quita()
    {
        var (cut, mediador) = Renderizar(ConOpcionesDeFiltro());
        cut.WaitForAssertion(() => mediador.Enviadas.OfType<ObtenerDocumentosQuery>().Should().NotBeEmpty());
        UltimaConsultaDeDocumentos(mediador).VenceEnDias.Should().BeNull("control: sin elegir nada no se pide");

        await ElegirPastillaDocumentoFase1(cut, "Vence en", "30 días");

        cut.WaitForAssertion(() => UltimaConsultaDeDocumentos(mediador).VenceEnDias.Should().Be(30));
        UltimaConsultaDeDocumentos(mediador).FechaVencimientoHasta.Should().BeNull("el servidor calcula la fecha con su reloj: la página no manda fechas");
        UrlActual().Should().Contain("VenceEn=30");
        var chip = cut.FindAll(".chip-filtro").Single(c => c.TextContent.Contains("Vence en: 30 días"));
        PastillaDocumentoFase1(cut, "Vence en").GetAttribute("aria-label").Should().Be("Vence en: 30 días");

        await chip.QuerySelector("button[aria-label='Quitar filtro de vencimiento']")!.ClickAsync(new MouseEventArgs());

        cut.WaitForAssertion(() => UltimaConsultaDeDocumentos(mediador).VenceEnDias.Should().BeNull());
        UrlActual().Should().NotContain("VenceEn");
        cut.FindAll(".chip-filtro").Should().NotContain(c => c.TextContent.Contains("Vence en"));
    }

    [Theory]
    [InlineData("7", 7)]
    [InlineData("30", 30)]
    [InlineData("90", 90)]
    [InlineData("15", null)]
    [InlineData("0", null)]
    [InlineData("-30", null)]
    [InlineData("30.0", null)]
    [InlineData("2026-12-31", null)]
    [InlineData("treinta", null)]
    public void El_enlace_con_VenceEn_pide_esos_dias_y_descarta_lo_que_no_es_un_plazo_del_menu(string valor, int? esperado)
    {
        var (cut, mediador) = Renderizar(ConOpcionesDeFiltro(), url: $"documentos?VenceEn={valor}");
        cut.WaitForAssertion(() => mediador.Enviadas.OfType<ObtenerDocumentosQuery>().Should().NotBeEmpty());

        UltimaConsultaDeDocumentos(mediador).VenceEnDias.Should().Be(esperado);
        cut.FindAll(".chip-filtro").Any(c => c.TextContent.Contains("Vence en")).Should().Be(esperado is not null);
    }

    // ------------------------------------------------------------- «Pendiente de revisión IA»

    [Theory]
    [InlineData("Administrador")]
    [InlineData("DireccionCae")]
    [InlineData("CoordinadorCae")]
    [InlineData("GestorCae")]
    public async Task Los_roles_que_acceden_a_la_pestana_Revision_IA_ven_la_pastilla_y_filtran_con_ella(string rol)
    {
        var (cut, mediador) = Renderizar(ConOpcionesDeFiltro(), rol: rol);
        cut.WaitForAssertion(() => mediador.Enviadas.OfType<ObtenerDocumentosQuery>().Should().NotBeEmpty());
        var pastilla = PastillaRevisionIa(cut);
        pastilla.TextContent.Trim().Should().Be("Pendiente de revisión IA");
        pastilla.GetAttribute("aria-pressed").Should().Be("false");
        UltimaConsultaDeDocumentos(mediador).PendienteDeRevisionIa.Should().BeFalse();

        await pastilla.ClickAsync(new MouseEventArgs());

        cut.WaitForAssertion(() => UltimaConsultaDeDocumentos(mediador).PendienteDeRevisionIa.Should().BeTrue());
        PastillaRevisionIa(cut).GetAttribute("aria-pressed").Should().Be("true");
        UrlActual().Should().Contain("PendienteRevisionIa=1");
        var chip = cut.FindAll(".chip-filtro").Single(c => c.TextContent.Contains("Pendiente de revisión IA"));

        await chip.QuerySelector("button[aria-label='Quitar filtro de revisión IA']")!.ClickAsync(new MouseEventArgs());

        cut.WaitForAssertion(() => UltimaConsultaDeDocumentos(mediador).PendienteDeRevisionIa.Should().BeFalse());
        PastillaRevisionIa(cut).GetAttribute("aria-pressed").Should().Be("false");
        UrlActual().Should().NotContain("PendienteRevisionIa");
    }

    /// <summary>
    /// El Usuario de Cliente (rol Cliente) y Consulta no acceden a la pestaña Revisión IA: no ven la pastilla, y un enlace
    /// con el parámetro ni pinta el chip ni pide el filtro.
    /// </summary>
    [Theory]
    [InlineData("Cliente")]
    [InlineData("Consulta")]
    public void Cliente_y_Consulta_no_ven_Pendiente_de_revision_IA_ni_siquiera_con_el_enlace(string rol)
    {
        var (cut, mediador) = Renderizar(ConOpcionesDeFiltro(), url: "documentos?PendienteRevisionIa=1", rol: rol);
        cut.WaitForAssertion(() => mediador.Enviadas.OfType<ObtenerDocumentosQuery>().Should().NotBeEmpty());

        cut.FindAll("button.pastilla-interruptor").Should().BeEmpty();
        cut.Markup.Should().NotContain("Pendiente de revisión IA");
        mediador.Enviadas.OfType<ObtenerDocumentosQuery>().Should().OnlyContain(q => !q.PendienteDeRevisionIa);
        PastillaDocumentoFase1(cut, "Vence en").Should().NotBeNull("control: el resto de la barra sí está");
    }

    [Theory]
    [InlineData("1", true)]
    [InlineData("0", false)]
    [InlineData("true", false)]
    [InlineData("si", false)]
    public void El_enlace_con_PendienteRevisionIa_solo_vale_con_uno(string valor, bool esperado)
    {
        var (cut, mediador) = Renderizar(ConOpcionesDeFiltro(), url: $"documentos?PendienteRevisionIa={valor}");
        cut.WaitForAssertion(() => mediador.Enviadas.OfType<ObtenerDocumentosQuery>().Should().NotBeEmpty());

        UltimaConsultaDeDocumentos(mediador).PendienteDeRevisionIa.Should().Be(esperado);
        PastillaRevisionIa(cut).GetAttribute("aria-pressed").Should().Be(esperado ? "true" : "false");
    }

    /// <summary>
    /// La pantalla y el servidor usan los mismos cuatro roles: si alguien toca los de la pantalla (los de la pestaña) sin
    /// tocar <see cref="ColaRevisionIa.RolesQueAccedenALaPestana"/>, el filtro se ofrecería a quien el servidor ignora.
    /// </summary>
    [Fact]
    public void Los_roles_del_filtro_en_el_servidor_son_los_de_gestion_documental_de_la_pantalla()
    {
        ColaRevisionIa.RolesQueAccedenALaPestana.Should().BeEquivalentTo(
            [Roles.Administrador, Roles.DireccionCae, Roles.CoordinadorCae, Roles.GestorCae]);

        // Y esos son los de la constante que decide si la pantalla ofrece la pastilla.
        var deLaPantalla = (string)typeof(PaginaDocumentos)
            .GetField("RolesDeGestionDocumental", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .GetRawConstantValue()!;
        ColaRevisionIa.RolesQueAccedenALaPestana.Should().BeEquivalentTo(deLaPantalla.Split(','));
    }

    /// <summary>«Exportar esta vista» lleva los dos filtros: el Excel trae lo que se ve, no todo el alcance.</summary>
    [Fact]
    public void Exportar_esta_vista_lleva_Vence_en_y_Pendiente_de_revision_IA()
    {
        var (cut, _) = Renderizar(ConOpcionesDeFiltro(), url: "documentos?VenceEn=30&PendienteRevisionIa=1");

        var vista = cut.Find("header.cabecera-pagina a[data-keytip=X]").GetAttribute("href");

        vista.Should().StartWith("/documentos/exportar.xlsx?").And.Contain("venceEn=30").And.Contain("pendienteRevisionIa=1");
    }

    [Fact]
    public void Exportar_esta_vista_no_lleva_Pendiente_de_revision_IA_a_un_rol_que_no_la_tiene()
    {
        var (cut, _) = Renderizar(ConOpcionesDeFiltro(), url: "documentos?VenceEn=30&PendienteRevisionIa=1", rol: "Cliente");

        var vista = cut.Find("header.cabecera-pagina a[data-keytip=X]").GetAttribute("href");

        vista.Should().Contain("venceEn=30").And.NotContain("pendienteRevisionIa");
    }

    // ------------------------------------------------------------- columna «Revisión IA» (artboard Filtro-revision-ia)

    private static DocumentoListaDto ConConfianza(string tipoDocumento, int? confianza) =>
        Documento(tipoDocumento) with { ConfianzaRevisionIa = confianza };

    private static MediadorControlado ConConfianzas() =>
        ConDocumentos(ConConfianza("Aptitud médica", 58), ConConfianza("Certificado TGSS", 72), ConConfianza("Formación Art. 19", 96));

    private static List<string> CabecerasDeLaTabla(IRenderedComponent<PaginaDocumentos> cut) =>
        cut.FindAll("table.documentos-tabla-visible thead th").Select(th => th.TextContent.Trim()).ToList();

    [Theory]
    [InlineData("GestorCae")]
    [InlineData("Administrador")]
    public void Con_el_filtro_la_tabla_gana_la_columna_Revision_IA_con_la_barra_y_Confianza_N_por_ciento_en_cada_fila(string rol)
    {
        var (cut, mediador) = Renderizar(ConConfianzas(), url: "documentos?PendienteRevisionIa=1", rol: rol);
        cut.WaitForAssertion(() => cut.FindAll(".documentos-confianza").Should().HaveCount(3));

        var cabeceras = CabecerasDeLaTabla(cut);
        cabeceras.Should().Contain("Revisión IA");
        cabeceras.IndexOf("Revisión IA").Should().Be(cabeceras.Count - 2, "va tras el estado y antes de las acciones, como en el artboard");
        cut.FindAll(".documentos-confianza").Select(c => c.TextContent.Trim())
            .Should().Equal("Confianza 58 %", "Confianza 72 %", "Confianza 96 %");
        cut.FindAll(".documentos-confianza-barra > i").Select(i => i.GetAttribute("style"))
            .Should().Equal("width:58%", "width:72%", "width:96%");
        cut.FindAll(".documentos-confianza-barra > i").Select(i => i.ClassList.Single(c => c.StartsWith("documentos-confianza-relleno-")))
            .Should().Equal("documentos-confianza-relleno-peligro", "documentos-confianza-relleno-advertencia", "documentos-confianza-relleno-exito");
        cut.FindAll("table.documentos-tabla-visible tbody tr:not(.documentos-fila-hijos)").Should().OnlyContain(
            f => f.QuerySelectorAll("td").Length == cabeceras.Count, "una celda por cabecera: la columna no descuadra las filas");
        UltimaConsultaDeDocumentos(mediador).PendienteDeRevisionIa.Should().BeTrue();
    }

    [Fact]
    public void Sin_el_filtro_no_hay_columna_Revision_IA_aunque_el_servidor_mande_confianza()
    {
        var (cut, mediador) = Renderizar(ConConfianzas(), rol: "GestorCae");
        cut.WaitForAssertion(() => mediador.Enviadas.OfType<ObtenerDocumentosQuery>().Should().NotBeEmpty());
        cut.WaitForAssertion(() => cut.FindAll("table.documentos-tabla-visible tbody tr").Should().NotBeEmpty());

        CabecerasDeLaTabla(cut).Should().NotContain("Revisión IA");
        cut.FindAll(".documentos-confianza").Should().BeEmpty();
        cut.Markup.Should().NotContain("Confianza 58");
    }

    /// <summary>
    /// Consulta y Usuario de Cliente que fuerzan el enlace: la pantalla no aplica el filtro y no pinta la columna, aunque una respuesta
    /// del servidor llevara confianza (defensa en profundidad: el servidor tampoco la manda).
    /// </summary>
    [Theory]
    [InlineData("Consulta")]
    [InlineData("Cliente")]
    public void Consulta_y_Cliente_no_ven_la_columna_Revision_IA_ni_con_el_enlace(string rol)
    {
        var (cut, mediador) = Renderizar(ConConfianzas(), url: "documentos?PendienteRevisionIa=1", rol: rol);
        cut.WaitForAssertion(() => mediador.Enviadas.OfType<ObtenerDocumentosQuery>().Should().NotBeEmpty());
        cut.WaitForAssertion(() => cut.FindAll("table.documentos-tabla-visible tbody tr").Should().NotBeEmpty());

        CabecerasDeLaTabla(cut).Should().NotContain("Revisión IA");
        cut.FindAll(".documentos-confianza").Should().BeEmpty();
        cut.Markup.Should().NotContain("Confianza 58");
    }

    [Fact]
    public void Una_fila_sin_confianza_deja_la_celda_vacia_con_el_filtro_aplicado()
    {
        var (cut, _) = Renderizar(ConDocumentos(ConConfianza("Aptitud médica", null)), url: "documentos?PendienteRevisionIa=1", rol: "GestorCae");
        cut.WaitForAssertion(() => cut.FindAll("td.documentos-celda-revision-ia").Should().HaveCount(1));

        cut.Find("td.documentos-celda-revision-ia").TextContent.Trim().Should().BeEmpty();
    }

    /// <summary>El <c>colgroup</c> de las agrupaciones suma la columna antes de las acciones y no toca los demás anchos.</summary>
    [Fact]
    public void El_colgroup_de_la_agrupacion_por_Plataforma_CAE_gana_la_columna_sin_mover_los_demas_anchos()
    {
        var (cut, _) = Renderizar(ConConfianzas(), url: "documentos?agrupar=plataforma&PendienteRevisionIa=1", rol: "GestorCae");
        cut.WaitForAssertion(() => CabecerasDeLaTabla(cut).Should().Contain("Revisión IA"));

        var anchos = cut.FindAll("table.documentos-tabla-visible colgroup col").Select(c => c.GetAttribute("style")!.Trim()).ToList();

        // Los siete anchos del artboard Agrupar-plataforma, con la columna nueva (190 px) entre «Estado en la plataforma» y las acciones.
        anchos.Should().Equal("width: 14%", "width: 23%", "width: 13%", "width: 10%", "width: 12%", "width: 15%", "width: 190px", "width: 232px");
        CabecerasDeLaTabla(cut).Should().HaveCount(anchos.Count, "una columna del colgroup por cabecera");
    }

    // ------------------------------------------------------------- «Limpiar», vista recordada y filtro guardado

    [Fact]
    public async Task Limpiar_todo_quita_Vence_en_y_Pendiente_de_revision_IA_de_la_URL_y_de_la_consulta()
    {
        var (cut, mediador) = Renderizar(ConOpcionesDeFiltro(), url: "documentos?VenceEn=90&PendienteRevisionIa=1");
        cut.WaitForAssertion(() => UltimaConsultaDeDocumentos(mediador).Should().Match<ObtenerDocumentosQuery>(
            q => q.VenceEnDias == 90 && q.PendienteDeRevisionIa, "control positivo: los dos llegan"));

        await cut.InvokeAsync(() => cut.FindComponent<BarraFiltros>().Instance.OnLimpiarTodo.InvokeAsync());

        cut.WaitForAssertion(() => UltimaConsultaDeDocumentos(mediador).Should().Match<ObtenerDocumentosQuery>(
            q => q.VenceEnDias == null && !q.PendienteDeRevisionIa));
        UrlActual().Should().NotContain("VenceEn").And.NotContain("PendienteRevisionIa");
        cut.FindAll(".chip-filtro").Should().BeEmpty();
    }

    [Fact]
    public void Sin_parametros_restaura_Vence_en_y_Pendiente_de_revision_IA_de_la_vista_recordada()
    {
        var recordada = new Dictionary<string, string> { ["VenceEn"] = "30", ["PendienteRevisionIa"] = "1", ["Estado"] = "Vencido" };
        var mediador = ConVistaRecordada(JsonSerializer.Serialize(recordada));

        var (cut, _) = Renderizar(mediador);

        cut.WaitForAssertion(() => ParametrosDeLaUrl().Should().BeEquivalentTo(recordada));
        cut.WaitForAssertion(() => UltimaConsultaDeDocumentos(mediador).Should().Match<ObtenerDocumentosQuery>(
            q => q.VenceEnDias == 30 && q.PendienteDeRevisionIa));
    }

    [Fact]
    public void La_vista_recordada_de_un_rol_sin_acceso_no_activa_Pendiente_de_revision_IA()
    {
        var mediador = ConVistaRecordada(JsonSerializer.Serialize(new Dictionary<string, string> { ["PendienteRevisionIa"] = "1", ["VenceEn"] = "7" }));

        var (cut, _) = Renderizar(mediador, rol: "Cliente");

        cut.WaitForAssertion(() => UltimaConsultaDeDocumentos(mediador).VenceEnDias.Should().Be(7, "control: «Vence en» sí se restaura"));
        mediador.Enviadas.OfType<ObtenerDocumentosQuery>().Should().OnlyContain(q => !q.PendienteDeRevisionIa);
    }

    [Fact]
    public async Task Guardar_filtro_guarda_Vence_en_como_dias_y_Pendiente_de_revision_IA()
    {
        var (cut, mediador) = Renderizar(ConOpcionesDeFiltro(), url: "documentos?VenceEn=30&PendienteRevisionIa=1");
        cut.WaitForAssertion(() => UltimaConsultaDeDocumentos(mediador).VenceEnDias.Should().Be(30));

        await GuardarFiltroDocumentoFase1(cut);
        var campoNombre = cut.FindComponents<CampoTexto>().Single(c => c.Instance.Etiqueta == "Nombre");
        await cut.InvokeAsync(() => campoNombre.Instance.ValorChanged.InvokeAsync("Por vencer y por revisar"));
        await cut.FindAll(".modal-pie button").Single(b => b.TextContent.Trim() == "Guardar").ClickAsync(new MouseEventArgs());

        var guardado = mediador.Enviadas.OfType<GuardarFiltroCommand>().Should().ContainSingle().Subject;
        guardado.ValoresJson.Should().Contain("\"VenceEnDias\":30", "se guardan los días, nunca una fecha")
            .And.Contain("\"RevisionIa\":true");
        guardado.ValoresJson.Should().NotMatchRegex(@"\d{4}-\d{2}-\d{2}");
    }

    [Fact]
    public async Task Un_filtro_guardado_con_un_tipo_que_no_encaja_no_rompe_la_pantalla_ni_cambia_la_vista()
    {
        var roto = new FiltroGuardadoDto(Guid.NewGuid(), "Roto", "{\"VenceEnDias\":\"30\",\"RevisionIa\":\"si\"}", DateTime.UtcNow);
        var mediador = ConOpcionesDeFiltro();
        mediador.Interceptar = p => p is ObtenerFiltrosGuardadosQuery ? Task.FromResult<object?>(new[] { roto }) : null;
        var (cut, _) = Renderizar(mediador, url: "documentos?Estado=Vencido");
        cut.WaitForAssertion(() => PastillaDocumentoFase1(cut, "Filtros guardados"));

        await PastillaDocumentoFase1(cut, "Filtros guardados").ClickAsync(new MouseEventArgs());
        await cut.FindAll(".barra-filtros-pastillas .menu-filtro-guardado [role=menuitem]")
            .First(b => b.TextContent.Trim() == "Roto").ClickAsync(new MouseEventArgs());

        UltimaConsultaDeDocumentos(mediador).Estados.Should().Equal([EstadoDocumento.Vencido], "la vista no cambió");
        UltimaConsultaDeDocumentos(mediador).VenceEnDias.Should().BeNull();
    }

    [Fact]
    public async Task Aplicar_un_filtro_guardado_pone_Vence_en_desde_hoy_y_uno_anterior_a_D_21_lo_quita()
    {
        var nuevo = new FiltroGuardadoDto(Guid.NewGuid(), "Por vencer", "{\"VenceEnDias\":30,\"RevisionIa\":true}", DateTime.UtcNow);
        var invalido = new FiltroGuardadoDto(Guid.NewGuid(), "Plazo raro", "{\"VenceEnDias\":15}", DateTime.UtcNow);
        var antiguo = new FiltroGuardadoDto(Guid.NewGuid(), "Vencidos", "{\"Busqueda\":null,\"Ambito\":null,\"Estado\":\"Vencido\"}", DateTime.UtcNow);
        var mediador = ConOpcionesDeFiltro();
        mediador.Interceptar = p => p is ObtenerFiltrosGuardadosQuery
            ? Task.FromResult<object?>(new[] { nuevo, invalido, antiguo })
            : null;
        var (cut, _) = Renderizar(mediador);

        await AplicarGuardado("Por vencer");

        cut.WaitForAssertion(() => UltimaConsultaDeDocumentos(mediador).Should().Match<ObtenerDocumentosQuery>(
            q => q.VenceEnDias == 30 && q.PendienteDeRevisionIa));
        UrlActual().Should().Contain("VenceEn=30").And.Contain("PendienteRevisionIa=1");

        await AplicarGuardado("Plazo raro");

        cut.WaitForAssertion(() => UltimaConsultaDeDocumentos(mediador).Should().Match<ObtenerDocumentosQuery>(
            q => q.VenceEnDias == null && !q.PendienteDeRevisionIa, "un plazo que el menú no ofrece se descarta"));

        await AplicarGuardado("Por vencer");
        cut.WaitForAssertion(() => UltimaConsultaDeDocumentos(mediador).VenceEnDias.Should().Be(30, "control: vuelve a ponerse"));

        await AplicarGuardado("Vencidos");

        cut.WaitForAssertion(() => UltimaConsultaDeDocumentos(mediador).Estados.Should().Equal(EstadoDocumento.Vencido));
        UltimaConsultaDeDocumentos(mediador).Should().Match<ObtenerDocumentosQuery>(q => q.VenceEnDias == null && !q.PendienteDeRevisionIa,
            "el filtro antiguo no los traía");
        UrlActual().Should().NotContain("VenceEn").And.NotContain("PendienteRevisionIa");

        async Task AplicarGuardado(string nombre)
        {
            await cut.FindAll(".menu-acciones-disparador").Single(d => d.GetAttribute("aria-label") == "Filtros guardados")
                .ClickAsync(new MouseEventArgs());
            await cut.FindAll(".barra-filtros-pastillas .menu-filtro-guardado [role=menuitem]")
                .First(b => b.TextContent.Trim() == nombre).ClickAsync(new MouseEventArgs());
        }
    }
}
