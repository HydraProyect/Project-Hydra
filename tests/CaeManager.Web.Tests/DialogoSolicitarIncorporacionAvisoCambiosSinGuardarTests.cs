using Bunit;
using CaeManager.Application.Operaciones.IncorporacionCartera.Commands;
using CaeManager.Application.Operaciones.IncorporacionCartera.Queries;
using CaeManager.Domain.Common;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.IncorporacionCartera.Components;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// P1-E2b: salir con el diálogo de solicitud de incorporación a cartera a medias pregunta
/// antes. La Empresa (Tenant propietario) preseleccionada al abrir no es un cambio, y la
/// solicitud ya enviada no deja nada que perder aunque quien abrió el diálogo aún no lo
/// haya cerrado.
/// </summary>
public class DialogoSolicitarIncorporacionAvisoCambiosSinGuardarTests : BunitContext
{
    private static readonly Guid TenantPropietario = Guid.NewGuid();

    public DialogoSolicitarIncorporacionAvisoCambiosSinGuardarTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLocalization();
        Services.AddSingleton<IMediator>(_mediador);
        Services.AddSingleton<ToastService>();
    }

    private readonly MediatorFalso _mediador = new();

    private sealed class MediatorFalso : IMediator
    {
        public List<object> Enviadas { get; } = [];

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            Enviadas.Add(request);
            return Task.FromResult((TResponse)(object)(request switch
            {
                ObtenerCandidatosIncorporacionCarteraQuery => Result.Exito<IReadOnlyList<CandidatoIncorporacionCarteraDto>>(
                    [new CandidatoIncorporacionCarteraDto(TenantPropietario, "Refrigeración Norte", null)]),
                SolicitarIncorporacionCarteraCommand => Result.Exito(Guid.NewGuid()),
                _ => throw new NotSupportedException($"Petición no prevista en este test: {request.GetType().Name}.")
            }));
        }

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest =>
            Task.CompletedTask;

        public Task<object?> Send(object request, CancellationToken cancellationToken = default) =>
            Task.FromResult<object?>(null);

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification => Task.CompletedTask;
    }

    private NavigationManager Navegacion => Services.GetRequiredService<NavigationManager>();

    private bool? _visibleNotificado;

    /// <summary>El padre no vuelve a pintar el diálogo al recibir VisibleChanged: se queda visible.</summary>
    private IRenderedComponent<DialogoSolicitarIncorporacion> Renderizar()
    {
        var cut = Render<DialogoSolicitarIncorporacion>(p => p
            .Add(c => c.Visible, true)
            .Add(c => c.VisibleChanged, v => _visibleNotificado = v));
        cut.WaitForAssertion(() => cut.FindAll("textarea").Should().NotBeEmpty());
        return cut;
    }

    private static Task EscribirMensajeAsync(IRenderedComponent<DialogoSolicitarIncorporacion> cut, string mensaje) =>
        cut.Find("textarea").InputAsync(new ChangeEventArgs { Value = mensaje });

    [Fact]
    public async Task La_Empresa_preseleccionada_al_abrir_no_es_un_cambio()
    {
        var cut = Renderizar();
        cut.Find("select").GetAttribute("value").Should().Be(TenantPropietario.ToString(),
            "el test necesita que la única Empresa llegue preseleccionada");

        await cut.SalirYComprobarQueNoPreguntaAsync(Navegacion, "lo preseleccionado no es un cambio de quien escribe");
    }

    [Fact]
    public async Task Salir_con_el_mensaje_escrito_pregunta_y_descartar_cierra_el_dialogo()
    {
        var cut = Renderizar();
        await EscribirMensajeAsync(cut, "Llevo su coordinación desde enero.");

        await cut.SalirYComprobarQuePreguntaAsync(Navegacion);
        await cut.PulsarEnElAvisoAsync("Salir y descartar");

        _visibleNotificado.Should().BeFalse("descartar cierra el diálogo antes de salir");
        Navegacion.Uri.Should().EndWith(AvisoCambiosSinGuardarPrueba.DestinoFuera);
    }

    [Fact]
    public async Task Enviada_la_solicitud_salir_no_pregunta()
    {
        var cut = Renderizar();
        await EscribirMensajeAsync(cut, "Llevo su coordinación desde enero.");

        await cut.FindAll("button").Single(b => b.TextContent.Trim() == "Enviar solicitud").ClickAsync(new MouseEventArgs());
        _mediador.Enviadas.Should().Contain(p => p is SolicitarIncorporacionCarteraCommand, "si no se envió, el test no mide nada");

        await cut.SalirYComprobarQueNoPreguntaAsync(Navegacion, "la solicitud ya está enviada");
    }
}
