using CaeManager.Application.Alertas.Queries.ObtenerAlertas;
using CaeManager.Application.Bandeja.Queries.ObtenerBandejaAgrupada;
using CaeManager.Application.Bandeja.Queries.ObtenerBandejaGestor;
using CaeManager.Application.Bandeja.Queries.ObtenerMiTrabajoAgregado;
using CaeManager.Application.Documentos.Queries.ObtenerAcreditacionesPorProveedor;
using CaeManager.Domain.Documentos;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Bandeja;

/// <summary>
/// Cubre solo lo que <see cref="ObtenerBandejaGestorQueryHandlerTests"/> no
/// cubre ya: los dos buckets nuevos de Mi trabajo Gen2
/// (<see cref="TipoItemBandeja.VencimientoProximo"/>,
/// <see cref="TipoItemBandeja.EnPlataformaSeguimiento"/>) y la severidad
/// «Bloqueo» (<c>EsBloqueo</c>, que para los tipos que dependen del Centro de
/// Trabajo delega en <c>BloqueaAccesoAlCentro</c>). No repite la fusión ni
/// el agrupado — esos ya están probados donde viven.
/// </summary>
public class ObtenerMiTrabajoAgregadoQueryHandlerTests
{
    private static AlertaDto Alerta(EstadoDocumento estado, DateOnly? fecha = null) => new(
        DocumentoId: Guid.NewGuid(), TrabajadorId: Guid.NewGuid(), TrabajadorNombre: "Ana García",
        TipoDocumentoId: Guid.NewGuid(), TipoDocumentoNombre: "Apto médico", FechaVencimiento: fecha,
        Estado: estado, ArchivoUrl: null, CentroNombre: "Centro Norte");

    private static ProveedorAcreditacionesDto PendientePlataforma(EstadoAcreditacion estado) => new(
        ProveedorPlataformaCaeId: Guid.NewGuid(), ProveedorNombre: "Dokify", ProveedorCodigo: "dokify",
        Clientes: [new ClienteAcreditacionesDto(
            ClienteId: Guid.NewGuid(), ClienteNombre: "Cliente Norte S.A.",
            Documentos: [new AcreditacionDrillDownDto(
                AcreditacionId: Guid.NewGuid(), DocumentoId: Guid.NewGuid(), PropietarioNombre: "Iker Etxeberria",
                TipoDocumentoNombre: "Formación 60h", Estado: estado, UltimoMotivoRechazo: null,
                TrabajadorId: Guid.NewGuid())])]);

    [Fact]
    public void MapearProximos_solo_incluye_alertas_en_estado_Proximo()
    {
        var proximo = Alerta(EstadoDocumento.Proximo, new DateOnly(2026, 10, 1));

        var resultado = ObtenerMiTrabajoAgregadoQueryHandler.MapearProximos(
            [proximo, Alerta(EstadoDocumento.Vencido), Alerta(EstadoDocumento.Faltante)]);

        var item = resultado.Should().ContainSingle().Subject;
        item.Tipo.Should().Be(TipoItemBandeja.VencimientoProximo);
        item.Fecha.Should().Be(new DateOnly(2026, 10, 1));
        item.TrabajadorId.Should().Be(proximo.TrabajadorId);
    }

    [Fact]
    public void MapearProximos_vacio_cuando_no_hay_alertas_Proximo()
    {
        ObtenerMiTrabajoAgregadoQueryHandler.MapearProximos([Alerta(EstadoDocumento.Vencido)]).Should().BeEmpty();
    }

    [Fact]
    public void MapearSeguimiento_solo_incluye_acreditaciones_Subida()
    {
        var subida = PendientePlataforma(EstadoAcreditacion.Subida);

        var resultado = ObtenerMiTrabajoAgregadoQueryHandler.MapearSeguimiento(
            [subida, PendientePlataforma(EstadoAcreditacion.PendienteDeSubir), PendientePlataforma(EstadoAcreditacion.Rechazada)]);

        var item = resultado.Should().ContainSingle().Subject;
        item.Tipo.Should().Be(TipoItemBandeja.EnPlataformaSeguimiento);
        item.ProveedorNombre.Should().Be("Dokify");
        item.ClienteNombre.Should().Be("Cliente Norte S.A.");
        item.Titulo.Should().Be("Formación 60h");
    }

