using Bunit;
using CaeManager.Application.Configuracion;
using CaeManager.Application.Configuracion.Queries.ObtenerEstadoAutomatizaciones;
using CaeManager.Infrastructure.Alertas;
using CaeManager.Infrastructure.Integraciones;
using CaeManager.Infrastructure.VigilanciaNormativa;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.Configuracion.Components;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CaeManager.Web.Tests;

/// <summary>
/// "Sin datos" (nunca ejecutó todavía) y "No configurado" (el hosted service
/// ni siquiera está registrado porque falta configurar la integración) son
/// dos hechos distintos que antes se veían igual en la tabla — salud de
/// plataforma, A-06.
/// </summary>
public class AutomatizacionesPanelTests : BunitContext
{
    private static IReadOnlyList<AutomatizacionDto> TrabajosDeEjemplo() =>
    [
        new(CatalogoAutomatizaciones.IngestaCorreoM365, "Ingesta de correo M365", "desc", Conmutable: true, Activo: true, null, null),
        new(CatalogoAutomatizaciones.IngestaWhatsApp, "Ingesta de WhatsApp", "desc", Conmutable: true, Activo: true, null, null),
    ];

    private void ConfigurarServiciosComunes(bool m365Configurado, bool whatsAppConfigurado)
    {
        Services.AddScoped<IMediator>(_ => new MediatorFalso { Respuesta = TrabajosDeEjemplo() });
        Services.AddScoped<ToastService>();
        Services.AddSingleton<IOptions<Microsoft365GraphOptions>>(
            Options.Create(m365Configurado
                ? new Microsoft365GraphOptions { ClientId = "id", ClientSecret = "secret", UrlPublicaBase = "https://hydra.local" }
                : new Microsoft365GraphOptions()));
        Services.AddSingleton<IOptions<WhatsAppCloudApiOptions>>(
            Options.Create(whatsAppConfigurado
                ? new WhatsAppCloudApiOptions { AppSecret = "secret", VerifyToken = "token" }
                : new WhatsAppCloudApiOptions()));
        Services.AddSingleton<IOptions<AlertasPorCorreoOptions>>(Options.Create(new AlertasPorCorreoOptions()));
        Services.AddSingleton<IOptions<VigilanciaNormativaBoeOptions>>(Options.Create(new VigilanciaNormativaBoeOptions()));
    }

    [Fact]
    public void Muestra_No_configurado_para_un_trabajo_sin_integracion_configurada()
    {
        ConfigurarServiciosComunes(m365Configurado: false, whatsAppConfigurado: true);

        var cut = Render<AutomatizacionesPanel>();

        var filas = cut.FindAll(".fila-automatizaciones:not(.fila-cabecera)");
        filas[0].TextContent.Should().Contain("No configurado");
        filas[0].TextContent.Should().Contain("Sin configurar");
        filas[0].QuerySelector("button.interruptor").Should().BeNull("un trabajo sin configurar no tiene ningún interruptor que conmutar");
    }

    [Fact]
    public void Muestra_el_interruptor_normal_para_un_trabajo_configurado()
    {
        ConfigurarServiciosComunes(m365Configurado: true, whatsAppConfigurado: true);

        var cut = Render<AutomatizacionesPanel>();

        var filas = cut.FindAll(".fila-automatizaciones:not(.fila-cabecera)");
        filas[0].TextContent.Should().Contain("Sin datos");
        filas[0].TextContent.Should().NotContain("No configurado");
        filas[0].QuerySelector("button.interruptor").Should().NotBeNull();
    }

    private sealed class MediatorFalso : IMediator
    {
        public object? Respuesta { get; set; }

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            Task.FromResult((TResponse)Respuesta!);

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest =>
            Task.CompletedTask;

        public Task<object?> Send(object request, CancellationToken cancellationToken = default) => Task.FromResult(Respuesta);

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("MediatorFalso no soporta streams.");

        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("MediatorFalso no soporta streams.");

        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification => Task.CompletedTask;
    }
}
