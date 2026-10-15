using Bunit;
using CaeManager.Application.Asignaciones.Queries.ObtenerAsignacionesDocumentacionPorCentro;
using CaeManager.Application.Centros;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Common;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Features.Centros.Components;
using CaeManager.Web.Features.Documentos;
using FluentAssertions;
using Microsoft.AspNetCore.Components.Web;

namespace CaeManager.Web.Tests;

public partial class AcordeonAsignacionesCentroVigenciaSinConfirmarTests
{
    [Theory]
    [InlineData(EstadoDocumento.Vencido, 1, "Vencido", "Aptitud médica")]
    [InlineData(EstadoDocumento.SinConfirmar, 1, "Sin confirmar", "Aptitud médica")]
    [InlineData(EstadoDocumento.SinConfirmar, 3, "Sin confirmar", "3 documentos")]
    [InlineData(EstadoDocumento.Urgente, 1, "Por vencer", "1/1 · Aptitud médica en 5 días")]
    public void Modo_listado_alinea_nombre_estado_motivo_y_gestion_sin_recuento_separado(
        EstadoDocumento estado, int cantidad, string etiqueta, string motivo)
    {
        this.ConRolDeEscritura();
        var documentos = Enumerable.Range(0, cantidad).Select(_ => new DocumentoRequeridoDto(
            Guid.NewGuid(), Guid.NewGuid(), "Aptitud médica", estado,
            estado == EstadoDocumento.Urgente ? DiaDeNegocio.Hoy().AddDays(5) : null)).ToArray();
        var trabajador = new TrabajadorAsignacionDocumentacionDto(Guid.NewGuid(), Guid.NewGuid(), "Ruiz Peña, Ana",
            DiaDeNegocio.Hoy(), estado, documentos, CumplimientoDocumental.Evaluar(documentos.Select(d => d.Estado)));
        RegistrarServicios(new MediatorFalso { Trabajadores = [trabajador] });
        var cut = Render<AcordeonAsignacionesCentro>(p => p.Add(c => c.CentroId, Guid.NewGuid()).Add(c => c.ModoListado, true));
        var fila = cut.Find(".lista-trabajadores-acordeon-listado .fila-relacion");
        fila.QuerySelector(".fila-relacion-nombre")!.TextContent.Should().Be("Ana Ruiz Peña");
        fila.QuerySelector(".fila-relacion-estado .badge")!.TextContent.Trim().Should().Be(etiqueta);
        fila.QuerySelector(".fila-trabajador-motivo").Should().NotBeNull("el listado presenta un motivo compacto junto al estado");
        fila.QuerySelector(".fila-trabajador-motivo")!.TextContent.Should().Be(motivo);
        fila.QuerySelector(".fila-trabajador-motivo")!.GetAttribute("title").Should().Be(motivo);
        fila.QuerySelectorAll(".fila-trabajador-fraccion").Should().BeEmpty();
        fila.QuerySelector(".fila-trabajador-gestionar")!.TextContent.Trim().Should().Be("Gestionar");
        fila.QuerySelector(".fila-trabajador-gestionar")!.ClassList.Should().Contain("accion-fila-enlace", "la acción del listado se presenta como enlace, conservando el botón accesible");
    }

    [Theory]
    [InlineData("es-ES", "2 documentos")]
    [InlineData("ca-ES", "2 documents")]
    public void El_motivo_de_varios_documentos_esta_localizado(string idioma, string esperado)
    {
        var anterior = System.Globalization.CultureInfo.CurrentUICulture;
        try
        {
            System.Globalization.CultureInfo.CurrentUICulture = System.Globalization.CultureInfo.GetCultureInfo(idioma);
            var documentos = Enumerable.Range(0, 2).Select(_ => new DocumentoRequeridoDto(
                Guid.NewGuid(), Guid.NewGuid(), "Aptitud médica", EstadoDocumento.SinConfirmar, null)).ToArray();
            var trabajador = new TrabajadorAsignacionDocumentacionDto(Guid.NewGuid(), Guid.NewGuid(), "Ana Ruiz Peña",
                DiaDeNegocio.Hoy(), EstadoDocumento.SinConfirmar, documentos,
                CumplimientoDocumental.Evaluar(documentos.Select(d => d.Estado)));
            RegistrarServicios(new MediatorFalso { Trabajadores = [trabajador] });
            var cut = Render<AcordeonAsignacionesCentro>(p => p.Add(c => c.CentroId, Guid.NewGuid()).Add(c => c.ModoListado, true));
            cut.Find(".fila-trabajador-motivo").TextContent.Should().Be(esperado);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentUICulture = anterior;
        }
    }

