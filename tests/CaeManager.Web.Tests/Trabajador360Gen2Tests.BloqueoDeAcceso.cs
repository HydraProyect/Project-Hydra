using System.Globalization;
using Bunit;
using CaeManager.Web.Features.Trabajadores.Pages;
using CaeManager.Application.Centros.Queries.ObtenerDocumentacionBloqueantePendiente;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// #2005 (inventario de efectos sobre el Trabajador): la ficha del Trabajador dice, por cada Centro, lo mismo que las pantallas
/// del Centro (<c>TextoBloqueoDeAcceso</c>): «fin de tolerancia» y «sin subir a la plataforma», no solo el estado genérico del
/// documento. La regla sigue siendo la única por Centro; la ficha solo la dice.
/// </summary>
public partial class Trabajador360Gen2Tests
{
    private (Guid Id, MediatorFalso Mediador, CaeManager.Application.Trabajadores.Queries.ObtenerDocumentacionPorCentroDeTrabajador.CentroDocumentacionTrabajadorDto Centro)
        ConBloqueo(SituacionDeRequisitoBloqueante situacion, EstadoDocumento estado, int toleranciaDias, DateOnly? vencimiento)
    {
        var id = Guid.NewGuid();
        var mediador = Registrar(new MediatorFalso());
        mediador.Detalles[id] = Detalle(id, "Javier", "Salas Moreno");
        var documento = Documento("Formación PRL", estado);
        var centro = Centro("Centro Norte", "Refrielectric S.A.", estado, documento);
        mediador.Centros[id] = [centro];
        mediador.Bloqueos.Add(new DocumentacionBloqueantePendienteDto(
            centro.CentroId, centro.CentroNombre, id, "Salas Moreno, Javier", documento.TipoDocumentoId, documento.TipoDocumentoNombre,
            Ambito: AmbitoAplicacion.Trabajador, Situacion: situacion, ToleranciaDias: toleranciaDias, VencimientoEfectivo: vencimiento));
        return (id, mediador, centro);
    }

    private static void AbrirCentroYSusDocumentos(IRenderedComponent<TrabajadorDetalle> cut)
    {
        cut.FindAll("[role=tab]").Single(t => t.FirstChild!.TextContent.Trim() is "Centros" or "Centres").Click();
        cut.Find(".trabajador360-centros .fila-relacion-desplegar").Click();
    }

    [Fact]
    public void La_ficha_dice_el_fin_de_tolerancia_del_Centro_con_el_texto_de_las_pantallas_del_Centro()
    {
        var (id, _, _) = ConBloqueo(SituacionDeRequisitoBloqueante.Vencido, EstadoDocumento.Vencido, 15, new DateOnly(2026, 9, 2));

        var cut = Renderizar(id);
        AbrirCentroYSusDocumentos(cut);

        cut.Find(".trabajador360-bloqueo-acceso").TextContent.Should().Be("venció el 02/09/2026 y la tolerancia (15 d) terminó");
    }

    [Fact]
    public void La_ficha_dice_que_el_documento_esta_sin_subir_a_la_plataforma_del_Centro()
    {
        var (id, _, _) = ConBloqueo(SituacionDeRequisitoBloqueante.PendienteDeSubirAPlataforma, EstadoDocumento.Vigente, 0, null);

        var cut = Renderizar(id);
        AbrirCentroYSusDocumentos(cut);

        cut.Find(".trabajador360-bloqueo-acceso").TextContent.Should().Be("sin subir a la plataforma del Centro");
    }