    [Fact]
    public void MapearSeguimiento_vacio_cuando_no_hay_subidas()
    {
        ObtenerMiTrabajoAgregadoQueryHandler.MapearSeguimiento(
            [PendientePlataforma(EstadoAcreditacion.PendienteDeSubir)]).Should().BeEmpty();
    }

    [Fact]
    public void EsBloqueo_es_falso_para_un_requisito_pendiente_de_alta_nueva()
    {
        var item = new ItemBandejaDto(
            Id: "r1", Tipo: TipoItemBandeja.RequisitoPendiente, Titulo: "PSS", Subtitulo: "Centro",
            TrabajadorId: null, CentroId: null, DocumentoId: null, TipoDocumentoId: null, RequisitoId: Guid.NewGuid(),
            Fecha: null, EsAltaNueva: true);

        ObtenerMiTrabajoAgregadoQueryHandler.EsBloqueo(item).Should().BeFalse();
    }

    [Fact]
    public void EsBloqueo_es_verdadero_para_un_requisito_pendiente_que_no_es_alta_nueva()
    {
        var item = new ItemBandejaDto(
            Id: "r2", Tipo: TipoItemBandeja.RequisitoPendiente, Titulo: "PSS", Subtitulo: "Centro",
            TrabajadorId: null, CentroId: null, DocumentoId: null, TipoDocumentoId: null, RequisitoId: Guid.NewGuid(),
            Fecha: null, EsAltaNueva: false);

        ObtenerMiTrabajoAgregadoQueryHandler.EsBloqueo(item).Should().BeTrue();
    }

    [Theory]
    [InlineData(TipoItemBandeja.Faltante, true)]
    [InlineData(TipoItemBandeja.Vencido, true)]
    [InlineData(TipoItemBandeja.PlataformaVencida, true)]
    [InlineData(TipoItemBandeja.SugerenciaVisitaUrgente, true)]
    [InlineData(TipoItemBandeja.Urgente, false)]
    [InlineData(TipoItemBandeja.VisitaUrgente, false)]
    [InlineData(TipoItemBandeja.RevisionIa, false)]
    [InlineData(TipoItemBandeja.DeteccionPendiente, false)]
    [InlineData(TipoItemBandeja.PlataformaPendiente, false)]
    public void EsBloqueo_clasifica_cada_tipo_sin_dependencia_del_Centro(TipoItemBandeja tipo, bool esperado)
    {
        var item = new ItemBandejaDto(
            Id: "x", Tipo: tipo, Titulo: "T", Subtitulo: "S",
            TrabajadorId: null, CentroId: null, DocumentoId: null, TipoDocumentoId: null, RequisitoId: null, Fecha: null);

        ObtenerMiTrabajoAgregadoQueryHandler.EsBloqueo(item).Should().Be(esperado);
    }

    private static ItemBandejaDto Rechazada(bool bloqueaCentro) => new(
        Id: "rech", Tipo: TipoItemBandeja.PlataformaRechazada, Titulo: "Formación 60h", Subtitulo: "Iker Etxeberria",
        TrabajadorId: Guid.NewGuid(), CentroId: Guid.NewGuid(), DocumentoId: Guid.NewGuid(), TipoDocumentoId: Guid.NewGuid(),
        RequisitoId: null, Fecha: null, RechazoBloqueaCentro: bloqueaCentro);

    /// <summary>
    /// P2.7, residual de #809 (D-7): una Rechazada que el cálculo de estado de
    /// su Centro de Trabajo no cuenta como causa bloqueante —otro canal,
    /// Trabajador desvinculado, tipo que no aplica— es trabajo, no un bloqueo.
    /// Antes, EsBloqueo contaba como bloqueo cualquier Rechazada, y Mi trabajo
    /// contradecía a /bandeja y a Centro 360.
    /// </summary>
    [Fact]
    public void EsBloqueo_es_falso_para_una_Rechazada_no_aplicable_a_su_Centro()
    {
        ObtenerMiTrabajoAgregadoQueryHandler.EsBloqueo(Rechazada(bloqueaCentro: false)).Should().BeFalse();
    }

