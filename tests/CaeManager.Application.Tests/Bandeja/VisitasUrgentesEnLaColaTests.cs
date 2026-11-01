using CaeManager.Application.Alertas.Queries.ObtenerAlertas;
using CaeManager.Application.Bandeja.Queries.ObtenerBandejaGestor;
using CaeManager.Application.Bandeja.Queries.ObtenerMiTrabajoAgregado;
using CaeManager.Application.Centros;
using CaeManager.Application.Centros.Queries.ObtenerDocumentacionBloqueantePendiente;
using CaeManager.Application.Comunicaciones.Queries.ObtenerSugerenciasVisitaCorreoPendientes;
using CaeManager.Application.Documentos.Queries.ObtenerAcreditacionesPorProveedor;
using CaeManager.Application.Documentos.Queries.ObtenerRevisionesIaPendientes;
using CaeManager.Application.Tenants.Queries.ObtenerClientesAutorizados;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Documentos;
using CaeManager.Application.Tests.Integraciones;
using CaeManager.Application.Tests.Plantillas;
using CaeManager.Application.Tests.Reportes;
using CaeManager.Application.Tests.TiposDocumento;
using CaeManager.Application.Trabajadores.Queries.ObtenerDeteccionesPendientes;
using CaeManager.Application.Visitas;
using CaeManager.Application.Visitas.Queries.ObtenerVisitas;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Common;
using CaeManager.Domain.Configuracion;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Visitas;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CaeManager.Application.Tests.Bandeja;

/// <summary>
/// La vigilancia recorre todas las candidatas y su aviso enlaza a /bandeja, así que /bandeja y Mi trabajo
/// tienen que enseñar toda Visita urgente que el aviso cuenta. Con una sola página de 200 candidatas, las
/// Visitas en curso, que van delante por fecha y se descartan después, tapaban a las urgentes de verdad, y
/// más de 200 urgentes dejaban fuera a la última.
/// </summary>
public class VisitasUrgentesEnLaColaTests
{
    private static readonly int TamanoPagina = GestionesUrgentesDeVisita.ConsultaCandidatas().TamanoPagina;

    public static TheoryData<int, int> VisitasEnCursoYUrgentes => new()
    {
        { TamanoPagina + 1, 1 },
        { 0, TamanoPagina + 1 },
        { TamanoPagina + 1, TamanoPagina + 1 },
    };

    [Theory]
    [MemberData(nameof(VisitasEnCursoYUrgentes))]
    public async Task La_bandeja_ensena_toda_Visita_urgente_que_el_aviso_cuenta(int enCurso, int urgentes)
    {
        var cola = new Cola(enCurso, urgentes);

        var items = await new ObtenerBandejaGestorQueryHandler(cola, cola.Configuracion)
            .Handle(new ObtenerBandejaGestorQuery(), default);

        var cuentaElAviso = await cola.IdsQueCuentaElAvisoAsync();
        cuentaElAviso.Should().HaveCount(urgentes);
        Visitas(items).Should().BeEquivalentTo(cuentaElAviso);
    }

    [Theory]
    [MemberData(nameof(VisitasEnCursoYUrgentes))]
    public async Task Mi_trabajo_ensena_toda_Visita_urgente_que_el_aviso_cuenta(int enCurso, int urgentes)
    {
        var cola = new Cola(enCurso, urgentes);

        var resultado = await new ObtenerMiTrabajoAgregadoQueryHandler(
                cola, cola.Configuracion, new EmpresasQueryContextFalso(), new CalculoEstadoCentroSinUso(),
                new AlcanceDatosServiceFalso(), NullLogger<ObtenerMiTrabajoAgregadoQueryHandler>.Instance)
            .Handle(new ObtenerMiTrabajoAgregadoQuery(), default);

        var bloqueoActuacion = resultado.Tenants.Should().ContainSingle().Which.BloqueoActuacion;
        var cuentaElAviso = await cola.IdsQueCuentaElAvisoAsync();
        cuentaElAviso.Should().HaveCount(urgentes);
        Visitas(bloqueoActuacion.Grupos.SelectMany(g => g.Items).Concat(bloqueoActuacion.SinGrupo))
            .Should().BeEquivalentTo(cuentaElAviso);
    }

