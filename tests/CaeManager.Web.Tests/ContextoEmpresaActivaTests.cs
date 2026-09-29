using CaeManager.Application.Tenants.Queries.ObtenerClientesAutorizados;
using CaeManager.Web.Components.Layout;
using FluentAssertions;
using MediatR;

namespace CaeManager.Web.Tests;

/// <summary>
/// Contrato de <see cref="ContextoEmpresaActiva.ResolverAsync"/>, compartido por Subcontratas, Empresas y
/// Documentos: en el estado 4a no hay empresa activa (si no, la cabecera enseñaría el origen como elegido) y
/// una resolución cancelada es una salida normal, no una excepción.
/// </summary>
public class ContextoEmpresaActivaTests
{
    private static readonly Guid Origen = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid Norte = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002");
    private static readonly Guid Sur = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000003");

    private static readonly IReadOnlyList<ClienteAutorizadoDto> Cartera =
    [
        new(Origen, "Operador de prueba", EsOrigen: true, EsGestionadoPorOperacion: false),
        new(Norte, "Empresa Norte", EsOrigen: false, EsGestionadoPorOperacion: true),
        new(Sur, "Empresa Sur", EsOrigen: false, EsGestionadoPorOperacion: true),
    ];

    private sealed class Mediador(Func<CancellationToken, Task<IReadOnlyList<ClienteAutorizadoDto>>> responder) : IMediator
    {
        public List<CancellationToken> Tokens { get; } = [];

        public async Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            Tokens.Add(cancellationToken);
            return (TResponse)(object)await responder(cancellationToken);
        }

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest => throw new NotSupportedException();
        public Task<object?> Send(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task Publish(object notification, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default) where TNotification : INotification => throw new NotSupportedException();
    }

    [Fact]
    public async Task En_el_estado_4a_no_hay_empresa_activa_y_se_pide_elegir()
    {
        var mediador = new Mediador(_ => Task.FromResult(Cartera));

        var contexto = await ContextoEmpresaActiva.ResolverAsync(mediador, new SeleccionEmpresaGestionadaDePrueba(Origen));

        contexto.SinSeleccion.Should().BeTrue("control: el origen sin gestionar obliga a elegir");
        contexto.Activa.Should().BeNull("el origen no es la empresa elegida; la cabecera no debe enseñarlo como tal");
    }

    [Fact]
    public async Task Con_una_empresa_elegida_es_la_activa_y_no_hay_4a()
    {
        var mediador = new Mediador(_ => Task.FromResult(Cartera));

        var contexto = await ContextoEmpresaActiva.ResolverAsync(mediador, new SeleccionEmpresaGestionadaDePrueba(Sur));

        contexto.SinSeleccion.Should().BeFalse();
        contexto.Activa!.Nombre.Should().Be("Empresa Sur");
    }

    [Fact]
    public async Task El_token_llega_a_la_consulta_y_una_resolucion_cancelada_es_una_salida_normal()
    {
        using var cts = new CancellationTokenSource();
        var mediador = new Mediador(async ct =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return Cartera;
        });

        var resolucion = ContextoEmpresaActiva.ResolverAsync(mediador, new SeleccionEmpresaGestionadaDePrueba(Sur), cts.Token);
        mediador.Tokens.Should().ContainSingle().Which.Should().Be(cts.Token, "sin el token la consulta sigue viva tras retirar la página");

        cts.Cancel();
        var contexto = await resolucion;

        contexto.Should().Be(ContextoEmpresaActiva.Ninguno);
    }

    [Fact]
    public async Task Una_excepcion_de_cancelacion_ajena_al_token_no_se_traga()
    {
        var mediador = new Mediador(_ => throw new OperationCanceledException());

        var accion = () => ContextoEmpresaActiva.ResolverAsync(mediador, new SeleccionEmpresaGestionadaDePrueba(Sur));

        await accion.Should().ThrowAsync<OperationCanceledException>("solo se trata como normal la cancelación pedida por quien llama");
    }
}