    [Fact]
    public void EsBloqueo_es_verdadero_para_una_Rechazada_que_bloquea_su_Centro()
    {
        ObtenerMiTrabajoAgregadoQueryHandler.EsBloqueo(Rechazada(bloqueaCentro: true)).Should().BeTrue();
    }

    /// <summary>
    /// Mi trabajo y la cola agrupada no pueden discrepar: para los dos tipos
    /// que dependen del Centro, la severidad «Bloqueo» es exactamente el
    /// «bloquea acceso» de <c>BloqueaAccesoAlCentro</c>.
    /// </summary>
    [Theory]
    [InlineData(TipoItemBandeja.PlataformaRechazada, false, false)]
    [InlineData(TipoItemBandeja.PlataformaRechazada, true, false)]
    [InlineData(TipoItemBandeja.RequisitoPendiente, false, false)]
    [InlineData(TipoItemBandeja.RequisitoPendiente, false, true)]
    public void EsBloqueo_coincide_con_BloqueaAccesoAlCentro_en_los_tipos_que_dependen_del_Centro(
        TipoItemBandeja tipo, bool rechazoBloqueaCentro, bool esAltaNueva)
    {
        var item = new ItemBandejaDto(
            Id: "c", Tipo: tipo, Titulo: "T", Subtitulo: "S",
            TrabajadorId: null, CentroId: Guid.NewGuid(), DocumentoId: Guid.NewGuid(), TipoDocumentoId: null, RequisitoId: null, Fecha: null,
            EsAltaNueva: esAltaNueva, RechazoBloqueaCentro: rechazoBloqueaCentro);

        ObtenerMiTrabajoAgregadoQueryHandler.EsBloqueo(item)
            .Should().Be(ObtenerBandejaAgrupadaQueryHandler.BloqueaAccesoAlCentro(item));
    }

    private static readonly DateOnly Hoy = new(2026, 9, 23);

    private static ProveedorAcreditacionesDto Acreditacion(
        EstadoAcreditacion estado, EstadoVigenciaEnPlataforma vigencia, DateOnly? vence, Guid? documentoId = null) => new(
        ProveedorPlataformaCaeId: Guid.NewGuid(), ProveedorNombre: "Dokify", ProveedorCodigo: "dokify",
        Clientes: [new ClienteAcreditacionesDto(
            ClienteId: Guid.NewGuid(), ClienteNombre: "Cliente Norte S.A.",
            Documentos: [new AcreditacionDrillDownDto(
                AcreditacionId: Guid.NewGuid(), DocumentoId: documentoId ?? Guid.NewGuid(), PropietarioNombre: "Iker Etxeberria",
                TipoDocumentoNombre: "Formación 60h", Estado: estado, UltimoMotivoRechazo: null,
                TrabajadorId: Guid.NewGuid(), CentroId: Guid.NewGuid(),
                EstadoVigencia: vigencia, FechaVencimientoEnPlataforma: vence)])]);

    private static ProveedorAcreditacionesDto AceptadaQueVence(DateOnly vence, Guid? documentoId = null) =>
        Acreditacion(EstadoAcreditacion.Aceptada, EstadoVigenciaEnPlataforma.VenceEnFecha, vence, documentoId);

    /// <summary>
    /// P12 (2026-09-23): la acreditación aceptada cuya vigencia en la plataforma
    /// ya venció entra en Mi trabajo como bloqueo, con su plataforma, su fecha y
    /// el documento afectado, y la acción va a la acreditación.
    /// </summary>
    [Fact]
    public void MapearVencidasEnPlataforma_incluye_la_aceptada_con_la_vigencia_vencida()
    {
        var vencida = AceptadaQueVence(Hoy.AddDays(-1));
        var acreditacion = vencida.Clientes.Single().Documentos.Single();

        var item = ObtenerMiTrabajoAgregadoQueryHandler.MapearVencidasEnPlataforma([vencida], [], Hoy)
            .Should().ContainSingle().Subject;

        item.Tipo.Should().Be(TipoItemBandeja.PlataformaVencida);
        item.Id.Should().Be($"plataforma-vencida-{acreditacion.AcreditacionId}");
        item.DocumentoId.Should().Be(acreditacion.DocumentoId);
        item.CentroId.Should().Be(acreditacion.CentroId);
        item.Fecha.Should().Be(Hoy.AddDays(-1));
        item.ProveedorNombre.Should().Be("Dokify");
        item.ClienteNombre.Should().Be("Cliente Norte S.A.");
        ObtenerMiTrabajoAgregadoQueryHandler.EsBloqueo(item).Should().BeTrue();
    }

