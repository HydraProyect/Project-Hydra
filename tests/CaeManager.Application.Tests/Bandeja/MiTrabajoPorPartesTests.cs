using CaeManager.Application.Alertas.Queries.ObtenerAlertas;
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
using CaeManager.Domain.Configuracion;
using CaeManager.Domain.Documentos;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CaeManager.Application.Tests.Bandeja;

/// <summary>
/// Ficha 02 de «La jornada del Gestor CAE»: Mi trabajo entrega la cola por partes, una por Tenant propietario, para
/// que la pantalla pinte cada Empresa al terminar. Lo que se prueba es lo que NO puede cambiar: el recorrido sigue
/// siendo secuencial, cada Tenant se consulta dentro de su propio <see cref="AmbitoTenantExplicito"/>, ningún ámbito
/// queda abierto mientras el consumidor tiene el control, y la puerta de acceso a datos no se retiene entre partes.
/// </summary>
public class MiTrabajoPorPartesTests
{
    private static readonly Guid TenantOrigen = Guid.NewGuid();
    private static readonly Guid TenantRefri = Guid.NewGuid();
    private static readonly Guid TenantRoto = Guid.NewGuid();

    private static ObtenerMiTrabajoAgregadoQueryHandler Handler(IMediator mediator, PuertaAccesoDatos? puerta = null)
    {
        var configuracion = new ConfiguracionQueryContextFalso();
        configuracion.ListaParametrosSistema.Add(new ParametroSistema(30, 7));

        return new ObtenerMiTrabajoAgregadoQueryHandler(
            mediator, configuracion, new EmpresasQueryContextFalso(), new CalculoEstadoCentroSinUso(),
            new AlcanceDatosServiceFalso(), NullLogger<ObtenerMiTrabajoAgregadoQueryHandler>.Instance, puerta);
    }

    private static async Task<List<ParteMiTrabajoDto>> Recorrer(
        ObtenerMiTrabajoAgregadoQueryHandler handler, Action<ParteMiTrabajoDto>? alRecibir = null)
    {
        var partes = new List<ParteMiTrabajoDto>();
        await foreach (var parte in handler.Handle(new ObtenerMiTrabajoPorPartesQuery(), CancellationToken.None))
        {
            alRecibir?.Invoke(parte);
            partes.Add(parte);
        }

        return partes;
    }

    [Fact]
    public async Task Entrega_una_apertura_y_una_parte_por_Tenant_en_el_orden_de_la_cartera()
    {
        var partes = await Recorrer(Handler(new MediatorPorTenant(tenantQueFalla: null), new PuertaAccesoDatos()));

        partes.Select(p => p.Completados).Should().Equal(0, 1, 2, 3);
        partes.Should().OnlyContain(p => p.Total == 3, "la apertura ya dice cuántos Tenants se van a consultar");
        partes[0].Tenant.Should().BeNull();
        partes[0].NoConsultado.Should().BeNull();
        partes.Skip(1).Select(p => p.Tenant!.TenantId).Should().Equal(TenantOrigen, TenantRefri, TenantRoto);
    }

    [Fact]
    public async Task Un_Tenant_que_falla_llega_como_parte_no_consultada_y_los_demas_siguen()
    {
        var partes = await Recorrer(Handler(new MediatorPorTenant(TenantRoto), new PuertaAccesoDatos()));

        partes.Where(p => p.Tenant is not null).Select(p => p.Tenant!.TenantId).Should().Equal(TenantOrigen, TenantRefri);
        partes.Single(p => p.NoConsultado is not null).NoConsultado
            .Should().Be(new TenantNoConsultadoDto(TenantRoto, "Roto SL", EsOrigen: false));
        partes[^1].Completados.Should().Be(3, "un Tenant no consultado también cuenta como resuelto");
    }

    [Fact]
    public async Task Ningun_ambito_de_Tenant_queda_abierto_mientras_el_consumidor_tiene_el_control()
    {
        var ambitosEnElConsumidor = new List<Guid?>();

        await Recorrer(Handler(new MediatorPorTenant(tenantQueFalla: null), new PuertaAccesoDatos()),
            _ => ambitosEnElConsumidor.Add(AmbitoTenantExplicito.TenantIdActual));

        ambitosEnElConsumidor.Should().HaveCount(4).And.OnlyContain(a => a == null,
            "el sellado se cierra antes de cada entrega: lo que pinte la pantalla no corre bajo el Tenant de la parte");
    }

    [Fact]
    public async Task El_recorrido_es_secuencial_un_Tenant_cada_vez()
    {
        var mediator = new MediatorPorTenant(tenantQueFalla: null);

        await Recorrer(Handler(mediator, new PuertaAccesoDatos()));

        mediator.MaximoDeTenantsEnVuelo.Should().Be(1);
        mediator.TenantsConsultados.Should().Equal(TenantOrigen, TenantRefri, TenantRoto);
    }

