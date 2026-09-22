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
/// este test lo vería. No envía preguntas: el mediador no se llega a usar.
/// </summary>
public class AsistenteIaTextosTests : BunitContext
{
    private sealed class MediatorSinUso : IMediator
    {
        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

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

    [Fact]
    public void Panel_abierto_muestra_los_textos_de_los_recursos()
    {
        Services.AddLocalization();
        Services.AddScoped<AsistenteIaService>();
        Services.AddScoped<IMediator, MediatorSinUso>();
        JSInterop.Mode = JSRuntimeMode.Loose;

        var panel = Render<AsistenteIa>();
        panel.InvokeAsync(() => Services.GetRequiredService<AsistenteIaService>().Abrir());

        panel.Find("#asistente-titulo").TextContent.Should().Be("Pregúntale a Hydra");
        panel.Find("button.asistente-cerrar").GetAttribute("aria-label").Should().Be("Cerrar");
        panel.Find(".asistente-descripcion").TextContent.Should().StartWith("Especialista en legislación de Prevención");
        panel.Find(".asistente-mensaje-vacio").TextContent.Should().Be("Escribe tu pregunta sobre PRL para empezar.");
        panel.Find("textarea.asistente-textarea").GetAttribute("placeholder").Should().Be("Escribe tu pregunta…");
        panel.Find("button.asistente-boton-enviar").TextContent.Trim().Should().Be("Enviar");
    }
}