    [Fact]
    public async Task Gestionar_del_listado_conserva_el_desglose_documental_existente()
    {
        this.ConRolDeEscritura();
        var trabajador = new TrabajadorAsignacionDocumentacionDto(Guid.NewGuid(), Guid.NewGuid(), "Ana Ruiz Peña",
            DiaDeNegocio.Hoy(), EstadoDocumento.Vencido,
            [new DocumentoRequeridoDto(Guid.NewGuid(), Guid.NewGuid(), "Aptitud médica", EstadoDocumento.Vencido, DiaDeNegocio.Hoy().AddDays(-2))],
            CumplimientoDocumental.Evaluar([EstadoDocumento.Vencido]));
        RegistrarServicios(new MediatorFalso { Trabajadores = [trabajador] });
        var cut = Render<AcordeonAsignacionesCentro>(p => p.Add(c => c.CentroId, Guid.NewGuid()).Add(c => c.ModoListado, true));
        await cut.Find(".fila-trabajador-gestionar").ClickAsync(new MouseEventArgs());
        cut.Find(".fila-trabajador-gestionar").GetAttribute("aria-expanded").Should().Be("true");
        cut.FindAll(".tabla-documentos-requeridos").Should().ContainSingle();
        cut.Find(".tabla-documentos-requeridos").TextContent.Should().Contain("Aptitud médica");
    }

    [Fact]
    public void Consulta_no_recibe_la_accion_de_gestion_en_el_listado()
    {
        var trabajador = new TrabajadorAsignacionDocumentacionDto(Guid.NewGuid(), Guid.NewGuid(), "Ana Ruiz Peña",
            DiaDeNegocio.Hoy(), EstadoDocumento.Vencido,
            [new DocumentoRequeridoDto(Guid.NewGuid(), Guid.NewGuid(), "Aptitud médica", EstadoDocumento.Vencido, null)],
            CumplimientoDocumental.Evaluar([EstadoDocumento.Vencido]));
        RegistrarServicios(new MediatorFalso { Trabajadores = [trabajador] });
        this.ConRolDeEscritura(Roles.Consulta);
        var cut = Render<AcordeonAsignacionesCentro>(p => p.Add(c => c.CentroId, Guid.NewGuid()).Add(c => c.ModoListado, true));
        cut.FindAll(".fila-trabajador-gestionar").Should().BeEmpty();
        cut.Find(".fila-trabajador-motivo").TextContent.Should().Be("Aptitud médica");
    }

    private static TrabajadorAsignacionDocumentacionDto TrabajadorConDocumento(DocumentoRequeridoDto documento) =>
        new(Guid.NewGuid(), Guid.NewGuid(), "Ruiz Peña, Ana", DiaDeNegocio.Hoy(), documento.Estado, [documento],
            CumplimientoDocumental.Evaluar([documento.Estado]));

    [Fact]
    public void Consulta_conserva_el_chevron_para_leer_el_desglose_en_el_listado()
    {
        RegistrarServicios(new MediatorFalso
        {
            Trabajadores = [TrabajadorConDocumento(new DocumentoRequeridoDto(
            Guid.NewGuid(), Guid.NewGuid(), "Aptitud médica", EstadoDocumento.Vencido, null))]
        });
        this.ConRolDeEscritura(Roles.Consulta);
        var cut = Render<AcordeonAsignacionesCentro>(p => p.Add(c => c.CentroId, Guid.NewGuid()).Add(c => c.ModoListado, true));
        cut.FindAll(".fila-trabajador-gestionar").Should().BeEmpty();
        cut.Find(".fila-relacion-desplegar").Click();
        cut.Find(".fila-relacion-desplegar").GetAttribute("aria-expanded").Should().Be("true");
    }

    [Fact]
    public void Un_vencido_en_tolerancia_conserva_su_ultimo_dia_en_el_motivo_compacto()
    {
        this.ConRolDeEscritura();
        var hasta = DiaDeNegocio.Hoy().AddDays(3);
        RegistrarServicios(new MediatorFalso
        {
            Trabajadores = [TrabajadorConDocumento(new DocumentoRequeridoDto(
            Guid.NewGuid(), Guid.NewGuid(), "Formación PRL", EstadoDocumento.EnTolerancia, DiaDeNegocio.Hoy().AddDays(-1),
            EnToleranciaHasta: hasta))]
        });
        var cut = Render<AcordeonAsignacionesCentro>(p => p.Add(c => c.CentroId, Guid.NewGuid()).Add(c => c.ModoListado, true));
        cut.Find(".fila-trabajador-motivo").TextContent.Should().Be(
            "Formación PRL · " + EstadoDocumentoUi.Texto(EstadoDocumento.EnTolerancia, hasta));
    }

    [Fact]
    public void Un_documento_que_vence_hoy_dice_hoy_y_no_en_0_dias()
    {
        this.ConRolDeEscritura();
        RegistrarServicios(new MediatorFalso
        {
            Trabajadores = [TrabajadorConDocumento(new DocumentoRequeridoDto(
            Guid.NewGuid(), Guid.NewGuid(), "Aptitud médica", EstadoDocumento.Urgente, DiaDeNegocio.Hoy()))]
        });
        var cut = Render<AcordeonAsignacionesCentro>(p => p.Add(c => c.CentroId, Guid.NewGuid()).Add(c => c.ModoListado, true));
        cut.Find(".fila-trabajador-motivo").TextContent.Should().Be("1/1 · Aptitud médica hoy");
    }
}
