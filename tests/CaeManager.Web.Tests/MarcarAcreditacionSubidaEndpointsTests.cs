using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using CaeManager.Application.Common;
using CaeManager.Application.Documentos.Commands.CambiarEstadoAcreditacionEnCentro;
using CaeManager.Application.Documentos.Commands.ConfirmarSubidaDesdeExtension;
using CaeManager.Application.Documentos.Commands.MarcarAcreditacionSubida;
using CaeManager.Domain.Common;
using CaeManager.Infrastructure.Autenticacion;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Features.Extension;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CaeManager.Web.Tests;

/// <summary>
/// «Lo he enviado» y su «Deshacer» (incremento E1 de la extensión): cómo responde cada fallo del comando y qué peticiones
/// mal formadas se paran antes de llegar a él. La antiforgery de las mismas rutas está en
/// <see cref="EntradasExternasAntiforgeryTests"/>.
/// </summary>
public class MarcarAcreditacionSubidaEndpointsTests
{
    private const string Id = "11111111-1111-1111-1111-111111111111";
    private const string Subida = "/extension/acreditaciones/" + Id + "/subida";
    private const string DeshacerSubida = Subida + "/deshacer";
    private const string IdVacio = "00000000-0000-0000-0000-000000000000";

    public static TheoryData<string, HttpStatusCode> FallosDeLaConfirmacion => new()
    {
        // No existe y fuera de cartera son el mismo código, a propósito: no se filtra cuál de los dos es.
        { "Acreditacion.NoEncontrada", HttpStatusCode.NotFound },
        { ConfirmarSubidaDesdeExtensionCommandHandler.CodigoConectorInactivo, HttpStatusCode.Conflict },
        { MarcarAcreditacionSubidaCommandHandler.CodigoRechazadaSinVersionNueva, HttpStatusCode.Conflict },
    };

    public static TheoryData<string, HttpStatusCode> FallosDelDeshacer => new()
    {
        { "Acreditacion.NoEncontrada", HttpStatusCode.NotFound },
        { DeshacerSubidaDesdeExtensionCommandHandler.CodigoYaNoSePuedeDeshacer, HttpStatusCode.Conflict },
        { ConcurrenciaOptimista.CodigoConflicto, HttpStatusCode.Conflict },
    };

    [Theory]
    [MemberData(nameof(FallosDeLaConfirmacion))]
    public async Task Cada_fallo_de_la_confirmacion_responde_su_codigo_con_el_detalle(string codigo, HttpStatusCode esperado)
    {
        var mediador = new Mediador(Error.Crear(codigo, "Detalle para el Gestor CAE"));
        await using var app = await ArrancarAsync(mediador);
        using var cliente = Cliente(app);

        using var respuesta = await cliente.PostAsync(Subida, null);

        respuesta.StatusCode.Should().Be(esperado);
        (await respuesta.Content.ReadAsStringAsync()).Should().Contain("Detalle para el Gestor CAE",
            "la extensión muestra el detalle real, no un genérico");
    }

    [Theory]
    [MemberData(nameof(FallosDelDeshacer))]
    public async Task Cada_fallo_del_deshacer_responde_su_codigo_con_el_detalle(string codigo, HttpStatusCode esperado)
    {
        var mediador = new Mediador(Error.Crear(codigo, "Corrígelo desde Centro 360"));
        await using var app = await ArrancarAsync(mediador);
        using var cliente = Cliente(app);

        using var respuesta = await cliente.PostAsync(DeshacerSubida, Cuerpo(Guid.NewGuid(), Guid.NewGuid()));

        respuesta.StatusCode.Should().Be(esperado);
        (await respuesta.Content.ReadAsStringAsync()).Should().Contain("Corrígelo desde Centro 360");
    }

