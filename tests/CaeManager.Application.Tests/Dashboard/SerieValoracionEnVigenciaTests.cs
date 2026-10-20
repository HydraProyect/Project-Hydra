using CaeManager.Application.Common;
using CaeManager.Application.Dashboard;
using CaeManager.Application.Dashboard.Queries;
using CaeManager.Application.Dashboard.TarifasOperadorCae;
using CaeManager.Application.Operaciones;
using CaeManager.Application.Tenants.Queries.ObtenerClientesAutorizados;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Integraciones;
using CaeManager.Domain.Common;
using CaeManager.Domain.Dashboard;
using CaeManager.Domain.Operaciones;
using FluentAssertions;
using MediatR;
using Xunit;

namespace CaeManager.Application.Tests.Dashboard;

/// <summary>
/// La serie del Cuadro de dirección valora lo que el Operador CAE externo cobra solo dentro de la vigencia de su
/// Asignación de Operación en cada mes (decisión de la coordinadora, 2026-10-20), a través del handler: vigencias
/// leídas con <see cref="AutorizacionTarifasOperadorCae.VigenciasPorTenantBeneficiarioAsync"/> y valoración con
/// <c>ValorarEnVigenciaAsync</c>.
///
/// <para>
/// Escenario (hoy 18-oct-2026; serie septiembre y octubre; septiembre está cerrado):
/// A, Asignación externa desde el 15-sep (septiembre cerrado y parcial, octubre entero); E, desde el 10-oct (septiembre
/// sin vigencia, octubre parcial en vivo); B, solo una Asignación Programada; C, una fila raíz; y el propio Operador,
/// con una Asignación interna sobre sí mismo. Todos tienen tarifa: si la valoración no se acotara, todos saldrían con
/// importe.
/// </para>
/// </summary>
public class SerieValoracionEnVigenciaTests
{
    private static readonly DateTime Ahora = new(2026, 10, 18, 10, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Enero = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime QuinceSep = new(2026, 9, 15, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime DiezOct = new(2026, 10, 10, 0, 0, 0, DateTimeKind.Utc);
    private static readonly MesCuadroDireccion Septiembre = new(2026, 9);
    private static readonly MesCuadroDireccion Octubre = new(2026, 10);

    private static readonly Guid Operador = Guid.NewGuid();
    private static readonly Guid A = Guid.NewGuid();
    private static readonly Guid B = Guid.NewGuid();
    private static readonly Guid C = Guid.NewGuid();
    private static readonly Guid E = Guid.NewGuid();

    // Medición del mes entero en vivo, de la foto del cierre y de los tramos: tres juegos distintos para saber cuál se valoró.
    private static readonly MedicionCuadroDireccion EnVivo = Medicion(altasCentros: 10, obras: 4, visitas: 6);
    private static readonly MedicionCuadroDireccion DeLaFoto = Medicion(altasCentros: 20, obras: 8, visitas: 12);
    private static readonly UnidadesOperadorCae DelTramo = new(2, 1, 3);

    [Fact]
    public async Task Sin_vigencia_en_el_mes_no_hay_importe_y_una_Programada_una_raiz_o_una_interna_no_son_vigencia()
    {
        var (serie, _) = await PedirSerieAsync();

        Mes(serie, E, Septiembre).Should().BeEmpty("la Asignación de E empieza en octubre");
        foreach (var sinVigencia in new[] { B, C, Operador })
        {
            Mes(serie, sinVigencia, Septiembre).Should().BeEmpty("Programada, raíz o interna no cuentan como vigencia del Operador");
            Mes(serie, sinVigencia, Octubre).Should().BeEmpty("Programada, raíz o interna no cuentan como vigencia del Operador");
        }
    }

    [Fact]
    public async Task Un_mes_cubierto_entero_se_valora_con_la_medicion_del_mes()
    {
        var (serie, _) = await PedirSerieAsync();

        Mes(serie, A, Octubre).Should().BeEquivalentTo(Lineas(EnVivo.AltasCentros, EnVivo.ObrasNuevas!.Value, EnVivo.VisitasTrabajadorExtranjero!.Value));
    }

    [Fact]
    public async Task Un_mes_en_curso_con_vigencia_parcial_solo_valora_altas_Obras_y_Visitas_del_tramo()
    {
        var (serie, tramos) = await PedirSerieAsync();

        Mes(serie, E, Octubre).Should().BeEquivalentTo(Lineas(2, 1, 3));
        tramos.Should().ContainSingle(t => t.Tenant == E).Which.Tramos.Should().Equal(new TramoDeVigencia(DiezOct, Octubre.FinUtc));
    }

    [Fact]
    public async Task Un_mes_cerrado_con_vigencia_parcial_recuenta_el_tramo_y_no_valora_la_foto_del_mes_entero()
    {
        var (serie, tramos) = await PedirSerieAsync();

        Mes(serie, A, Septiembre).Should().BeEquivalentTo(Lineas(2, 1, 3), "la foto guarda el mes entero, no el tramo desde el 15");
        serie.Tenants.Single(t => t.TenantId == A).Meses[0].Cifras.AltasCentros.Valor
            .Should().Be(DeLaFoto.AltasCentros, "las cifras del Cuadro siguen saliendo de la foto");
        tramos.Should().ContainSingle(t => t.Tenant == A).Which.Tramos.Should().Equal(new TramoDeVigencia(QuinceSep, Septiembre.FinUtc));
    }

    private static IReadOnlyList<LineaFacturacionCuadroDto> Mes(SerieCuadroDireccionDto serie, Guid tenant, MesCuadroDireccion mes) =>
        serie.Tenants.Single(t => t.TenantId == tenant).Meses.Single(p => p.Anyo == mes.Anyo && p.Mes == mes.Mes).Facturacion!;

    private static LineaFacturacionCuadroDto[] Lineas(int centros, int obras, int visitas) =>
    [
        new(ConceptoTarifaOperadorCae.VisitaTrabajadorExtranjero, "Visita de Trabajador extranjero", "EUR", visitas, visitas * 10m),
        new(ConceptoTarifaOperadorCae.CentroNuevo, "Centro nuevo", "EUR", centros, centros * 50m),
        new(ConceptoTarifaOperadorCae.ObraNueva, "Obra nueva", "EUR", obras, obras * 100m),
    ];

    private static async Task<(SerieCuadroDireccionDto Serie, List<(Guid? Tenant, IReadOnlyList<TramoDeVigencia> Tramos)> Tramos)> PedirSerieAsync()
    {
        var medicion = new MedicionPorTramos();
        var handler = new ObtenerSerieCuadroDireccionQueryHandler(
            new Autorizados([
                new ClienteAutorizadoDto(Operador, "Operador", EsOrigen: true),
                new ClienteAutorizadoDto(A, "A", EsOrigen: false),
                new ClienteAutorizadoDto(B, "B", EsOrigen: false),
                new ClienteAutorizadoDto(C, "C", EsOrigen: false),
                new ClienteAutorizadoDto(E, "E", EsOrigen: false),
            ]),
            new AlcanceDatosServiceFalso(tieneAccesoTotal: true), medicion, new TarifasFalsas(), new OperacionesFalsas(),
            new DireccionDelOperador(), new RelojFijo(Ahora), new CierreDeSeptiembre());

        var serie = await handler.Handle(new ObtenerSerieCuadroDireccionQuery(2026, 10, 2, ConFacturacion: true), default);
        return (serie, medicion.Pedidos);
    }

    private static MedicionCuadroDireccion Medicion(int altasCentros, int obras, int visitas) =>
        new(10, 0, 0, 3, altasCentros, 0, 0, [], new KpisDashboardDto(0, 0, 0, 0, 0, 0, 0, 0),
            ExtranjerosActivos: 1, ObrasNuevas: obras, VisitasTrabajadorExtranjero: visitas);

    private static void Sellar(EntidadConTenant entidad, Guid tenant) =>
        typeof(EntidadConTenant).GetProperty(nameof(EntidadConTenant.TenantId))!.SetValue(entidad, tenant);

    // ---------------------------------------------------------------- dobles

    private sealed class OperacionesFalsas : IOperacionesQueryContext
    {
        private readonly List<AsignacionOperacion> _asignaciones;

        public OperacionesFalsas()
        {
            var raizIncoherente = AsignacionOperacion.Externa(C, Operador, ServicioCae.Outbound, AmbitoAsignacion.Universal, Enero, null, Enero);
            typeof(AsignacionOperacion).GetProperty(nameof(AsignacionOperacion.EsRaiz))!.SetValue(raizIncoherente, true);

            _asignaciones =
            [
                AsignacionOperacion.Externa(A, Operador, ServicioCae.Outbound, AmbitoAsignacion.Universal, QuinceSep, null, QuinceSep),
                AsignacionOperacion.Externa(E, Operador, ServicioCae.Outbound, AmbitoAsignacion.Universal, DiezOct, null, DiezOct),
                // Programada: se creó antes de su inicio y nunca se activó, aunque su inicio ya pasó.
                AsignacionOperacion.Externa(B, Operador, ServicioCae.Outbound, AmbitoAsignacion.Universal, Enero, null, Enero.AddDays(-1)),
                raizIncoherente,
                AsignacionOperacion.Interna(Operador, ServicioCae.Outbound, AmbitoAsignacion.DeRelacionCliente(Guid.NewGuid()), Enero, null, Enero),
                // De otro Operador sobre A: no es vigencia de este Operador.
                AsignacionOperacion.Externa(A, Guid.NewGuid(), ServicioCae.Outbound, AmbitoAsignacion.Universal, Enero, null, Enero),
            ];
        }

        public IQueryable<AsignacionOperacion> AsignacionesOperacion => new TestAsyncQueryable<AsignacionOperacion>(_asignaciones.AsQueryable());
        public IQueryable<AsignacionCartera> AsignacionesCartera => new TestAsyncQueryable<AsignacionCartera>(new List<AsignacionCartera>().AsQueryable());
    }

    private sealed class TarifasFalsas : ITarifasOperadorCaeContext
    {
        private readonly List<TarifaOperadorCae> _tarifas = [];

        public TarifasFalsas()
        {
            foreach (var tenant in new[] { Operador, A, B, C, E })
            {
                foreach (var (concepto, precio) in new[]
                {
                    (ConceptoTarifaOperadorCae.CentroNuevo, 50m), (ConceptoTarifaOperadorCae.ObraNueva, 100m),
                    (ConceptoTarifaOperadorCae.VisitaTrabajadorExtranjero, 10m),
                })
                {
                    var tarifa = TarifaOperadorCae.Registrar(tenant, concepto, precio, "EUR", true, Guid.NewGuid(), Enero);
                    Sellar(tarifa, Operador);
                    _tarifas.Add(tarifa);
                }
            }
        }

        public IQueryable<TarifaOperadorCae> TarifasOperadorCae => new TestAsyncQueryable<TarifaOperadorCae>(_tarifas.AsQueryable());
        public void Agregar(TarifaOperadorCae tarifa) => throw new NotSupportedException();
    }

    private sealed class CierreDeSeptiembre : ICierresCuadroDireccion
    {
        private readonly List<CierreMensualTenantBeneficiario> _cierres =
        [
            CierreMensualTenantBeneficiario.Crear(2026, 9, new FotoCierreMensual(DeLaFoto, []).Serializar(), false,
                new DateTime(2026, 10, 2, 3, 0, 0, DateTimeKind.Utc)),
        ];

        public IQueryable<CierreMensualTenantBeneficiario> CierresMensuales =>
            new TestAsyncQueryable<CierreMensualTenantBeneficiario>(_cierres.AsQueryable());

        public void Agregar(CierreMensualTenantBeneficiario cierre) => throw new NotSupportedException();
    }

    private sealed class MedicionPorTramos() : MedicionMensualCuadroDireccion(null!, null!, null!, null!, null!, null!, null!)
    {
        public List<(Guid? Tenant, IReadOnlyList<TramoDeVigencia> Tramos)> Pedidos { get; } = [];

        public override Task<MedicionCuadroDireccion> MedirAsync(MesCuadroDireccion mes, DateTime corteUtc, CancellationToken cancellationToken, bool medirDocumental = true) =>
            Task.FromResult(EnVivo);

        public override Task<UnidadesOperadorCae> MedirUnidadesOperadorAsync(
            IReadOnlyList<TramoDeVigencia> tramos, DateTime corteUtc, CancellationToken cancellationToken)
        {
            Pedidos.Add((AmbitoTenantExplicito.TenantIdActual, tramos));
            return Task.FromResult(DelTramo);
        }
    }

    private sealed class DireccionDelOperador : ICurrentUserService
    {
        public Task<Guid?> ObtenerUsuarioActualIdAsync() => Task.FromResult<Guid?>(Guid.NewGuid());
        public Task<string?> ObtenerRolEfectivoAsync() => Task.FromResult<string?>("DireccionCae");
        public Task<string?> ObtenerRolOrigenAsync() => ObtenerRolEfectivoAsync();
        public Task<Guid?> ObtenerTenantOrigenIdAsync() => Task.FromResult<Guid?>(Operador);
        public Task<bool> TieneDobleFactorActivoAsync() => Task.FromResult(true);
    }

    private sealed class RelojFijo(DateTime ahoraUtc) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(ahoraUtc, TimeSpan.Zero);
    }

    private sealed class Autorizados(IReadOnlyList<ClienteAutorizadoDto> autorizados) : IMediator
    {
        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            request is ObtenerClientesAutorizadosQuery
                ? Task.FromResult((TResponse)(object)autorizados)
                : throw new NotSupportedException(request.GetType().Name);

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest =>
            throw new NotSupportedException();

        public Task<object?> Send(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification => Task.CompletedTask;
    }
}
