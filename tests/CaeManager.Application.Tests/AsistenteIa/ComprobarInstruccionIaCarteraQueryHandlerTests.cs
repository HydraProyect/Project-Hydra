using CaeManager.Application.AsistenteIa.Candidatos;
using CaeManager.Application.Common;
using CaeManager.Application.Cumplimiento;
using CaeManager.Application.Tenants.Queries.ObtenerClientesAutorizados;
using CaeManager.Application.Tests.Clientes;
using FluentAssertions;
using MediatR;
using Xunit;

namespace CaeManager.Application.Tests.AsistenteIa;

/// <summary>
/// Nivel 0 sobre toda la cartera: qué Tenants cuentan y dónde se lee la
/// instrucción de cada uno. Que el filtro de Tenant y RLS de verdad la escondan
/// fuera de su ámbito lo prueba <c>InstruccionIaCarteraBajoRlsTests</c>
/// (IntegrationTests); aquí el doble lo imita.
/// </summary>
public class ComprobarInstruccionIaCarteraQueryHandlerTests
{
    private static readonly ClienteAutorizadoDto Origen = new(Guid.NewGuid(), "Operador CAE externo", true);
    private static readonly ClienteAutorizadoDto ConInstruccion = new(Guid.NewGuid(), "Beneficiario con instrucción", false);
    private static readonly ClienteAutorizadoDto SinInstruccion = new(Guid.NewGuid(), "Beneficiario sin instrucción", false);
    private static readonly ClienteAutorizadoDto AlcanceCero = new(Guid.NewGuid(), "Beneficiario sin cartera", false);

    [Fact]
    public async Task Separa_los_Tenants_de_la_cartera_por_instruccion_y_deja_fuera_los_de_alcance_cero()
    {
        var resultado = await Handler(habilitados: [Origen.TenantId, ConInstruccion.TenantId])
            .Handle(new ComprobarInstruccionIaCarteraQuery(), default);

        resultado.ConInstruccion.Select(t => t.TenantId).Should().Equal(Origen.TenantId, ConInstruccion.TenantId);
        resultado.SinInstruccion.Select(t => t.TenantId).Should().Equal(SinInstruccion.TenantId);
        resultado.ErrorSiFalta()!.Mensaje.Should().Contain("Beneficiario sin instrucción")
            .And.NotContain("Beneficiario sin cartera", "un Tenant con alcance cero no es de la cartera");
    }

    [Fact]
    public async Task Un_Tenant_de_alcance_cero_sin_instruccion_no_bloquea()
    {
        var resultado = await Handler(habilitados: [Origen.TenantId, ConInstruccion.TenantId, SinInstruccion.TenantId])
            .Handle(new ComprobarInstruccionIaCarteraQuery(), default);

        resultado.SinInstruccion.Should().BeEmpty();
        resultado.ErrorSiFalta().Should().BeNull();
    }

    [Fact]
    public async Task La_instruccion_de_cada_Tenant_se_lee_dentro_de_su_ambito()
    {
        // El doble solo ve la instrucción de un Tenant desde su ámbito, como el
        // filtro de Tenant real: leída fuera, todos saldrían sin instrucción.
        var todos = new[] { Origen.TenantId, ConInstruccion.TenantId, SinInstruccion.TenantId, AlcanceCero.TenantId };

        var resultado = await Handler(habilitados: todos).Handle(new ComprobarInstruccionIaCarteraQuery(), default);

        resultado.SinInstruccion.Should().BeEmpty();
        AmbitoTenantExplicito.TenantIdActual.Should().BeNull("el ámbito se restaura al salir de cada vuelta");
    }

    [Fact]
    public void El_error_nombra_a_todos_los_Tenants_que_faltan()
    {
        var dto = new InstruccionIaCarteraDto([],
        [
            new(SinInstruccion.TenantId, SinInstruccion.Nombre, false),
            new(AlcanceCero.TenantId, "Otro beneficiario", false),
        ]);

        var error = dto.ErrorSiFalta()!;

        error.Codigo.Should().Be(InstruccionIaCarteraDto.CodigoError);
        error.Mensaje.Should().Contain("Beneficiario sin instrucción, Otro beneficiario");
    }

    private static ComprobarInstruccionIaCarteraQueryHandler Handler(Guid[] habilitados) =>
        new(new AutorizadosFalsos([Origen, ConInstruccion, SinInstruccion, AlcanceCero]),
            new AlcancePorAmbito(AlcanceCero.TenantId),
            new InstruccionSoloEnSuAmbito(habilitados));

    private sealed class InstruccionSoloEnSuAmbito(Guid[] habilitados) : IInstruccionTratamientoIaService
    {
        public Task<bool> EstaHabilitadaAsync(Guid tenantId, CancellationToken cancellationToken = default) =>
            Task.FromResult(AmbitoTenantExplicito.TenantIdActual == tenantId && habilitados.Contains(tenantId));
    }

    /// <summary>Alcance cero solo en <paramref name="tenantAlcanceCero"/>; acceso total en los demás.</summary>
    private sealed class AlcancePorAmbito(Guid tenantAlcanceCero) : IAlcanceDatosService
    {
        private IAlcanceDatosService Actual => AmbitoTenantExplicito.TenantIdActual == tenantAlcanceCero
            ? new AlcanceDatosServiceFalso(tieneAccesoTotal: false, clienteIdsVisibles: [])
            : new AlcanceDatosServiceFalso();

        public Task<bool> TieneAccesoTotalAsync(CancellationToken c = default) => Actual.TieneAccesoTotalAsync(c);
        public Task<IReadOnlyList<Guid>?> ObtenerClienteIdsVisiblesAsync(CancellationToken c = default) => Actual.ObtenerClienteIdsVisiblesAsync(c);
        public Task<IReadOnlyList<Guid>?> ObtenerCentroIdsVisiblesAsync(CancellationToken c = default) => Actual.ObtenerCentroIdsVisiblesAsync(c);
        public Task<IReadOnlyList<Guid>?> ObtenerCentroIdsParaGestionAsync(CancellationToken c = default) => Actual.ObtenerCentroIdsParaGestionAsync(c);
        public Task<IReadOnlyList<Guid>?> ObtenerEmpresaIdsVisiblesAsync(CancellationToken c = default) => Actual.ObtenerEmpresaIdsVisiblesAsync(c);
        public Task<IReadOnlyList<Guid>?> ObtenerEmpresaIdsParaGestionAsync(CancellationToken c = default) => Actual.ObtenerEmpresaIdsParaGestionAsync(c);
        public Task<IReadOnlyList<Guid>?> ObtenerSubcontrataIdsVisiblesAsync(CancellationToken c = default) => Actual.ObtenerSubcontrataIdsVisiblesAsync(c);
        public Task<IReadOnlyList<Guid>?> ObtenerSubcontrataIdsParaGestionAsync(CancellationToken c = default) => Actual.ObtenerSubcontrataIdsParaGestionAsync(c);
        public Task<IReadOnlyList<Guid>?> ObtenerTrabajadorIdsVisiblesAsync(CancellationToken c = default) => Actual.ObtenerTrabajadorIdsVisiblesAsync(c);
        public Task<IReadOnlyList<Guid>?> ObtenerVehiculoIdsVisiblesAsync(CancellationToken c = default) => Actual.ObtenerVehiculoIdsVisiblesAsync(c);
        public Task<bool> ConexionIntegracionVisibleAsync(Guid id, CancellationToken c = default) => Actual.ConexionIntegracionVisibleAsync(id, c);
    }

    private sealed class AutorizadosFalsos(IReadOnlyList<ClienteAutorizadoDto> autorizados) : IMediator
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
