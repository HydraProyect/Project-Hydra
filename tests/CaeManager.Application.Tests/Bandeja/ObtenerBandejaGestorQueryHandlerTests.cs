using CaeManager.Application.Alertas.Queries.ObtenerAlertas;
using CaeManager.Application.Bandeja.Queries.ObtenerBandejaGestor;
using CaeManager.Application.Documentos.Queries.ObtenerRevisionesIaPendientes;
using CaeManager.Application.RequisitosDocumentales.Queries.ObtenerRequisitosDocumentalesPendientes;
using CaeManager.Domain.Documentos;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Bandeja;

public class ObtenerBandejaGestorQueryHandlerTests
{
    private static AlertaDto Alerta(EstadoDocumento estado, DateOnly? fecha = null) => new(
        DocumentoId: Guid.NewGuid(), TrabajadorId: Guid.NewGuid(), TrabajadorNombre: "Ana García",
        TipoDocumentoId: Guid.NewGuid(), TipoDocumentoNombre: "Apto médico", FechaVencimiento: fecha,
        Estado: estado, ArchivoUrl: null, CentroNombre: "Centro Norte");

    private static RevisionIaDocumentoDto Revision() => new(
        Id: Guid.NewGuid(), DocumentoId: Guid.NewGuid(), TrabajadorNombre: "Luis Pérez",
        TipoDocumentoNombre: "EPIs", ConfianzaGeneral: 60, TipoDetectado: null,
        FechaEmisionDetectada: null, Motivo: "Confianza baja", CreadaEnUtc: DateTime.UtcNow);

    private static RequisitoDocumentalPendienteDto Requisito() => new(
        Id: Guid.NewGuid(), CentroId: Guid.NewGuid(), CentroNombre: "Centro Sur", Descripcion: "PSS firmado");

    [Fact]
    public void Excluye_las_alertas_en_estado_Proximo()
    {
        var resultado = ObtenerBandejaGestorQueryHandler.Fusionar(
            [Alerta(EstadoDocumento.Proximo), Alerta(EstadoDocumento.Vencido)], [], []);

        resultado.Should().ContainSingle();
        resultado[0].Tipo.Should().Be(TipoItemBandeja.Vencido);
    }

    [Fact]
    public void Ordena_por_prioridad_Faltante_Vencido_Requisito_Urgente_RevisionIa()
    {
        var resultado = ObtenerBandejaGestorQueryHandler.Fusionar(
            [Alerta(EstadoDocumento.Urgente), Alerta(EstadoDocumento.Vencido), Alerta(EstadoDocumento.Faltante)],
            [Revision()],
            [Requisito()]);

        resultado.Select(i => i.Tipo).Should().Equal(
            TipoItemBandeja.Faltante,
            TipoItemBandeja.Vencido,
            TipoItemBandeja.RequisitoPendiente,
            TipoItemBandeja.Urgente,
            TipoItemBandeja.RevisionIa);
    }

    [Fact]
    public void Una_alerta_faltante_o_vencida_se_ordena_por_fecha_de_vencimiento_dentro_del_mismo_tipo()
    {
        var masCercana = Alerta(EstadoDocumento.Vencido, new DateOnly(2026, 1, 1));
        var masLejana = Alerta(EstadoDocumento.Vencido, new DateOnly(2026, 6, 1));

        var resultado = ObtenerBandejaGestorQueryHandler.Fusionar([masLejana, masCercana], [], []);

        resultado[0].Fecha.Should().Be(new DateOnly(2026, 1, 1));
        resultado[1].Fecha.Should().Be(new DateOnly(2026, 6, 1));
    }

    [Fact]
    public void Mapea_una_revision_Ia_con_su_titulo_subtitulo_y_documento()
    {
        var revision = Revision();

        var resultado = ObtenerBandejaGestorQueryHandler.Fusionar([], [revision], []);

        var item = resultado.Should().ContainSingle().Subject;
        item.Tipo.Should().Be(TipoItemBandeja.RevisionIa);
        item.Titulo.Should().Be("EPIs");
        item.Subtitulo.Should().Be("Luis Pérez — Confianza baja");
        item.DocumentoId.Should().Be(revision.DocumentoId);
    }

    [Fact]
    public void Mapea_un_requisito_pendiente_con_su_centro()
    {
        var requisito = Requisito();

        var resultado = ObtenerBandejaGestorQueryHandler.Fusionar([], [], [requisito]);

        var item = resultado.Should().ContainSingle().Subject;
        item.Tipo.Should().Be(TipoItemBandeja.RequisitoPendiente);
        item.Titulo.Should().Be("PSS firmado");
        item.Subtitulo.Should().Be("Centro Sur");
        item.CentroId.Should().Be(requisito.CentroId);
        item.RequisitoId.Should().Be(requisito.Id);
    }
}
