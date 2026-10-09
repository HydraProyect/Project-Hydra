using CaeManager.Application.Alertas.Queries.ObtenerAlertas;
using CaeManager.Application.Bandeja.Queries.ObtenerBandejaAgrupada;
using CaeManager.Application.Bandeja.Queries.ObtenerBandejaGestor;
using CaeManager.Application.Bandeja.Queries.ObtenerMiTrabajoAgregado;
using CaeManager.Application.Centros;
using CaeManager.Application.Centros.Queries.ObtenerDocumentacionBloqueantePendiente;
using CaeManager.Application.Common;
using CaeManager.Application.Comunicaciones.Queries.ObtenerSugerenciasVisitaCorreoPendientes;
using CaeManager.Application.Documentos.Queries.ObtenerAcreditacionesPorProveedor;
using CaeManager.Application.Documentos.Queries.ObtenerRevisionesIaPendientes;
using CaeManager.Application.Tenants.Queries.ObtenerClientesAutorizados;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Documentos;
using CaeManager.Application.Tests.Reportes;
using CaeManager.Application.Trabajadores.Queries.ObtenerDeteccionesPendientes;
using CaeManager.Application.Visitas.Queries.ObtenerVisitas;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Configuracion;
using CaeManager.Domain.Documentos;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CaeManager.Application.Tests.Bandeja;

/// <summary>
/// Orden único de la cola (decisiones 9 y 10 del propietario, 2026-10-03):
/// todo lo que bloquea el acceso precede a lo que no lo bloquea y, dentro de
/// cada tramo, Vencido va antes que Faltante. «Bloquea el acceso» es el
/// predicado que ya marca los grupos de la cola
/// (<see cref="ObtenerBandejaAgrupadaQueryHandler.BloqueaElAcceso"/>), no la
/// severidad «Bloqueo» de Mi trabajo, que también incluye todo Vencido y todo
/// Faltante.
///
/// <para>
/// Tres propiedades: la regla pura (<see cref="ObtenerBandejaGestorQueryHandler.Ordenar"/>),
/// que la cola agrupada y Mi trabajo agregada la aplican DESPUÉS de saber qué
/// Rechazada cierra su Centro de Trabajo, y que las dos dan el mismo orden.
/// </para>
/// </summary>
public class OrdenUnicoDeLaColaTests
{
    private static readonly DateOnly Hoy = new(2026, 10, 3);

    private static ItemBandejaDto De(TipoItemBandeja tipo, string id, DateOnly? fecha = null, bool rechazoBloqueaCentro = false) => new(
        Id: id, Tipo: tipo, Titulo: "T", Subtitulo: "S", Fecha: fecha,
        TrabajadorId: null, CentroId: null, DocumentoId: null, TipoDocumentoId: null, RequisitoId: null,
        RechazoBloqueaCentro: rechazoBloqueaCentro);

    private static IEnumerable<string> IdsOrdenados(params ItemBandejaDto[] items) =>
        ObtenerBandejaGestorQueryHandler.Ordenar(items).Select(i => i.Id);

    // ------------------------------------------------------------ 1. la regla

    [Fact]
    public void Un_vencido_va_antes_que_un_faltante()
    {
        IdsOrdenados(De(TipoItemBandeja.Faltante, "faltante"), De(TipoItemBandeja.Vencido, "vencido"))
            .Should().Equal("vencido", "faltante");
    }

    [Fact]
    public void La_vencida_en_la_plataforma_va_junto_al_vencido_y_antes_que_el_faltante()
    {
        ObtenerBandejaGestorQueryHandler.Prioridad(TipoItemBandeja.PlataformaVencida)
            .Should().Be(ObtenerBandejaGestorQueryHandler.Prioridad(TipoItemBandeja.Vencido));

        // Con la misma prioridad deciden la fecha y el Id: las dos clases de vencido se intercalan.
        IdsOrdenados(
                De(TipoItemBandeja.Faltante, "faltante"),
                De(TipoItemBandeja.Vencido, "vencido-reciente", Hoy.AddDays(-1)),
                De(TipoItemBandeja.PlataformaVencida, "plataforma-vencida", Hoy.AddDays(-3)),
                De(TipoItemBandeja.Vencido, "vencido-antiguo", Hoy.AddDays(-9)))
            .Should().Equal("vencido-antiguo", "plataforma-vencida", "vencido-reciente", "faltante");
    }

    public static TheoryData<TipoItemBandeja> TodosLosTipos() => [.. Enum.GetValues<TipoItemBandeja>()];

