using Bunit;
using CaeManager.Web.Features.AsistenteIa;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// Los textos del panel del asistente de IA salen de <c>TextosAsistenteIa</c>.
/// Las claves no coinciden con su texto a propósito: si el localizador no
/// encontrara el recurso devolvería el nombre de la clave («MensajeVacio»), y
/// estos tests lo verían.
/// </summary>
public class AsistenteIaTextosTests : BunitContext
{
    /// <summary>La pregunta queda en vuelo para siempre: el panel se queda en «Pensando…».</summary>
    private sealed class MediatorQueNoResponde : IMediator
    {
        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            new TaskCompletionSource<TResponse>().Task;

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest =>
            throw new NotSupportedException();

        public Task<object?> Send(object request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification => Task.CompletedTask;
    }

    public AsistenteIaTextosTests()
    {
        Services.AddLocalization();
        Services.AddScoped<AsistenteIaService>();
        Services.AddScoped<IMediator, MediatorQueNoResponde>();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private IRenderedComponent<AsistenteIa> RenderAbierto()
    {
        var panel = Render<AsistenteIa>();
        panel.InvokeAsync(() => Services.GetRequiredService<AsistenteIaService>().Abrir());
        return panel;
    }

    [Fact]
    public void Panel_abierto_muestra_los_textos_de_los_recursos()
    {
        var panel = RenderAbierto();

        panel.Find("#asistente-titulo").TextContent.Should().Be("Pregúntale a Hydra");
        panel.Find("button.asistente-cerrar").GetAttribute("aria-label").Should().Be("Cerrar");
        panel.Find(".asistente-descripcion").TextContent.Should().Be(
            "Especialista en legislación de Prevención de Riesgos Laborales (España/UE). No tiene acceso a tus Clientes, Trabajadores ni Documentos — solo responde dudas normativas generales.");
        panel.Find(".asistente-mensaje-vacio").TextContent.Should().Be("Escribe tu pregunta sobre PRL para empezar.");
        panel.Find("textarea.asistente-textarea").GetAttribute("placeholder").Should().Be("Escribe tu pregunta…");
        panel.Find("button.asistente-boton-enviar").TextContent.Trim().Should().Be("Enviar");
    }

    [Fact]
    public void Con_la_pregunta_en_vuelo_muestra_Pensando()
    {
        var panel = RenderAbierto();

        panel.Find("textarea.asistente-textarea").Input("¿Qué es un plan de seguridad?");
        panel.Find("button.asistente-boton-enviar").Click();

        panel.Find(".asistente-mensaje-cargando").TextContent.Should().Be("Pensando…");
    }
}
