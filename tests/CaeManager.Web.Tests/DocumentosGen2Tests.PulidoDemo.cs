using Bunit;
using CaeManager.Application.Documentos.Queries.ObtenerDocumentos;
using CaeManager.Domain.Documentos;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using CaeManager.Domain.Common;

namespace CaeManager.Web.Tests;

/// <summary>
/// Pulido de la pantalla Documentos para la demo: una sola barra de pestañas, el motivo del estado sin repetir lo que
/// «Vence» ya dice, ninguna línea vacía bajo el propietario, «Ver» como enlace frente a «Gestionar», y una altura de
/// fila que no depende del número de plataformas.
/// </summary>
public partial class DocumentosGen2Tests
{
    private static string EtiquetaDePestana(AngleSharp.Dom.IElement pestana) =>
        string.Concat(pestana.ChildNodes.Where(n => n.NodeType == AngleSharp.Dom.NodeType.Text).Select(n => n.TextContent)).Trim();

    [Theory]
    [InlineData("Administrador", true)]
    [InlineData("DireccionCae", true)]
    [InlineData("CoordinadorCae", true)]
    [InlineData("GestorCae", true)]
    [InlineData("Consulta", true)]
    [InlineData("Cliente", false)]
    public void La_pagina_tiene_una_sola_barra_de_pestanas_con_Revision_IA_una_vez_e_Incidencias_segun_rol(string rol, bool conIncidencias)
    {
        var (cut, _) = Renderizar(ConDocumentos(DocumentoDeFila()), rol: rol);
        cut.WaitForAssertion(() => cut.Find("tbody button.enlace-nombre-fila"));

        cut.FindAll("[role=tablist]").Should().ContainSingle("la página lleva una sola tira de pestañas; que el marco no pinte la suya sobre /documentos lo fija BarraSeccionTests");
        cut.FindComponents<CaeManager.Web.Features.Documentos.Components.BarraPestanasDocumentos>().Should().ContainSingle(
            "la tira es el componente compartido de la sección, el mismo que pintan Incidencias y la pantalla heredada de Revisión IA");
        BarraPestanasDocumentosTests.Activa(cut).Should().Be("Estado");
        var etiquetas = Pestanas(cut).Select(EtiquetaDePestana).ToList();
        var conRevisionIa = rol is not ("Consulta" or "Cliente");
        etiquetas.Count(e => e == "Revisión IA").Should().Be(conRevisionIa ? 1 : 0);
        etiquetas.Should().StartWith(conRevisionIa
            ? ["Estado", "Plataformas CAE", "Reclamaciones", "Preventivo", "Revisión IA", "Plantillas"]
            : ["Estado", "Plataformas CAE", "Reclamaciones", "Preventivo", "Plantillas"]);
        etiquetas.Contains("Incidencias").Should().Be(conIncidencias);
        if (conIncidencias)
            etiquetas.Last().Should().Be("Incidencias");
    }

    [Fact]
    public async Task Incidencias_sale_a_su_pantalla_sin_cambiar_la_pestana_activa()
    {
        var (cut, _) = Renderizar(ConDocumentos(DocumentoDeFila()));
        cut.WaitForAssertion(() => cut.Find("tbody button.enlace-nombre-fila"));

        await Pestana(cut, "Incidencias").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        Services.GetRequiredService<NavigationManager>().Uri.Should().EndWith("/incidencias");
        Pestana(cut, "Estado").GetAttribute("aria-selected").Should().Be("true");
    }

    [Theory]
    [InlineData(-88, EstadoDocumento.Vencido)]
    [InlineData(-3, EstadoDocumento.EnTolerancia)]
    [InlineData(5, EstadoDocumento.Urgente)]
    public void El_relativo_de_la_fecha_sale_una_vez_en_Vence_y_no_se_repite_en_Estado(int dias, EstadoDocumento estado)
    {
        var documento = DocumentoDeFila() with { FechaVencimiento = DiaDeNegocio.Hoy().AddDays(dias), Estado = estado };
        var (cut, _) = Renderizar(ConDocumentos(documento));
        cut.WaitForAssertion(() => cut.Find(".documentos-vigencia-subtitulo"));

        cut.Find(".documentos-vigencia-subtitulo").TextContent.Should().NotBeNullOrWhiteSpace();
        cut.FindAll("tbody tr .estado-fila-motivo").Should().BeEmpty("«Vence» ya dice cuánto hace o cuánto falta");
        cut.Find("tbody tr .col-estado").TextContent.Should().NotContain("hace").And.NotContain("Caduca en");
    }