    /// <summary>
    /// Para cada tipo de la cola, también los que se añadan después: si el ítem
    /// no bloquea el acceso, va detrás de un requisito bloqueante pendiente y de
    /// una Rechazada que cierra su Centro de Trabajo, tenga la fecha que tenga.
    /// </summary>
    [Theory]
    [MemberData(nameof(TodosLosTipos))]
    public void Todo_lo_que_bloquea_el_acceso_precede_a_cualquier_item_que_no_lo_bloquea(TipoItemBandeja tipo)
    {
        var noBloquea = De(tipo, "a-no-bloquea", Hoy.AddDays(-30));
        if (ObtenerBandejaAgrupadaQueryHandler.BloqueaElAcceso(noBloquea)) return;

        IdsOrdenados(
                noBloquea,
                De(TipoItemBandeja.RequisitoPendiente, "z-requisito"),
                De(TipoItemBandeja.PlataformaRechazada, "z-rechazada-que-bloquea", rechazoBloqueaCentro: true))
            .Should().Equal("z-rechazada-que-bloquea", "z-requisito", "a-no-bloquea");
    }

    [Fact]
    public void El_control_de_la_teoria_anterior_recorre_tipos_que_no_bloquean()
    {
        // Si BloqueaElAcceso pasara a ser verdadero para todo, la teoría anterior quedaría verde sin comprobar nada.
        Enum.GetValues<TipoItemBandeja>()
            .Count(t => !ObtenerBandejaAgrupadaQueryHandler.BloqueaElAcceso(De(t, "x")))
            .Should().BeGreaterThanOrEqualTo(10);
    }

    [Fact]
    public void Un_requisito_bloqueante_pendiente_va_antes_que_una_Visita_urgente_y_que_una_sugerencia_de_visita()
    {
        IdsOrdenados(
                De(TipoItemBandeja.SugerenciaVisitaUrgente, "sugerencia", Hoy),
                De(TipoItemBandeja.VisitaUrgente, "visita", Hoy),
                De(TipoItemBandeja.RequisitoPendiente, "requisito"))
            .Should().Equal("requisito", "sugerencia", "visita");
    }

    [Fact]
    public void Una_Rechazada_que_cierra_su_Centro_va_la_primera_y_una_que_no_lo_cierra_se_queda_en_su_prioridad()
    {
        IdsOrdenados(
                De(TipoItemBandeja.VisitaUrgente, "visita", Hoy),
                De(TipoItemBandeja.PlataformaRechazada, "rechazada-no-aplicable"),
                De(TipoItemBandeja.Faltante, "faltante"),
                De(TipoItemBandeja.Vencido, "vencido", Hoy.AddDays(-1)),
                De(TipoItemBandeja.PlataformaRechazada, "rechazada-que-bloquea", rechazoBloqueaCentro: true))
            .Should().Equal("rechazada-que-bloquea", "vencido", "faltante", "rechazada-no-aplicable", "visita");
    }

    [Fact]
    public void El_orden_completo_es_bloqueante_vencido_faltante_y_despues_el_resto()
    {
        IdsOrdenados(
                De(TipoItemBandeja.RevisionIa, "revision"),
                De(TipoItemBandeja.Urgente, "urgente", Hoy.AddDays(2)),
                De(TipoItemBandeja.RequisitoPendiente, "requisito"),
                De(TipoItemBandeja.VisitaUrgente, "visita", Hoy),
                De(TipoItemBandeja.PlataformaRechazada, "rechazada-no-aplicable"),
                De(TipoItemBandeja.Faltante, "faltante"),
                De(TipoItemBandeja.PlataformaVencida, "plataforma-vencida", Hoy.AddDays(-3)),
                De(TipoItemBandeja.Vencido, "vencido", Hoy.AddDays(-1)),
                De(TipoItemBandeja.SugerenciaVisitaUrgente, "sugerencia", Hoy),
                De(TipoItemBandeja.PlataformaRechazada, "rechazada-que-bloquea", rechazoBloqueaCentro: true))
            .Should().Equal(
                "rechazada-que-bloquea", "requisito",
                "sugerencia", "plataforma-vencida", "vencido", "faltante", "rechazada-no-aplicable", "visita", "urgente", "revision");
    }

    // ------------------------------- 2. y 3. los dos consumidores, de extremo a extremo de Application

    private static readonly Guid Cliente = Guid.NewGuid();
    private static readonly Guid Centro = Guid.NewGuid();
    private static readonly Guid DocumentoRechazadoQueBloquea = Guid.NewGuid();

    private static AlertaDto Alerta(EstadoDocumento estado, DateOnly? fecha) => new(
        DocumentoId: estado == EstadoDocumento.Faltante ? null : Guid.NewGuid(), TrabajadorId: Guid.NewGuid(), TrabajadorNombre: "Ana García",
        TipoDocumentoId: Guid.NewGuid(), TipoDocumentoNombre: "Apto médico", FechaVencimiento: fecha,
        Estado: estado, ArchivoUrl: null, CentroNombre: "Centro Norte", CentroId: Centro,
        ClienteId: Cliente, ClienteNombre: "Cervezas Duff Ibérica");

