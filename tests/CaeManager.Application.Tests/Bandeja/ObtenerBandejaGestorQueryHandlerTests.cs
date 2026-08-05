using CaeManager.Application.Alertas.Queries.ObtenerAlertas;
using CaeManager.Application.Bandeja.Queries.ObtenerBandejaGestor;
using CaeManager.Application.Comunicaciones.Queries.ObtenerSugerenciasVisitaCorreoPendientes;
using CaeManager.Application.Documentos.Queries.ObtenerRevisionesIaPendientes;
using CaeManager.Application.RequisitosDocumentales.Queries.ObtenerRequisitosDocumentalesPendientes;
using CaeManager.Application.Visitas.Queries.ObtenerVisitas;
using CaeManager.Domain.Comunicaciones;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Visitas;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Bandeja;

public class ObtenerBandejaGestorQueryHandlerTests
{
    private static readonly DateOnly Hoy = new(2026, 7, 15);
    private const int HorasAvisoVisita = 48;
    private const int HorasCriticasVisita = 24;

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

    private static VisitaListaDto Visita(NivelUrgenciaVisita nivel, DateOnly? fechaInicio = null) => new(
        Id: Guid.NewGuid(), CentroId: Guid.NewGuid(), CentroNombre: "Centro Este",
        ClienteId: Guid.NewGuid(), ClienteRazonSocial: "Cliente Este S.A.",
        EmpresaId: Guid.NewGuid(), EmpresaRazonSocial: "Empresa Este S.L.",
        FechaInicio: fechaInicio ?? Hoy.AddDays(1), FechaFin: fechaInicio ?? Hoy.AddDays(1),
        TotalTrabajadores: 2, DocumentacionCompleta: true, NotificadoCliente: false,
        Origen: OrigenVisita.Plataforma, NivelUrgencia: nivel);

    private static SugerenciaVisitaCorreoPendienteDto Sugerencia(DateOnly? fechaInicioSugerida) => new(
        Id: Guid.NewGuid(), CentroId: Guid.NewGuid(), CentroNombre: "Centro Oeste",
        FechaInicioSugerida: fechaInicioSugerida, Resumen: "Pide entrar mañana con dos operarios",
        Canal: CanalConversacion.Correo);

    private static IReadOnlyList<ItemBandejaDto> Fusionar(
        IReadOnlyList<AlertaDto>? alertas = null,
        IReadOnlyList<RevisionIaDocumentoDto>? revisiones = null,
        IReadOnlyList<RequisitoDocumentalPendienteDto>? requisitos = null,
        IReadOnlyList<VisitaListaDto>? visitasUrgentes = null,
        IReadOnlyList<SugerenciaVisitaCorreoPendienteDto>? sugerenciasVisita = null) =>
        ObtenerBandejaGestorQueryHandler.Fusionar(
            alertas ?? [], revisiones ?? [], requisitos ?? [], visitasUrgentes ?? [], sugerenciasVisita ?? [],
            Hoy, HorasAvisoVisita, HorasCriticasVisita);

    [Fact]
    public void Excluye_las_alertas_en_estado_Proximo()
    {
        var resultado = Fusionar(alertas: [Alerta(EstadoDocumento.Proximo), Alerta(EstadoDocumento.Vencido)]);

        resultado.Should().ContainSingle();
        resultado[0].Tipo.Should().Be(TipoItemBandeja.Vencido);
    }

    [Fact]
    public void Ordena_por_prioridad_completa()
    {
        var resultado = Fusionar(
            alertas: [Alerta(EstadoDocumento.Urgente), Alerta(EstadoDocumento.Vencido), Alerta(EstadoDocumento.Faltante)],
            revisiones: [Revision()],
            requisitos: [Requisito()],
            visitasUrgentes: [Visita(NivelUrgenciaVisita.Urgente)],
            sugerenciasVisita: [Sugerencia(fechaInicioSugerida: null)]);

        resultado.Select(i => i.Tipo).Should().Equal(
            TipoItemBandeja.SugerenciaVisitaUrgente,
            TipoItemBandeja.Faltante,
            TipoItemBandeja.Vencido,
            TipoItemBandeja.VisitaUrgente,
            TipoItemBandeja.RequisitoPendiente,
            TipoItemBandeja.Urgente,
            TipoItemBandeja.RevisionIa);
    }

    [Fact]
    public void Una_alerta_faltante_o_vencida_se_ordena_por_fecha_de_vencimiento_dentro_del_mismo_tipo()
    {
        var masCercana = Alerta(EstadoDocumento.Vencido, new DateOnly(2026, 1, 1));
        var masLejana = Alerta(EstadoDocumento.Vencido, new DateOnly(2026, 6, 1));

        var resultado = Fusionar(alertas: [masLejana, masCercana]);

        resultado[0].Fecha.Should().Be(new DateOnly(2026, 1, 1));
        resultado[1].Fecha.Should().Be(new DateOnly(2026, 6, 1));
    }

    [Fact]
    public void Mapea_una_revision_Ia_con_su_titulo_subtitulo_y_documento()
    {
        var revision = Revision();

        var resultado = Fusionar(revisiones: [revision]);

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

        var resultado = Fusionar(requisitos: [requisito]);

        var item = resultado.Should().ContainSingle().Subject;
        item.Tipo.Should().Be(TipoItemBandeja.RequisitoPendiente);
        item.Titulo.Should().Be("PSS firmado");
        item.Subtitulo.Should().Be("Centro Sur");
        item.CentroId.Should().Be(requisito.CentroId);
        item.RequisitoId.Should().Be(requisito.Id);
    }

    [Fact]
    public void Solo_incluye_visitas_en_niveles_Urgente_o_Critica()
    {
        var resultado = Fusionar(visitasUrgentes:
        [
            Visita(NivelUrgenciaVisita.Normal),
            Visita(NivelUrgenciaVisita.EnCurso),
            Visita(NivelUrgenciaVisita.Urgente),
            Visita(NivelUrgenciaVisita.Critica)
        ]);

        resultado.Should().HaveCount(2);
        resultado.Should().OnlyContain(i => i.Tipo == TipoItemBandeja.VisitaUrgente);
    }

    [Fact]
    public void Una_sugerencia_sin_fecha_detectada_se_trata_como_visita_sorpresa_del_mismo_dia()
    {
        var resultado = Fusionar(sugerenciasVisita: [Sugerencia(fechaInicioSugerida: null)]);

        var item = resultado.Should().ContainSingle().Subject;
        item.Tipo.Should().Be(TipoItemBandeja.SugerenciaVisitaUrgente);
        item.SugerenciaVisitaId.Should().NotBeNull();
    }

    [Fact]
    public void Una_sugerencia_con_fecha_fuera_de_la_ventana_de_aviso_no_entra_en_la_bandeja()
    {
        var resultado = Fusionar(sugerenciasVisita: [Sugerencia(fechaInicioSugerida: Hoy.AddDays(10))]);

        resultado.Should().BeEmpty();
    }

    [Fact]
    public void Una_sugerencia_con_fecha_dentro_de_la_ventana_de_aviso_si_entra()
    {
        var resultado = Fusionar(sugerenciasVisita: [Sugerencia(fechaInicioSugerida: Hoy.AddDays(1))]);

        resultado.Should().ContainSingle();
    }
}