    /// <summary>Si la consulta trajera una que el criterio descarta, volvería a ocupar sitio en la lectura; si perdiera una, nadie la vería.</summary>
    [Fact]
    public async Task Las_candidatas_son_justo_las_que_el_criterio_de_urgencia_acepta()
    {
        var cola = new Cola(enCurso: 0, urgentes: 0);
        var hoy = DiaDeNegocio.Hoy();
        foreach (var (inicio, fin) in new[]
        {
            (hoy.AddDays(-1), hoy.AddDays(1)), (hoy, hoy), (hoy, hoy.AddDays(1)), (hoy.AddDays(1), hoy.AddDays(1)),
            (hoy.AddDays(1), hoy.AddDays(5)), (hoy.AddDays(2), hoy.AddDays(2)), (hoy.AddDays(3), hoy.AddDays(3)),
        })
            cola.AnadirVisita(inicio, fin);

        var todas = await cola.Manejador.Handle(
            new ObtenerVisitasQuery(Busqueda: null, SoloActivas: true, NotificadoCliente: null, TamanoPagina: 50), default);
        var candidatas = await cola.Manejador.Handle(GestionesUrgentesDeVisita.ConsultaCandidatas(), default);

        candidatas.Elementos.Should().HaveCount(3);
        candidatas.Elementos.Select(v => v.Id).Should().BeEquivalentTo(
            todas.Elementos.Where(GestionesUrgentesDeVisita.EsUrgente).Select(v => v.Id));
    }

    [Fact]
    public async Task El_filtro_de_urgentes_del_listado_sigue_incluyendo_las_Visitas_en_curso()
    {
        var cola = new Cola(enCurso: 1, urgentes: 1);

        var listado = await cola.Manejador.Handle(
            new ObtenerVisitasQuery(Busqueda: null, SoloActivas: true, NotificadoCliente: null, SoloUrgentes: true), default);

        listado.Elementos.Should().HaveCount(2);
    }

    [Fact]
    public async Task Sin_pagina_el_alcance_del_usuario_sigue_acotando_las_Visitas_urgentes()
    {
        var cola = new Cola(enCurso: 0, urgentes: 1);
        var centroAjeno = cola.AnadirCentro();
        var manana = DiaDeNegocio.Hoy().AddDays(1);
        cola.AnadirVisita(manana, manana, centroAjeno);
        var alcance = new AlcanceDatosServiceFalso(tieneAccesoTotal: false, centroIdsVisibles: [cola.Centro.Id]);

        var visitas = await cola.ManejadorCon(alcance).Handle(new ObtenerVisitasUrgentesQuery(), default);

        visitas.Should().ContainSingle().Which.CentroId.Should().Be(cola.Centro.Id);
    }

    private static IEnumerable<Guid> Visitas(IEnumerable<ItemBandejaDto> items) =>
        items.Where(i => i.Tipo == TipoItemBandeja.VisitaUrgente).Select(i => i.VisitaId!.Value);

    /// <summary>
    /// Las consultas de Visitas pasan por el manejador real, con su filtro y su paginación; el resto de la cola, vacío.
    /// Responde también a <see cref="ObtenerVisitasQuery"/>: el defecto era enviar esa consulta, y tiene que fallar por la aserción.
    /// </summary>
    private sealed class Cola : IMediator
    {
        private readonly List<Visita> _visitas = [];
        private readonly CentrosQueryContextFalso _centros = new();
        private readonly EmpresasQueryContextFalso _empresas = new();
        private readonly Empresa _titular = new("Titular");
        private readonly Empresa _empresa = new("Empresa, S.A.");

        public Cola(int enCurso, int urgentes)
        {
            Configuracion.ListaParametrosSistema.Add(new ParametroSistema(30, 7));
            _empresas.ListaEmpresas.AddRange([_titular, _empresa]);
            Centro = new Centro(_titular.Id, _empresa.Id, "Centro Norte");
            _centros.ListaCentros.Add(Centro);

            var hoy = DiaDeNegocio.Hoy();
            for (var i = 0; i < enCurso; i++)
                AnadirVisita(hoy.AddDays(-1), hoy.AddDays(1));
            for (var i = 0; i < urgentes; i++)
                AnadirVisita(hoy.AddDays(1), hoy.AddDays(1));
        }