    [Fact]
    public async Task La_puerta_de_acceso_a_datos_no_se_retiene_mientras_la_parte_espera_a_pintarse()
    {
        var puerta = new PuertaAccesoDatos();
        var handler = Handler(new MediatorPorTenant(tenantQueFalla: null), puerta);
        var otroAccesoPudoEntrar = new List<bool>();

        await foreach (var parte in handler.Handle(new ObtenerMiTrabajoPorPartesQuery(), CancellationToken.None))
        {
            // Otro componente del circuito pide la puerta justo cuando la pantalla pinta una parte.
            using var limite = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var entro = false;
            await puerta.EjecutarAsync(() => { entro = true; return Task.CompletedTask; }, limite.Token);
            otroAccesoPudoEntrar.Add(entro);
        }

        otroAccesoPudoEntrar.Should().HaveCount(4).And.OnlyContain(e => e);
    }

    [Fact]
    public async Task Cada_Tenant_se_consulta_dentro_de_la_puerta()
    {
        var puerta = new PuertaAccesoDatos();
        var mediator = new MediatorPorTenant(tenantQueFalla: null) { Puerta = puerta };

        await Recorrer(Handler(mediator, puerta));

        mediator.ConsultasConLaPuertaLibre.Should().Be(0, "el flujo no pasa por el pipeline: la serialización la pone el propio handler");
        mediator.ConsultasConLaPuertaTomada.Should().BeGreaterThan(0, "control positivo: el instrumento sí observó consultas");
    }

    [Fact]
    public void Sin_puerta_el_flujo_se_rechaza_en_vez_de_correr_sin_serializar()
    {
        var consulta = () => Handler(new MediatorPorTenant(tenantQueFalla: null))
            .Handle(new ObtenerMiTrabajoPorPartesQuery(), CancellationToken.None);

        consulta.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public async Task La_consulta_agregada_devuelve_lo_mismo_que_la_suma_de_las_partes()
    {
        var agregado = await Handler(new MediatorPorTenant(TenantRoto))
            .Handle(new ObtenerMiTrabajoAgregadoQuery(), CancellationToken.None);
        var partes = await Recorrer(Handler(new MediatorPorTenant(TenantRoto), new PuertaAccesoDatos()));

        agregado.Tenants.Select(t => t.TenantId).Should().Equal(partes.Where(p => p.Tenant is not null).Select(p => p.Tenant!.TenantId));
        agregado.NoConsultados.Should().Equal(partes.Where(p => p.NoConsultado is not null).Select(p => p.NoConsultado!));
    }

    [Fact]
    public async Task Cancelar_a_mitad_de_recorrido_aborta_sin_disfrazarse_de_Tenant_no_consultado()
    {
        using var cancelacion = new CancellationTokenSource();
        var handler = Handler(new MediatorPorTenant(tenantQueFalla: null), new PuertaAccesoDatos());

        var recorrido = async () =>
        {
            await foreach (var parte in handler.Handle(new ObtenerMiTrabajoPorPartesQuery(), cancelacion.Token))
            {
                if (parte.Completados == 1) cancelacion.Cancel();
            }
        };

        await recorrido.Should().ThrowAsync<OperationCanceledException>();
    }

    private sealed class MediatorPorTenant(Guid? tenantQueFalla) : IMediator
    {
        private int _enVuelo;
        private int _maximo;

        public PuertaAccesoDatos? Puerta { get; init; }
        public int MaximoDeTenantsEnVuelo => _maximo;
        public List<Guid> TenantsConsultados { get; } = [];
        public int ConsultasConLaPuertaTomada { get; private set; }
        public int ConsultasConLaPuertaLibre { get; private set; }

        public async Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            if (request is ObtenerClientesAutorizadosQuery)
            {
                IReadOnlyList<ClienteAutorizadoDto> tenants =
                [
                    new(TenantOrigen, "ArcoSPA", EsOrigen: true),
                    new(TenantRefri, "Refrielectric", EsOrigen: false),
                    new(TenantRoto, "Roto SL", EsOrigen: false),
                ];
                return (TResponse)(object)tenants;
            }

            var tenant = AmbitoTenantExplicito.TenantIdActual
                ?? throw new InvalidOperationException("La cola de un Tenant se consultó fuera de su ámbito.");
            if (request is ObtenerAlertasQuery)
            {
                // Primera consulta de cada Tenant: marca su entrada y mide cuántos hay a la vez.
                TenantsConsultados.Add(tenant);
                _maximo = Math.Max(_maximo, Interlocked.Increment(ref _enVuelo));
                if (Puerta is not null)
                {
                    // Sondeo desde OTRO flujo (sin heredar la marca de reentrada): si la puerta está tomada, no entra
                    // y el sondeo vence por tiempo.
                    using var sondeo = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
                    Task prueba;
                    using (ExecutionContext.SuppressFlow())
                        prueba = Task.Run(() => Puerta.EjecutarAsync(() => Task.CompletedTask, sondeo.Token));
                    try
                    {
                        await prueba;
                        ConsultasConLaPuertaLibre++;
                    }
                    catch (OperationCanceledException)
                    {
                        ConsultasConLaPuertaTomada++;
                    }
                }
            }

            await Task.Yield();
            try
            {
                if (tenant == tenantQueFalla)
                    throw new InvalidOperationException("Fallo determinista en este Tenant.");

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
                return (TResponse)vacio;
            }
            finally
            {
                if (request is ObtenerAcreditacionesPorProveedorQuery || tenant == tenantQueFalla)
                    Interlocked.Decrement(ref _enVuelo);
            }
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
