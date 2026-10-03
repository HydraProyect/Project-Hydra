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

    private static ObtenerMiTrabajoAgregadoQueryHandler Handler(IMediator mediator)
    {
        var configuracion = new ConfiguracionQueryContextFalso();
        configuracion.ListaParametrosSistema.Add(new ParametroSistema(30, 7));

        return new ObtenerMiTrabajoAgregadoQueryHandler(
            mediator, configuracion, new EmpresasQueryContextFalso(), new CalculoEstadoCentroSinUso(),
            new AlcanceDatosServiceFalso(), NullLogger<ObtenerMiTrabajoAgregadoQueryHandler>.Instance);
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

            if (AmbitoTenantExplicito.TenantIdActual == tenantQueFalla)
                throw excepcion ?? new InvalidOperationException("Fallo determinista en este Tenant.");

            object vacio = request switch
            {
                ObtenerAlertasQuery => new List<AlertaDto>(),
                ObtenerRevisionesIaPendientesQuery => new List<RevisionIaDocumentoDto>(),
                ObtenerDocumentacionBloqueantePendienteQuery => new List<DocumentacionBloqueantePendienteDto>(),
                ObtenerVisitasQuery => new ResultadoPaginado<VisitaListaDto>([], 0, 1, 200),
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
    }
}