    [Theory]
    [InlineData("/extension/acreditaciones/" + IdVacio + "/subida")]
    [InlineData("/extension/acreditaciones/" + IdVacio + "/subida/deshacer")]
    public async Task Un_id_vacio_responde_404_sin_llegar_al_comando(string ruta)
    {
        var mediador = new Mediador(error: null);
        await using var app = await ArrancarAsync(mediador);
        using var cliente = Cliente(app);

        using var respuesta = await cliente.PostAsync(ruta, Cuerpo(Guid.NewGuid(), Guid.NewGuid()));

        respuesta.StatusCode.Should().Be(HttpStatusCode.NotFound);
        mediador.Enviados.Should().BeEmpty();
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Deshacer_sin_cambio_o_sin_version_responde_400_sin_llegar_al_comando(bool sinCambio, bool sinVersion)
    {
        var mediador = new Mediador(error: null);
        await using var app = await ArrancarAsync(mediador);
        using var cliente = Cliente(app);

        using var respuesta = await cliente.PostAsync(DeshacerSubida,
            Cuerpo(sinCambio ? Guid.Empty : Guid.NewGuid(), sinVersion ? Guid.Empty : Guid.NewGuid()));

        respuesta.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        mediador.Enviados.Should().BeEmpty();
    }

    [Fact]
    public async Task La_confirmacion_correcta_devuelve_el_cambio_y_la_version_para_el_deshacer()
    {
        var mediador = new Mediador(error: null);
        await using var app = await ArrancarAsync(mediador);
        using var cliente = Cliente(app);

        using var respuesta = await cliente.PostAsync(Subida, null);

        respuesta.StatusCode.Should().Be(HttpStatusCode.OK);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        cuerpo.Should().Contain(mediador.CambioId.ToString()).And.Contain(mediador.Version.ToString());
        mediador.Enviados.Should().ContainSingle().Which.Should().BeOfType<ConfirmarSubidaDesdeExtensionCommand>()
            .Which.AcreditacionId.Should().Be(Guid.Parse(Id));
    }

    private static StringContent Cuerpo(Guid cambioId, Guid version) => new(
        $"{{\"cambioId\":\"{cambioId}\",\"version\":\"{version}\"}}", Encoding.UTF8, "application/json");

    private static HttpClient Cliente(WebApplication app)
    {
        var cliente = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            BaseAddress = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First()),
        };
        cliente.DefaultRequestHeaders.Add("Authorization", "Extension valido");
        return cliente;
    }

    private static async Task<WebApplication> ArrancarAsync(IMediator mediador)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddLogging();
        builder.Services.AddProblemDetails();
        builder.Services.AddAuthentication(ExtensionAuthenticationSchemeOptions.NombreEsquema)
            .AddScheme<AuthenticationSchemeOptions, ExtensionFalsa>(ExtensionAuthenticationSchemeOptions.NombreEsquema, _ => { });
        builder.Services.AddAuthorization(o => o.AddPolicy(Policies.SesionOExtension, p => p
            .AddAuthenticationSchemes(ExtensionAuthenticationSchemeOptions.NombreEsquema)
            .RequireAuthenticatedUser()));
        builder.Services.AddSingleton(mediador);
        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapMarcarAcreditacionSubidaEndpoints();
        await app.StartAsync();
        return app;
    }

    private sealed class ExtensionFalsa(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(
            Request.Headers.Authorization == "Extension valido"
                ? AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString())], Scheme.Name)), Scheme.Name))
                : AuthenticateResult.NoResult());
    }

    /// <summary>Devuelve <paramref name="error"/> a cualquier comando, o éxito si es null, y anota lo que recibe.</summary>
    private sealed class Mediador(Error? error) : IMediator
    {
        public List<object> Enviados { get; } = [];
        public Guid CambioId { get; } = Guid.NewGuid();
        public Guid Version { get; } = Guid.NewGuid();

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            Enviados.Add(request);
            object respuesta = request switch
            {
                ConfirmarSubidaDesdeExtensionCommand => error is null
                    ? Result.Exito(new CambioEstadoAnotadoDto(CambioId, Version))
                    : Result.Fallo<CambioEstadoAnotadoDto>(error),
                _ => error is null ? Result.Exito() : Result.Fallo(error),
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
}
