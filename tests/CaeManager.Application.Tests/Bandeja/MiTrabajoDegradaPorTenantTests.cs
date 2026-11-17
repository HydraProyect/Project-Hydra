using CaeManager.Application.Alertas.Queries.ObtenerAlertas;
using CaeManager.Application.Bandeja.Queries.ObtenerMiTrabajoAgregado;
using CaeManager.Application.Centros;
using CaeManager.Domain.Documentos;
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
using CaeManager.Domain.Configuracion;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CaeManager.Application.Tests.Bandeja;

/// <summary>
/// FS-07 (auditoría UX de flujos sin salida, 2026-09-24): Mi trabajo recorría
/// los Tenants propietarios de la cartera sin capturar el fallo de ninguno, y
/// una excepción en uno dejaba al Gestor CAE sin la cola de toda la cartera.
/// Ahora degrada por Tenant: los demás se devuelven y el que falló se nombra
/// aparte, sin datos suyos.
/// </summary>
public class MiTrabajoDegradaPorTenantTests
{
    private static readonly Guid TenantOrigen = Guid.NewGuid();
    private static readonly Guid TenantRefri = Guid.NewGuid();
    private static readonly Guid TenantRoto = Guid.NewGuid();

    private static ObtenerMiTrabajoAgregadoQueryHandler Handler(IMediator mediator, IClienteActivoSeleccionado? seleccion = null)
    {
        var configuracion = new ConfiguracionQueryContextFalso();
        configuracion.ListaParametrosSistema.Add(new ParametroSistema(30, 7));

        return new ObtenerMiTrabajoAgregadoQueryHandler(
            mediator, configuracion, new EmpresasQueryContextFalso(), new CalculoEstadoCentroSinUso(),
            new AlcanceDatosServiceFalso(), NullLogger<ObtenerMiTrabajoAgregadoQueryHandler>.Instance, seleccion: seleccion);
    }

    /// <summary>
    /// Issue #2351: con una Sesión Privilegiada seleccionada, el interceptor de RLS deja sin Tenant cualquier ámbito
    /// explícito sobre un Tenant distinto del objetivo. Mi trabajo no consulta esos Tenants: solo el objetivo.
    /// </summary>
    [Fact]
    public async Task Con_una_Sesion_Privilegiada_solo_se_consulta_su_Tenant_objetivo()
    {
        var mediator = new MediatorQueFallaEn(tenantQueFalla: null);

        var resultado = await Handler(mediator, new Seleccion(TenantRefri, sesionPrivilegiada: Guid.NewGuid()))
            .Handle(new ObtenerMiTrabajoAgregadoQuery(), CancellationToken.None);

        resultado.Tenants.Select(t => t.TenantId).Should().Equal([TenantRefri]);
        resultado.NoConsultados.Should().BeEmpty("los demás Tenants no se intentan, así que no fallan ni se registran");
        mediator.Consultados.Should().Equal([TenantRefri], "no se abre ningún ámbito sobre un Tenant distinto del objetivo");
    }

    [Fact]
    public async Task Con_una_Sesion_Privilegiada_cuyo_Tenant_objetivo_no_esta_en_la_lista_no_se_consulta_ninguno()
    {
        var mediator = new MediatorQueFallaEn(tenantQueFalla: null);

        var resultado = await Handler(mediator, new Seleccion(Guid.NewGuid(), sesionPrivilegiada: Guid.NewGuid()))
            .Handle(new ObtenerMiTrabajoAgregadoQuery(), CancellationToken.None);

        resultado.Tenants.Should().BeEmpty();
        mediator.Consultados.Should().BeEmpty();
    }

    [Fact]
    public async Task Sin_Sesion_Privilegiada_una_seleccion_de_Tenant_no_recorta_la_cartera()
    {
        var mediator = new MediatorQueFallaEn(tenantQueFalla: null);

        var resultado = await Handler(mediator, new Seleccion(TenantRefri, sesionPrivilegiada: null))
            .Handle(new ObtenerMiTrabajoAgregadoQuery(), CancellationToken.None);

        resultado.Tenants.Should().HaveCount(3, "el recorte es solo de la Sesión Privilegiada");
        mediator.Consultados.Should().Equal([TenantOrigen, TenantRefri, TenantRoto]);
    }