    /// <summary>La vigencia vale hasta su fecha inclusive: el mismo día aún vale.</summary>
    [Fact]
    public void MapearVencidasEnPlataforma_excluye_la_vigente_y_la_que_vence_hoy()
    {
        ObtenerMiTrabajoAgregadoQueryHandler.MapearVencidasEnPlataforma(
            [AceptadaQueVence(Hoy.AddDays(30)), AceptadaQueVence(Hoy)], [], Hoy).Should().BeEmpty();
    }

    /// <summary>«Sin confirmar» es no saberlo, no estar vencida; «no vence aquí» no vence.</summary>
    [Fact]
    public void MapearVencidasEnPlataforma_excluye_la_vigencia_sin_confirmar_y_la_que_no_vence()
    {
        ObtenerMiTrabajoAgregadoQueryHandler.MapearVencidasEnPlataforma(
            [
                Acreditacion(EstadoAcreditacion.Aceptada, EstadoVigenciaEnPlataforma.SinConfirmar, null),
                Acreditacion(EstadoAcreditacion.Aceptada, EstadoVigenciaEnPlataforma.NoVenceAqui, null),
            ],
            [], Hoy).Should().BeEmpty();
    }

    /// <summary>
    /// Una Rechazada o pendiente de subir ya tiene su propio ítem, y una Subida
    /// sigue en Seguimiento: una acreditación no puede dar dos ítems.
    /// </summary>
    [Theory]
    [InlineData(EstadoAcreditacion.Rechazada)]
    [InlineData(EstadoAcreditacion.PendienteDeSubir)]
    [InlineData(EstadoAcreditacion.Subida)]
    [InlineData(EstadoAcreditacion.NoRequerida)]
    public void MapearVencidasEnPlataforma_solo_toma_aceptadas(EstadoAcreditacion estado)
    {
        ObtenerMiTrabajoAgregadoQueryHandler.MapearVencidasEnPlataforma(
            [Acreditacion(estado, EstadoVigenciaEnPlataforma.VenceEnFecha, Hoy.AddDays(-5))], [], Hoy).Should().BeEmpty();
    }

    /// <summary>
    /// Si el Documento ya está vencido en TALVEG, la cola ya trae ese bloqueo y
    /// la acción es renovarlo: la acreditación vencida no se duplica. Un aviso
    /// que no es Vencido (Urgente) no la oculta.
    /// </summary>
    [Fact]
    public void MapearVencidasEnPlataforma_deduplica_con_el_vencimiento_documental()
    {
        var documentoVencido = Guid.NewGuid();
        var documentoUrgente = Guid.NewGuid();
        var alertas = new[]
        {
            Alerta(EstadoDocumento.Vencido) with { DocumentoId = documentoVencido },
            Alerta(EstadoDocumento.Urgente) with { DocumentoId = documentoUrgente },
        };

        var resultado = ObtenerMiTrabajoAgregadoQueryHandler.MapearVencidasEnPlataforma(
            [AceptadaQueVence(Hoy.AddDays(-1), documentoVencido), AceptadaQueVence(Hoy.AddDays(-1), documentoUrgente)],
            alertas, Hoy);

        resultado.Should().ContainSingle().Which.DocumentoId.Should().Be(documentoUrgente);
    }