    [Fact]
    public void La_ficha_dice_el_bloqueo_en_catalan_con_el_texto_de_las_pantallas_del_Centro()
    {
        var anterior = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("ca-ES");
            var (id, _, _) = ConBloqueo(SituacionDeRequisitoBloqueante.Vencido, EstadoDocumento.Vencido, 15, new DateOnly(2026, 9, 2));

            var cut = Renderizar(id);
            AbrirCentroYSusDocumentos(cut);

            cut.Find(".trabajador360-bloqueo-acceso").TextContent.Should().Be("va vèncer el 02/09/2026 i la tolerància (15 d) ha acabat");
        }
        finally
        {
            CultureInfo.CurrentUICulture = anterior;
        }
    }

    private DocumentacionBloqueantePendienteDto BloqueoAjeno(
        Guid centroId, Guid trabajadorId, Guid tipoId, AmbitoAplicacion ambito = AmbitoAplicacion.Trabajador) =>
        new(centroId, "Centro Norte", trabajadorId, "Otro, Trabajador", tipoId, "Formación PRL",
            Ambito: ambito, Situacion: SituacionDeRequisitoBloqueante.PendienteDeSubirAPlataforma);

    [Fact]
    public void Solo_se_pinta_el_bloqueo_de_este_Trabajador_en_este_Centro_para_este_Tipo_y_ambito_Trabajador()
    {
        var (id, mediador, centro) = ConBloqueo(SituacionDeRequisitoBloqueante.Ausente, EstadoDocumento.Faltante, 0, null);
        var tipo = centro.Documentos[0].TipoDocumentoId;
        // Todos distintos del bloqueo propio: otro Trabajador del mismo Centro y Tipo, otro Tipo, otro Centro y un documento de Empresa.
        mediador.Bloqueos.Clear();
        mediador.Bloqueos.Add(BloqueoAjeno(centro.CentroId, Guid.NewGuid(), tipo));
        mediador.Bloqueos.Add(BloqueoAjeno(centro.CentroId, id, Guid.NewGuid()));
        mediador.Bloqueos.Add(BloqueoAjeno(Guid.NewGuid(), id, tipo));
        mediador.Bloqueos.Add(BloqueoAjeno(centro.CentroId, id, tipo, AmbitoAplicacion.Empresa));

        var cut = Renderizar(id);
        AbrirCentroYSusDocumentos(cut);

        cut.FindAll(".trabajador360-bloqueo-acceso").Should().BeEmpty();
    }

    [Fact]
    public void La_ventana_de_incidencias_de_la_cabecera_dice_el_fin_de_tolerancia_del_Centro()
    {
        var (id, _, _) = ConBloqueo(SituacionDeRequisitoBloqueante.Vencido, EstadoDocumento.Vencido, 15, new DateOnly(2026, 9, 2));

        var cut = Renderizar(id);

        cut.Find(".cabecera-identidad").TextContent.Should().Contain("Formación PRL — venció el 02/09/2026 y la tolerancia (15 d) terminó");
    }

    [Fact]
    public void Si_la_consulta_de_bloqueos_falla_la_ficha_se_pinta_igual_sin_esa_linea()
    {
        var (id, mediador, _) = ConBloqueo(SituacionDeRequisitoBloqueante.Vencido, EstadoDocumento.Vencido, 15, new DateOnly(2026, 9, 2));
        mediador.FallarBloqueos = true;

        var cut = Renderizar(id);
        AbrirCentroYSusDocumentos(cut);

        cut.Markup.Should().Contain("Formación PRL");
        cut.FindAll(".trabajador360-bloqueo-acceso").Should().BeEmpty();
    }

    [Fact]
    public void Un_documento_que_no_bloquea_en_ese_Centro_no_lleva_texto_de_bloqueo()
    {
        var id = Guid.NewGuid();
        var mediador = Registrar(new MediatorFalso());
        mediador.Detalles[id] = Detalle(id, "Javier", "Salas Moreno");
        mediador.Centros[id] = [Centro("Centro Norte", "Refrielectric S.A.", EstadoDocumento.Vigente, Documento("Formación PRL", EstadoDocumento.Vigente))];

        var cut = Renderizar(id);
        AbrirCentroYSusDocumentos(cut);

        cut.FindAll(".trabajador360-bloqueo-acceso").Should().BeEmpty();
    }
}
