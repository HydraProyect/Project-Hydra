using Bunit;
using CaeManager.Application.Common;
using CaeManager.Application.Comunicaciones.Commands.EnviarMensajeNuevo;
using CaeManager.Application.Integraciones.Queries.ObtenerConexionesIntegracion;
using CaeManager.Domain.Common;
using CaeManager.Domain.Integraciones;
using CaeManager.Web.Components.DesignSystem;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// P1-E2b: salir de la pantalla que abrió «Redactar mensaje» con el mensaje a medias pregunta
/// antes. El asunto y el cuerpo que trae el llamador y el único buzón preseleccionado no son
/// cambios, y la navegación que el llamador lance al recibir OnEnviado no pregunta.
/// </summary>
public class RedactarMensajeDrawerAvisoCambiosSinGuardarTests : BunitContext
{
    public RedactarMensajeDrawerAvisoCambiosSinGuardarTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLocalization();
        Services.AddScoped<IMediator>(_ => new MediatorFalso(_cargaDeBuzones.Task));
        Services.AddScoped<ToastService>();
    }

    /// <summary>La carga de buzones: completa salvo en la prueba que la retiene.</summary>
    private TaskCompletionSource _cargaDeBuzones = CargaCompletada();

    private static TaskCompletionSource CargaCompletada()
    {
        var carga = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        carga.SetResult();
        return carga;
    }

    private sealed class MediatorFalso(Task cargaDeBuzones) : IMediator
    {
        public async Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            if (request is ObtenerConexionesIntegracionQuery)
                await cargaDeBuzones;
            return Responder(request);
        }

        private static TResponse Responder<TResponse>(IRequest<TResponse> request) =>
            (TResponse)(object)(request switch
            {
                ObtenerConexionesIntegracionQuery => (IReadOnlyList<ConexionIntegracionListaDto>)
                    [new(Guid.NewGuid(), "cae@example.invalid", "CAE Norte", null, null, EstadoConexionIntegracion.Habilitada, DateTime.UtcNow, null, null)],
                EnviarMensajeNuevoCommand => Result.Exito(Guid.NewGuid()),
                _ => throw new NotSupportedException($"Consulta no prevista en este test: {request.GetType().Name}.")
            });

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

    private (IRenderedComponent<RedactarMensajeDrawer> Cut, NavigationManager Navegacion) Renderizar(Action? alEnviar = null)
    {
        var navegacion = Services.GetRequiredService<NavigationManager>();
        var cut = Render<RedactarMensajeDrawer>(p => p
            .Add(x => x.Visible, true)
            .Add(x => x.AsuntoInicial, "Informe de cumplimiento")
            .Add(x => x.CuerpoInicial, "Adjunto el informe del mes.")
            .Add(x => x.OnEnviado, () => alEnviar?.Invoke()));
        cut.WaitForAssertion(() => cut.FindAll(".drawer-panel").Should().NotBeEmpty());
        return (cut, navegacion);
    }

    private static async Task EscribirDestinatarioAsync(IRenderedComponent<RedactarMensajeDrawer> cut)
    {
        var destinatario = cut.FindAll(".drawer-panel input.campo-input")[0];
        await destinatario.InputAsync("contacto@example.invalid");
        await destinatario.BlurAsync();
    }

    [Fact]
    public async Task Salir_con_el_mensaje_a_medias_pregunta()
    {
        var (cut, navegacion) = Renderizar();
        await EscribirDestinatarioAsync(cut);

        await cut.SalirYComprobarQuePreguntaAsync(navegacion);
    }

    [Fact]
    public async Task Lo_que_trae_el_llamador_no_es_un_cambio()
    {
        var (cut, navegacion) = Renderizar();
        cut.FindComponents<CampoTexto>().Should().Contain(c => c.Instance.Valor == "Informe de cumplimiento",
            "el test necesita que el asunto llegue prellenado por el llamador");

        await cut.SalirYComprobarQueNoPreguntaAsync(navegacion, "abrir el drawer con lo prellenado no deja nada que perder");
    }

    [Fact]
    public async Task La_navegacion_del_llamador_tras_enviar_no_pregunta()
    {
        NavigationManager? navegacion = null;
        var (cut, nav) = Renderizar(() => navegacion!.NavigateTo("/reportes"));
        navegacion = nav;
        await EscribirDestinatarioAsync(cut);

        await cut.FindAll(".drawer-pie button").Single(b => b.TextContent.Trim() == "Enviar").ClickAsync(new MouseEventArgs());

        nav.Uri.Should().EndWith("/reportes", "lo escrito ya se envió: la navegación del llamador no pregunta");
        cut.FindAll(".modal-pie button").Should().NotContain(b => b.TextContent.Trim() == "Salir y descartar");
    }

    /// <summary>
    /// Revisión Codex (lote C): lo tecleado mientras cargan los buzones es un cambio. Al
    /// preseleccionar el único buzón solo cambia ese valor de partida, no los demás.
    /// </summary>
    [Fact]
    public async Task Lo_tecleado_mientras_cargan_los_buzones_es_un_cambio()
    {
        _cargaDeBuzones = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var navegacion = Services.GetRequiredService<NavigationManager>();
        var cut = Render<RedactarMensajeDrawer>(p => p
            .Add(x => x.Visible, true)
            .Add(x => x.AsuntoInicial, "Informe de cumplimiento"));
        cut.WaitForAssertion(() => cut.FindAll(".drawer-panel input.campo-input").Should().NotBeEmpty());

        await EscribirDestinatarioAsync(cut);
        _cargaDeBuzones.SetResult();
        cut.WaitForAssertion(() => cut.Instance.Should().NotBeNull());

        await cut.SalirYComprobarQuePreguntaAsync(navegacion);
    }
}