    [Fact]
    public async Task Ordenada_por_emision_el_motivo_vuelve_a_Estado_porque_Vence_ya_no_lo_dice()
    {
        var documento = DocumentoDeFila() with { FechaVencimiento = DiaDeNegocio.Hoy().AddDays(-88), Estado = EstadoDocumento.Vencido };
        var (cut, _) = Renderizar(ConDocumentos(documento));
        await ElegirOrdenDocumentoFase1(cut, "FechaEmision");

        cut.WaitForAssertion(() => cut.Find("tbody tr .estado-fila-motivo").TextContent.Should().Contain("88"));
    }

    [Fact]
    public void Sin_confirmar_sin_fecha_el_motivo_sigue_diciendo_que_falta()
    {
        var documento = DocumentoDeFila() with { FechaVencimiento = null, Estado = EstadoDocumento.SinConfirmar };
        var (cut, _) = Renderizar(ConDocumentos(documento));

        cut.WaitForAssertion(() => cut.Find("tbody tr .estado-fila-motivo").TextContent.Should().NotBeNullOrWhiteSpace());
    }

    [Fact]
    public void Sin_plataformas_la_celda_del_propietario_no_pinta_una_segunda_linea_vacia()
    {
        var (cut, _) = Renderizar(ConDocumentos(DocumentoDeFila()));
        cut.WaitForAssertion(() => cut.Find("tbody button.enlace-nombre-fila"));

        var celda = cut.Find("tbody tr .documentos-propietario-nombre").ParentElement!;
        celda.Children.Should().ContainSingle("solo el nombre del propietario");
        cut.Find("tbody tr").TextContent.Should().NotContain("—");
        cut.FindAll("tbody tr .detalle-tecnico").Should().BeEmpty();
    }

    [Fact]
    public void Con_varias_plataformas_la_fila_pinta_una_linea_por_Plataforma_CAE_con_el_estado_capitalizado()
    {
        // D-22 y artboard Main: «logo Nombre · Validada», una línea por Plataforma CAE, el estado con inicial en mayúscula.
        var documento = DocumentoDeFila("blob/x.pdf",
            new AcreditacionResumenDto(Guid.NewGuid(), "Nalanda", EstadoAcreditacion.Aceptada),
            new AcreditacionResumenDto(Guid.NewGuid(), "Dokify", EstadoAcreditacion.Rechazada),
            new AcreditacionResumenDto(Guid.NewGuid(), "CTAIMA", EstadoAcreditacion.PendienteDeSubir));
        var (cut, _) = Renderizar(ConDocumentos(documento));
        cut.WaitForAssertion(() => cut.Find("tbody .documentos-plats"));

        var lineas = cut.FindAll("tbody tr .documentos-plats .documentos-plat").Select(l => l.TextContent.Trim()).ToList();
        lineas.Should().HaveCount(3);
        lineas.Single(l => l.Contains("Nalanda")).Should().Contain("Nalanda").And.Contain("· Validada");
        lineas.Single(l => l.Contains("Dokify")).Should().Contain("· Rechazada");
        lineas.Single(l => l.Contains("CTAIMA")).Should().Contain("· Pendiente de subir");
        cut.FindAll("tbody tr .documentos-plats .documentos-plat-estado").Should().OnlyContain(e => char.IsUpper(e.TextContent.Replace("· ", "")[0]));
        cut.FindAll("tbody tr .documentos-plats .documentos-en-plataforma-motivo, tbody tr .documentos-plats .documentos-abrir-portal")
            .Should().BeEmpty("la línea no lleva motivo ni «Abrir portal»: lo ve igual el Usuario de Cliente");
    }
    [Fact]
    public void Ver_es_un_enlace_y_Gestionar_conserva_su_caja_como_accion_principal()
    {
        var (cut, _) = Renderizar(ConDocumentos(DocumentoDeFila()));
        cut.WaitForAssertion(() => cut.Find("tbody .documentos-accion-rapida"));

        var ver = cut.FindAll("tbody .documentos-acciones-fila button").Single(b => b.TextContent.Trim() == "Ver");
        var gestionar = cut.Find("tbody .documentos-accion-rapida");
        ver.ClassList.Should().Contain("accion-fila-enlace");
        gestionar.ClassList.Should().NotContain("accion-fila-enlace").And.Contain("boton-secundario");
        cut.Find("tbody .documentos-acciones-fila").Children.Select(c => c.TextContent.Trim())
            .Take(2).Should().Equal("Ver", "Gestionar");
    }


    [Fact]
    public void Sin_confirmar_conserva_su_motivo_aunque_el_documento_traiga_fecha()
    {
        var documento = DocumentoDeFila() with { FechaVencimiento = DiaDeNegocio.Hoy().AddDays(-4), Estado = EstadoDocumento.SinConfirmar };
        var (cut, _) = Renderizar(ConDocumentos(documento));

        cut.WaitForAssertion(() => cut.Find("tbody tr .estado-fila-motivo").TextContent.Should().NotBeNullOrWhiteSpace());
    }
}