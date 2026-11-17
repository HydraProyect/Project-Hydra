using System.Security.Claims;
using CaeManager.Application.Inbound.Comercial.Queries.LineaInboundVigente;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Components.Layout;
using CaeManager.Web.Features.Inbound;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// El gate de la línea Inbound en la interfaz: <see cref="LineaInboundVigenteDelProductoContratado"/> responde lo que dice
/// <see cref="LineaInboundVigenteQuery"/> y falla cerrado ante un error, y <see cref="AdmisionDeDestinos.ContextoAsync"/>
/// lleva esa respuesta al contexto con el que la paleta y los atajos deciden, el mismo criterio que la sección «Inbound»
/// del menú lateral. La regla de qué cuenta como vigente la prueban los tests de la Query (Application) y su lectura bajo
/// <c>cae_app_runtime</c> (integración, en CI).
/// </summary>
public class LineaInboundVigenteGateTests
{
    // ------------------------------------------------ implementación del puerto

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task El_puerto_responde_lo_que_dice_la_Query(bool vigente)
    {
        var mediador = new MediadorDeLaLinea(_ => vigente);

        (await new LineaInboundVigenteDelProductoContratado(mediador).EstaVigenteAsync()).Should().Be(vigente);
        mediador.Enviadas.Should().ContainSingle().Which.Should().BeOfType<LineaInboundVigenteQuery>();
    }

    [Fact]
    public async Task Si_la_Query_falla_o_la_deniega_un_behavior_el_puerto_falla_cerrado()
    {
        var mediador = new MediadorDeLaLinea(_ => throw new UnauthorizedAccessException("denegada por un behavior"));

        (await new LineaInboundVigenteDelProductoContratado(mediador).EstaVigenteAsync()).Should().BeFalse(
            "un error o una denegación no enseñan la sección Inbound");
    }

    [Fact]
    public async Task Una_cancelacion_se_propaga_y_no_se_lee_como_un_no()
    {
        using var cancelacion = new CancellationTokenSource();
        await cancelacion.CancelAsync();
        var mediador = new MediadorDeLaLinea(ct => throw new OperationCanceledException(ct));

        var leer = () => new LineaInboundVigenteDelProductoContratado(mediador).EstaVigenteAsync(cancelacion.Token);

        await leer.Should().ThrowAsync<OperationCanceledException>("una cancelación no es una respuesta");
    }

    // --------------------------------------------------- AdmisionDeDestinos

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task El_contexto_de_la_paleta_y_los_atajos_lleva_la_linea_Inbound_del_puerto(bool vigente)
    {
        var contexto = await AdmisionDeDestinos.ContextoAsync(
            Sesion(Roles.Administrador), Servicios(new LineaFija(vigente)), consultarPlataforma: false);

        contexto!.LineaInboundVigente.Should().Be(vigente);
        CatalogoMenuLateral.Visibles(contexto).Any(g => g.Grupo.Id == "inbound").Should().Be(vigente,
            "la paleta y los atajos deciden con el criterio del menú lateral, y ese criterio tiene el gate de la línea");
    }

    [Fact]
    public async Task Si_el_puerto_falla_el_contexto_no_tiene_la_linea_Inbound()
    {
        var contexto = await AdmisionDeDestinos.ContextoAsync(
            Sesion(Roles.Administrador), Servicios(new LineaQueFalla()), consultarPlataforma: true);

        contexto!.LineaInboundVigente.Should().BeFalse("el gate falla cerrado y la paleta sigue funcionando");
    }

    [Fact]
    public async Task Sin_el_puerto_registrado_el_contexto_no_tiene_la_linea_Inbound()
    {
        var contexto = await AdmisionDeDestinos.ContextoAsync(
            Sesion(Roles.Administrador), new ServiceCollection().BuildServiceProvider(), consultarPlataforma: false);

        contexto!.LineaInboundVigente.Should().BeFalse();
    }

    [Fact]
    public async Task Sin_sesion_no_se_consulta_la_linea_Inbound()
    {
        var linea = new LineaFija(true);

        var contexto = await AdmisionDeDestinos.ContextoAsync(
            Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity()))), Servicios(linea),
            consultarPlataforma: false);

        contexto!.LineaInboundVigente.Should().BeFalse("una sesión anónima no ve ningún grupo del menú");
        linea.Consultas.Should().Be(0);
    }

    private static Task<AuthenticationState> Sesion(string rol) =>
        Task.FromResult(new AuthenticationState(
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, rol)], "prueba"))));

    private static IServiceProvider Servicios(ILineaInboundVigente linea)
    {
        var servicios = new ServiceCollection();
        servicios.AddSingleton(linea);
        return servicios.BuildServiceProvider();
    }

    private sealed class LineaFija(bool vigente) : ILineaInboundVigente
    {
        public int Consultas { get; private set; }

        public Task<bool> EstaVigenteAsync(CancellationToken cancellationToken = default)
        {
            Consultas++;
            return Task.FromResult(vigente);
        }
    }

    private sealed class LineaQueFalla : ILineaInboundVigente
    {
        public Task<bool> EstaVigenteAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("sin base");
    }

    /// <summary>Solo responde a <see cref="LineaInboundVigenteQuery"/>; cualquier otra petición es un error del test.</summary>
    private sealed class MediadorDeLaLinea(Func<CancellationToken, bool> respuesta) : IMediator
    {
        public ListaSegura<object> Enviadas { get; } = [];

        public async Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            Enviadas.Add(request);
            return request is LineaInboundVigenteQuery
                ? (TResponse)(object)respuesta(cancellationToken)
                : throw new NotSupportedException($"Petición no prevista en este test: {request.GetType().Name}.");
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
}
