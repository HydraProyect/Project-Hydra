using CaeManager.Application.AsistenteIa.Candidatos;
using CaeManager.Application.Common;
using CaeManager.Application.Cumplimiento;
using CaeManager.Application.Tenants.Queries.ObtenerClientesAutorizados;
using FluentAssertions;
using MediatR;
using Xunit;

namespace CaeManager.Application.Tests.AsistenteIa;

/// <summary>
/// Nivel 0 sobre toda la cartera: que cuentan todos los Tenants autorizados y dónde se lee la
/// instrucción de cada uno. Que el filtro de Tenant y RLS de verdad la escondan
/// fuera de su ámbito lo prueba <c>InstruccionIaCarteraBajoRlsTests</c>
/// (IntegrationTests); aquí el doble lo imita.
/// </summary>
public class ComprobarInstruccionIaCarteraQueryHandlerTests
{
    private static readonly ClienteAutorizadoDto Origen = new(Guid.NewGuid(), "Operador CAE externo", true);
    private static readonly ClienteAutorizadoDto ConInstruccion = new(Guid.NewGuid(), "Beneficiario con instrucción", false);
    private static readonly ClienteAutorizadoDto SinInstruccion = new(Guid.NewGuid(), "Beneficiario sin instrucción", false);
    private static readonly ClienteAutorizadoDto SinCartera = new(Guid.NewGuid(), "Beneficiario sin cartera", false);

    [Fact]
    public async Task Separa_los_Tenants_autorizados_por_instruccion()
    {
        var resultado = await Handler(habilitados: [Origen.TenantId, ConInstruccion.TenantId, SinCartera.TenantId])
            .Handle(new ComprobarInstruccionIaCarteraQuery(), default);

        resultado.ConInstruccion.Select(t => t.TenantId).Should().Equal(Origen.TenantId, ConInstruccion.TenantId, SinCartera.TenantId);
        resultado.SinInstruccion.Select(t => t.TenantId).Should().Equal(SinInstruccion.TenantId);
        resultado.ErrorSiFalta()!.Mensaje.Should().Contain("Beneficiario sin instrucción");
    }

    [Fact]
    public async Task Un_Tenant_autorizado_sin_cartera_tambien_cuenta()
    {
        // Revisión Codex: el criterio de alcance cero (sin Clientes empresariales en
        // cartera) también lo cumple una cartera universal que sí ve Trabajadores.
        // Ante la duda, falla cerrado: ningún Tenant autorizado se descarta.
        var resultado = await Handler(habilitados: [Origen.TenantId, ConInstruccion.TenantId, SinInstruccion.TenantId])
            .Handle(new ComprobarInstruccionIaCarteraQuery(), default);

        resultado.SinInstruccion.Select(t => t.TenantId).Should().Equal(SinCartera.TenantId);
        resultado.ErrorSiFalta()!.Mensaje.Should().Contain("Beneficiario sin cartera");
    }

    [Fact]
    public async Task La_instruccion_de_cada_Tenant_se_lee_dentro_de_su_ambito()
    {
        // El doble solo ve la instrucción de un Tenant desde su ámbito, como el
        // filtro de Tenant real: leída fuera, todos saldrían sin instrucción.
        var todos = new[] { Origen.TenantId, ConInstruccion.TenantId, SinInstruccion.TenantId, SinCartera.TenantId };

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
            new(SinCartera.TenantId, "Otro beneficiario", false),
        ]);

        var error = dto.ErrorSiFalta()!;

        error.Codigo.Should().Be(InstruccionIaCarteraDto.CodigoError);
        error.Mensaje.Should().Contain("Beneficiario sin instrucción, Otro beneficiario");
    }

    private static ComprobarInstruccionIaCarteraQueryHandler Handler(Guid[] habilitados) =>
        new(new AutorizadosFalsos([Origen, ConInstruccion, SinInstruccion, SinCartera]),
            new InstruccionSoloEnSuAmbito(habilitados));

    private sealed class InstruccionSoloEnSuAmbito(Guid[] habilitados) : IInstruccionTratamientoIaService
    {
        public Task<bool> EstaHabilitadaAsync(Guid tenantId, CancellationToken cancellationToken = default) =>
            Task.FromResult(AmbitoTenantExplicito.TenantIdActual == tenantId && habilitados.Contains(tenantId));
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
