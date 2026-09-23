using Bunit;
using CaeManager.Application.Tenants.Queries.EsAdministradorPlataforma;
using CaeManager.Application.VigilanciaNormativa.Queries.ObtenerAvisosRevisionNormativa;
using CaeManager.Web.Components.Layout;
using CaeManager.Web.Features.VigilanciaNormativa;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CaeManager.Web.Tests;

/// <summary>
/// Los textos del panel de vigilancia normativa salen de
/// <c>TextosVigilanciaNormativa</c>. Las claves no coinciden con su texto a
/// propósito: si el localizador no encontrara el recurso devolvería el nombre
/// de la clave («EstadoPendiente»), y estos tests lo verían.
/// </summary>
public class PanelAvisosNormativosTests : BunitContext
{
    public PanelAvisosNormativosTests()
    {
        Services.AddLocalization();
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton<ILogger<ExcepcionDeCircuitoDesconectado>>(NullLogger<ExcepcionDeCircuitoDesconectado>.Instance);
    }

    private sealed class MediatorConAvisos(bool esAdministradorPlataforma, params AvisoRevisionNormativaDto[] avisos) : IMediator
    {
        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            request switch
            {
                ObtenerAvisosRevisionNormativaQuery => Task.FromResult((TResponse)(object)(IReadOnlyList<AvisoRevisionNormativaDto>)avisos),
                EsAdministradorPlataformaQuery => Task.FromResult((TResponse)(object)esAdministradorPlataforma),
                _ => throw new NotSupportedException(request.GetType().Name),
            };

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

    private static AvisoRevisionNormativaDto Aviso(bool revisado) =>
        new(Guid.NewGuid(), "BOE-A-2026-1234", new DateOnly(2026, 9, 7), "Real Decreto de prueba",
            "https://www.boe.es/", "RD 1627/1997", revisado, revisado ? DateTime.UtcNow : null);

    [Fact]
    public void Panel_abierto_muestra_los_textos_de_los_recursos()
    {
        Services.AddScoped<IMediator>(_ => new MediatorConAvisos(true, Aviso(revisado: false), Aviso(revisado: true)));

        var panel = Render<PanelAvisosNormativos>();
        var boton = panel.Find("button.boton-avisos-normativos");
        boton.GetAttribute("title").Should().Be("Vigilancia normativa del BOE");
        boton.Click();

        panel.Find(".descripcion-avisos-normativos").TextContent.Should().StartWith("Publicaciones del BOE que tocan");
        panel.Find(".estado-aviso-pendiente").TextContent.Trim().Should().Be("Pendiente de revisión");
        panel.Find(".estado-aviso-revisado").TextContent.Trim().Should().Be("Revisado");
        panel.FindAll("button").Select(b => b.TextContent.Trim()).Should().Contain("Marcar revisado");
    }

    [Fact]
    public void Sin_Actor_de_Plataforma_no_aparece_Marcar_revisado()
    {
        Services.AddScoped<IMediator>(_ => new MediatorConAvisos(false, Aviso(revisado: false)));

        var panel = Render<PanelAvisosNormativos>();
        panel.Find("button.boton-avisos-normativos").Click();

        panel.FindAll("button").Select(b => b.TextContent.Trim()).Should().NotContain("Marcar revisado");
    }
}
