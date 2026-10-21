using Bunit;
using CaeManager.Application.Common;
using CaeManager.Infrastructure.Comunicaciones;
using CaeManager.Web.Features.AsistenteIa;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// Los textos del panel de Vega salen de <c>TextosAsistenteIa</c>.
/// Las claves no coinciden con su texto a propósito: si el localizador no
/// encontrara el recurso devolvería el nombre de la clave («AvisoIa»), y
/// estos tests lo verían.
/// </summary>
public class AsistenteIaTextosTests : BunitContext
{
    /// <summary>La pregunta queda en vuelo para siempre: el panel se queda en «Vega está pensando».</summary>
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
        Services.AddSingleton<Microsoft.Extensions.Options.IOptions<CaeManager.Infrastructure.AsistenteIa.AnthropicOptions>>(
            Microsoft.Extensions.Options.Options.Create(new CaeManager.Infrastructure.AsistenteIa.AnthropicOptions { ApiKey = "sk-ant-de-prueba" }));
        Services.AddSingleton<Microsoft.Extensions.Options.IOptions<CaeManager.Infrastructure.AsistenteIa.TypeSafeOptions>>(
            Microsoft.Extensions.Options.Options.Create(new CaeManager.Infrastructure.AsistenteIa.TypeSafeOptions()));
        Services.AddScoped<DisponibilidadAsistente>();
        Services.AddScoped<IMediator, MediatorQueNoResponde>();
        Services.AddSingleton<ISanitizadorHtmlService, GanssSanitizadorHtmlService>();
        Services.AddSingleton<IClienteActivoSeleccionado>(new SeleccionEmpresaGestionadaDePrueba());
        Services.AddSingleton<ITenantActual>(new TenantEfectivoDePrueba(Guid.NewGuid()));
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

        panel.Find("#vega-titulo").TextContent.Should().Be("Vega");
        panel.Find(".vega-identidad small").TextContent.Should().Be($"Asistente IA de {Marca.Nombre}");
        panel.Find(".vega-aviso").TextContent.Trim().Should().Be(
            "Vega es una inteligencia artificial y puede equivocarse. Nada se ejecuta sin tu confirmación.");
        panel.Find(".vega-nueva").GetAttribute("aria-label").Should().Be("Nueva conversación");
        panel.Find(".vega-pantalla-completa").GetAttribute("href").Should().Be("/asistente");
        panel.Find(".vega-plegar").GetAttribute("aria-label").Should().Be("Plegar (Ctrl+I)");
        panel.Find(".vega-bienvenida h3").TextContent.Should().Be("Hola. Soy Vega.");
        panel.Find("textarea.asistente-textarea").GetAttribute("placeholder").Should().Be("Pregunta o pide algo a Vega…");
        // El placeholder no es un nombre accesible: desaparece al escribir y no todos los lectores lo anuncian.
        panel.Find("textarea.asistente-textarea").GetAttribute("aria-label").Should().Be("Mensaje para Vega");
        panel.Find("button.asistente-boton-enviar").GetAttribute("aria-label").Should().Be("Enviar");
        panel.Find(".vega-pista-campo").TextContent.Should().Be("Enter envía · Mayús+Enter nueva línea · Ctrl+I pliega");
    }

    [Fact]
    public void La_bienvenida_ofrece_un_ejemplo_por_cada_modo_disponible()
    {
        var panel = RenderAbierto();

        var ejemplos = panel.FindAll(".vega-ejemplo");
        ejemplos.Should().ContainSingle("sin agente solo hay Consulta PRL");
        ejemplos[0].TextContent.Should().Contain("Consulta PRL · responde sobre normativa de prevención")
            .And.Contain("¿Qué formación hace falta para trabajos en altura?");
    }

    [Fact]
    public void Con_la_pregunta_en_vuelo_Vega_esta_pensando()
    {
        var panel = RenderAbierto();

        panel.Find("textarea.asistente-textarea").Input("¿Qué es un plan de seguridad?");
        panel.Find("button.asistente-boton-enviar").Click();

        panel.Find(".asistente-mensaje-cargando .vega-pensando").GetAttribute("aria-label").Should().Be("Vega está pensando");
    }
}