    private static AcreditacionDrillDownDto Acreditacion(
        EstadoAcreditacion estado, Guid? documentoId = null, DateOnly? venceEnPlataforma = null) => new(
        AcreditacionId: Guid.NewGuid(), DocumentoId: documentoId ?? Guid.NewGuid(), PropietarioNombre: "Homer Simpson",
        TipoDocumentoNombre: "Formación 60h", Estado: estado, UltimoMotivoRechazo: estado == EstadoAcreditacion.Rechazada ? "Ilegible" : null,
        TrabajadorId: Guid.NewGuid(), CentroId: Centro,
        EstadoVigencia: venceEnPlataforma is null ? EstadoVigenciaEnPlataforma.SinConfirmar : EstadoVigenciaEnPlataforma.VenceEnFecha,
        FechaVencimientoEnPlataforma: venceEnPlataforma, VencidaEnPlataforma: venceEnPlataforma is not null);

    /// <summary>
    /// Una sola cola de un Tenant propietario, con todo bajo el mismo Cliente
    /// empresarial para que salga en un único grupo: así el orden de sus items
    /// es el de <c>Ordenar</c> y no el de los grupos. Los datos se crean una vez:
    /// las dos consultas que se comparan leen exactamente las mismas filas.
    /// </summary>
    private sealed class ColaDeUnTenant : IMediator
    {
        private readonly ConfiguracionQueryContextFalso _configuracion = new();

        private readonly List<AlertaDto> _alertas =
        [
            Alerta(EstadoDocumento.Urgente, Hoy.AddDays(2)),
            Alerta(EstadoDocumento.Faltante, null),
            Alerta(EstadoDocumento.Vencido, Hoy.AddDays(-1)),
        ];

        private readonly List<DocumentacionBloqueantePendienteDto> _requisitos =
        [
            new(CentroId: Centro, CentroNombre: "Centro Norte", TrabajadorId: Guid.NewGuid(), TrabajadorNombre: "Ana García",
                TipoDocumentoId: Guid.NewGuid(), TipoDocumentoNombre: "PSS firmado", ClienteId: Cliente, ClienteNombre: "Cervezas Duff Ibérica"),
        ];

        private readonly List<ProveedorAcreditacionesDto> _acreditaciones =
        [
            new(ProveedorPlataformaCaeId: Guid.NewGuid(), ProveedorNombre: "Dokify", ProveedorCodigo: "dokify",
                Clientes: [new ClienteAcreditacionesDto(Cliente, "Cervezas Duff Ibérica",
                [
                    Acreditacion(EstadoAcreditacion.PendienteDeSubir),
                    Acreditacion(EstadoAcreditacion.Rechazada),
                    Acreditacion(EstadoAcreditacion.Rechazada, DocumentoRechazadoQueBloquea),
                    // Solo Mi trabajo agregada la convierte en ítem (PlataformaVencida); la cola de /bandeja la ignora.
                    Acreditacion(EstadoAcreditacion.Aceptada, venceEnPlataforma: Hoy.AddDays(-3)),
                ])]),
        ];

        public ColaDeUnTenant()
        {
            _configuracion.ListaParametrosSistema.Add(new ParametroSistema(30, 7));
        }

        public Guid Tenant { get; } = Guid.NewGuid();

        public ConfiguracionQueryContextFalso Configuracion => _configuracion;

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            if (request is ObtenerBandejaGestorQuery colaPlana)
            {
                return (Task<TResponse>)(object)new ObtenerBandejaGestorQueryHandler(this, _configuracion).Handle(colaPlana, cancellationToken);
            }

            object respuesta = request switch
            {
                ObtenerClientesAutorizadosQuery => new List<ClienteAutorizadoDto> { new(Tenant, "Tenant propietario", EsOrigen: false) },
                ObtenerAlertasQuery => _alertas,
                ObtenerRevisionesIaPendientesQuery => new List<RevisionIaDocumentoDto>(),
                ObtenerDocumentacionBloqueantePendienteQuery => _requisitos,
                ObtenerVisitasQuery => new ResultadoPaginado<VisitaListaDto>([], 0, 1, 200),
                ObtenerSugerenciasVisitaCorreoPendientesQuery => new List<SugerenciaVisitaCorreoPendienteDto>(),
                ObtenerDeteccionesPendientesQuery => new List<DeteccionPendienteDto>(),
                ObtenerAcreditacionesPorProveedorQuery => _acreditaciones,
                _ => throw new NotSupportedException(request.GetType().Name),
            };
            return Task.FromResult((TResponse)respuesta);
        }

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest => throw new NotSupportedException();
        public Task<object?> Send(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task Publish(object notification, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default) where TNotification : INotification => throw new NotSupportedException();
    }