    /// <summary>
    /// Prioridad alta, la de Vencido (D-6): por delante de una Rechazada y de
    /// un Requisito pendiente, por detrás de un Faltante.
    /// </summary>
    [Fact]
    public void Ordenar_pone_la_vencida_en_plataforma_con_la_prioridad_de_Vencido()
    {
        ItemBandejaDto De(TipoItemBandeja tipo, string id, DateOnly? fecha = null) => new(
            Id: id, Tipo: tipo, Titulo: "T", Subtitulo: "S", Fecha: fecha,
            TrabajadorId: null, CentroId: null, DocumentoId: null, TipoDocumentoId: null, RequisitoId: null);

        var ordenados = ObtenerBandejaGestorQueryHandler.Ordenar(
        [
            De(TipoItemBandeja.RequisitoPendiente, "requisito"),
            De(TipoItemBandeja.PlataformaRechazada, "rechazada"),
            De(TipoItemBandeja.Vencido, "vencido", Hoy.AddDays(-1)),
            De(TipoItemBandeja.PlataformaVencida, "plataforma-vencida", Hoy.AddDays(-3)),
            De(TipoItemBandeja.Faltante, "faltante"),
        ]);

        ordenados.Select(i => i.Id).Should().Equal("faltante", "plataforma-vencida", "vencido", "rechazada", "requisito");
    }

    private static readonly Guid EmpresaPropia = Guid.NewGuid();
    private static readonly Guid Subcontrata = Guid.NewGuid();

    private static readonly Dictionary<Guid, (string RazonSocial, bool EsPropia)> Empresas = new()
    {
        [EmpresaPropia] = ("Refrielectric", true),
        [Subcontrata] = ("Montajes Pérez", false),
    };

    private static ItemBandejaDto ItemDe(TipoItemBandeja tipo, Guid? empresaId, Guid? trabajadorId = null, string? empresaNombre = null) => new(
        Id: Guid.NewGuid().ToString(), Tipo: tipo, Titulo: "Seguro RC", Subtitulo: "Sujeto",
        TrabajadorId: trabajadorId, CentroId: null, DocumentoId: null, TipoDocumentoId: null, RequisitoId: null, Fecha: null,
        EmpresaId: empresaId, EmpresaNombre: empresaNombre);

    [Fact]
    public void MarcarEmpresaSujeto_distingue_la_Empresa_propia_de_la_Subcontrata_y_rellena_su_nombre()
    {
        var marcados = ObtenerMiTrabajoAgregadoQueryHandler.MarcarEmpresaSujeto(
            [ItemDe(TipoItemBandeja.PlataformaPendiente, EmpresaPropia), ItemDe(TipoItemBandeja.EnPlataformaSeguimiento, Subcontrata)],
            Empresas);

        marcados.Select(i => (i.EmpresaEsPropia, i.EmpresaNombre)).Should().Equal(
            (true, "Refrielectric"), (false, "Montajes Pérez"));
    }

    [Fact]
    public void MarcarEmpresaSujeto_no_pisa_un_nombre_de_Empresa_que_ya_venia()
    {
        var marcado = ObtenerMiTrabajoAgregadoQueryHandler.MarcarEmpresaSujeto(
            [ItemDe(TipoItemBandeja.RevisionIa, EmpresaPropia, empresaNombre: "Refrielectric S.L.")], Empresas).Single();

        marcado.EmpresaNombre.Should().Be("Refrielectric S.L.");
        marcado.EmpresaEsPropia.Should().BeTrue();
    }

    [Fact]
    public void MarcarEmpresaSujeto_deja_sin_marcar_las_tareas_cuyo_sujeto_es_una_persona()
    {
        var marcados = ObtenerMiTrabajoAgregadoQueryHandler.MarcarEmpresaSujeto(
            [
                ItemDe(TipoItemBandeja.PlataformaPendiente, EmpresaPropia, trabajadorId: Guid.NewGuid()),
                // Alta o baja detectada: lleva EmpresaId sin TrabajadorId, pero el sujeto es la persona.
                ItemDe(TipoItemBandeja.DeteccionPendiente, EmpresaPropia),
                ItemDe(TipoItemBandeja.PlataformaPendiente, Guid.NewGuid()),
            ],
            Empresas);

        marcados.Should().OnlyContain(i => i.EmpresaEsPropia == null, "ni persona, ni detección, ni Empresa desconocida");
    }
}