    private sealed class Seleccion(Guid? tenant, Guid? sesionPrivilegiada) : IClienteActivoSeleccionado
    {
        public Guid? TenantIdSeleccionado => tenant;
        public Guid? AsignacionOperacionIdSeleccionada => null;
        public Guid? SesionPrivilegiadaIdSeleccionada => sesionPrivilegiada;
    }

    [Fact]
    public async Task Si_falla_un_Tenant_los_demas_se_devuelven_y_el_que_fallo_se_nombra()
    {
        var resultado = await Handler(new MediatorQueFallaEn(TenantRoto))
            .Handle(new ObtenerMiTrabajoAgregadoQuery(), CancellationToken.None);

        resultado.Tenants.Select(t => t.TenantId).Should().Equal(
            [TenantOrigen, TenantRefri], "el fallo de un Tenant no puede llevarse por delante la cola de los demás");
        resultado.NoConsultados.Should().ContainSingle()
            .Which.Should().Be(new TenantNoConsultadoDto(TenantRoto, "Roto SL", EsOrigen: false));
    }

    [Fact]
    public async Task Sin_fallos_no_hay_Tenants_no_consultados()
    {
        var resultado = await Handler(new MediatorQueFallaEn(tenantQueFalla: null))
            .Handle(new ObtenerMiTrabajoAgregadoQuery(), CancellationToken.None);

        resultado.Tenants.Should().HaveCount(3);
        resultado.NoConsultados.Should().BeEmpty();
    }

    [Fact]
    public async Task Cancelar_la_consulta_no_se_disfraza_de_Tenant_no_consultado()
    {
        var mediator = new MediatorQueFallaEn(TenantRoto, new OperationCanceledException());

        var consulta = () => Handler(mediator).Handle(new ObtenerMiTrabajoAgregadoQuery(), CancellationToken.None);

        await consulta.Should().ThrowAsync<OperationCanceledException>();
    }

    private sealed class MediatorQueFallaEn(Guid? tenantQueFalla, Exception? excepcion = null) : IMediator
    {
        public List<Guid> Consultados { get; } = [];

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            if (request is ObtenerClientesAutorizadosQuery)
            {
                IReadOnlyList<ClienteAutorizadoDto> tenants =
                [
                    new(TenantOrigen, "ArcoSPA", EsOrigen: true),
                    new(TenantRefri, "Refrielectric", EsOrigen: false),
                    new(TenantRoto, "Roto SL", EsOrigen: false),
                ];
                return Task.FromResult((TResponse)(object)tenants);
            }

            if (AmbitoTenantExplicito.TenantIdActual is null)
                throw new InvalidOperationException("La cola de un Tenant se consultó fuera de su ámbito.");

            var consultado = AmbitoTenantExplicito.TenantIdActual.Value;
            if (!Consultados.Contains(consultado))
                Consultados.Add(consultado);

            if (AmbitoTenantExplicito.TenantIdActual == tenantQueFalla)
                throw excepcion ?? new InvalidOperationException("Fallo determinista en este Tenant.");

            object vacio = request switch
            {
                ObtenerAlertasQuery => new List<AlertaDto>(),
                ObtenerRevisionesIaPendientesQuery => new List<RevisionIaDocumentoDto>(),
                ObtenerDocumentacionBloqueantePendienteQuery => new List<DocumentacionBloqueantePendienteDto>(),
                ObtenerVisitasUrgentesQuery => new List<VisitaListaDto>(),
                ObtenerSugerenciasVisitaCorreoPendientesQuery => new List<SugerenciaVisitaCorreoPendienteDto>(),
                ObtenerDeteccionesPendientesQuery => new List<DeteccionPendienteDto>(),
                ObtenerAcreditacionesPorProveedorQuery => new List<ProveedorAcreditacionesDto>(),
                _ => throw new NotSupportedException(request.GetType().Name),
            };
            return Task.FromResult((TResponse)vacio);
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

    /// <summary>Colas vacías: ninguna Rechazada, así que el cálculo de estado del Centro no debe correr.</summary>
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