        public ConfiguracionQueryContextFalso Configuracion { get; } = new();

        public Centro Centro { get; }

        public ObtenerVisitasQueryHandler Manejador => ManejadorCon(new AlcanceDatosServiceFalso());

        public ObtenerVisitasQueryHandler ManejadorCon(AlcanceDatosServiceFalso alcance) =>
            new(_centros, Configuracion, new DocumentosQueryContextFalso(), _empresas, new TiposDocumentoQueryContextFalso(),
                new ListaDeVisitas(_visitas), alcance, new TrabajadoresQueryContextFalso());

        public Guid AnadirCentro()
        {
            var centro = new Centro(_titular.Id, _empresa.Id, "Centro Sur");
            _centros.ListaCentros.Add(centro);
            return centro.Id;
        }

        public void AnadirVisita(DateOnly inicio, DateOnly fin, Guid? centroId = null) =>
            _visitas.Add(new Visita(centroId ?? Centro.Id, inicio, fin, null));

        /// <summary>El mismo recorrido y el mismo criterio que <c>ContarGestionesUrgentesAsync</c> de la vigilancia.</summary>
        public async Task<List<Guid>> IdsQueCuentaElAvisoAsync() =>
            (await Manejador.LeerTodasAsync(GestionesUrgentesDeVisita.ConsultaCandidatas(), centroIdsVisibles: null, default))
            .Where(GestionesUrgentesDeVisita.EsUrgente)
            .Select(v => v.Id)
            .ToList();

        public async Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            object respuesta = request switch
            {
                ObtenerVisitasUrgentesQuery urgentes => await Manejador.Handle(urgentes, cancellationToken),
                ObtenerVisitasQuery visitas => await Manejador.Handle(visitas, cancellationToken),
                ObtenerClientesAutorizadosQuery => new List<ClienteAutorizadoDto> { new(Guid.NewGuid(), "Tenant propietario", EsOrigen: false) },
                ObtenerAlertasQuery => new List<AlertaDto>(),
                ObtenerRevisionesIaPendientesQuery => new List<RevisionIaDocumentoDto>(),
                ObtenerDocumentacionBloqueantePendienteQuery => new List<DocumentacionBloqueantePendienteDto>(),
                ObtenerSugerenciasVisitaCorreoPendientesQuery => new List<SugerenciaVisitaCorreoPendienteDto>(),
                ObtenerDeteccionesPendientesQuery => new List<DeteccionPendienteDto>(),
                ObtenerAcreditacionesPorProveedorQuery => new List<ProveedorAcreditacionesDto>(),
                _ => throw new NotSupportedException(request.GetType().Name),
            };
            return (TResponse)respuesta;
        }

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest =>
            throw new NotSupportedException();

        public Task<object?> Send(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task Publish(object notification, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification => throw new NotSupportedException();
    }

    private sealed class ListaDeVisitas(List<Visita> visitas) : IVisitasQueryContext
    {
        public IQueryable<Visita> Visitas => new TestAsyncQueryable<Visita>(visitas.AsQueryable());

        public IQueryable<VisitaTrabajador> VisitasTrabajadores =>
            new TestAsyncQueryable<VisitaTrabajador>(Array.Empty<VisitaTrabajador>().AsQueryable());
    }

    /// <summary>Sin ninguna Rechazada, el cálculo de estado del Centro no debe correr.</summary>
    private sealed class CalculoEstadoCentroSinUso : ICalculoEstadoCentroService
    {
        public Task<IReadOnlyDictionary<Guid, ResultadoEstadoCentro>> CalcularAsync(
            IReadOnlyList<Guid> centroIds, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyDictionary<Guid, FraccionCumplimiento>> CalcularCumplimientoAsync(
            IReadOnlyList<Guid> centroIds, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<ParDocumentalExigido>> ObtenerParesExigidosAsync(
            IReadOnlyList<Guid> centroIds, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<ParDocumentalDeEmpresaExigido>> ObtenerParesDeEmpresaExigidosAsync(
            IReadOnlyList<Guid> centroIds, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