    /// <summary>El Centro está bloqueado por UNA de las dos rechazadas; la otra no le aplica.</summary>
    private sealed class CentroBloqueadoPorUnRechazo : ICalculoEstadoCentroService
    {
        public Task<IReadOnlyDictionary<Guid, ResultadoEstadoCentro>> CalcularAsync(IReadOnlyList<Guid> centroIds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<Guid, ResultadoEstadoCentro>>(new Dictionary<Guid, ResultadoEstadoCentro>
            {
                [Centro] = new(EstadoCentro.Bloqueado,
                [
                    new CausaEstadoCentro("Formación 60h — rechazado por la plataforma", null, Bloqueante: true, AmbitoCausa.Trabajador,
                        DocumentoRechazadoQueBloquea, Guid.NewGuid(), FechaVencimiento: null),
                ]),
            });

        public Task<IReadOnlyDictionary<Guid, FraccionCumplimiento>> CalcularCumplimientoAsync(IReadOnlyList<Guid> centroIds, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<ParDocumentalExigido>> ObtenerParesExigidosAsync(IReadOnlyList<Guid> centroIds, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private static async Task<IReadOnlyList<ItemBandejaDto>> ColaAgrupadaAsync(ColaDeUnTenant cola)
    {
        var agrupada = await new ObtenerBandejaAgrupadaQueryHandler(cola, new CentroBloqueadoPorUnRechazo())
            .Handle(new ObtenerBandejaAgrupadaQuery(), CancellationToken.None);

        agrupada.SinGrupo.Should().BeEmpty();
        return agrupada.Grupos.Should().ContainSingle().Subject.Items;
    }

    private static async Task<IReadOnlyList<ItemBandejaDto>> MiTrabajoAgregadaAsync(ColaDeUnTenant cola)
    {
        var handler = new ObtenerMiTrabajoAgregadoQueryHandler(
            cola, cola.Configuracion, new EmpresasQueryContextFalso(), new CentroBloqueadoPorUnRechazo(), new AlcanceDatosServiceFalso(),
            NullLogger<ObtenerMiTrabajoAgregadoQueryHandler>.Instance);

        var resultado = await handler.Handle(new ObtenerMiTrabajoAgregadoQuery(), CancellationToken.None);

        resultado.NoConsultados.Should().BeEmpty();
        var tenant = resultado.Tenants.Should().ContainSingle().Subject;
        tenant.BloqueoActuacion.SinGrupo.Should().BeEmpty();
        return tenant.BloqueoActuacion.Grupos.Should().ContainSingle().Subject.Items;
    }

    private static string Describir(ItemBandejaDto item) =>
        item.Tipo == TipoItemBandeja.PlataformaRechazada && item.RechazoBloqueaCentro ? "PlataformaRechazada que bloquea" : item.Tipo.ToString();

    /// <summary>
    /// La cola plana llega ordenada sin saber qué Rechazada cierra su Centro:
    /// la que lo cierra tiene que acabar la primera, no en su sitio de «no
    /// bloquea» (detrás de Vencido y Faltante).
    /// </summary>
    [Fact]
    public async Task La_cola_agrupada_ordena_despues_de_saber_que_Rechazada_cierra_su_Centro()
    {
        var items = await ColaAgrupadaAsync(new ColaDeUnTenant());

        items.Select(Describir).Should().Equal(
            "PlataformaRechazada que bloquea", "RequisitoPendiente",
            "Vencido", "Faltante", "PlataformaRechazada", "Urgente", "PlataformaPendiente");
    }

    [Fact]
    public async Task Mi_trabajo_agregada_ordena_despues_de_saber_que_Rechazada_cierra_su_Centro()
    {
        var items = await MiTrabajoAgregadaAsync(new ColaDeUnTenant());

        items.Select(Describir).Should().Equal(
            "PlataformaRechazada que bloquea", "RequisitoPendiente",
            "PlataformaVencida", "Vencido", "Faltante", "PlataformaRechazada", "Urgente", "PlataformaPendiente");
    }

    /// <summary>
    /// Mismas fuentes, mismo orden: lo único que Mi trabajo agregada añade a
    /// este tramo es la acreditación vencida en la plataforma, que /bandeja no
    /// emite. Quitándola, las dos secuencias de filas son idénticas.
    /// </summary>
    [Fact]
    public async Task Mi_trabajo_agregada_y_la_cola_agrupada_comparten_el_orden()
    {
        var cola = new ColaDeUnTenant();

        var deLaCola = await ColaAgrupadaAsync(cola);
        var deMiTrabajo = await MiTrabajoAgregadaAsync(cola);

        deLaCola.Should().HaveCountGreaterThan(5);
        deMiTrabajo.Where(i => i.Tipo != TipoItemBandeja.PlataformaVencida).Select(i => i.Id)
            .Should().Equal(deLaCola.Select(i => i.Id));
    }
}
