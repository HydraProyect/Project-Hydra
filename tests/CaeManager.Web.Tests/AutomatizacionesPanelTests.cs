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
    public void Muestra_No_configurado_para_M365_sin_integracion_configurada_y_deja_WhatsApp_intacto()
    {
        ConfigurarServiciosComunes(m365Configurado: false, whatsAppConfigurado: true);

        var cut = Render<AutomatizacionesPanel>();

        var filas = cut.FindAll(".fila-automatizaciones:not(.fila-cabecera)");
        filas[0].TextContent.Should().Contain("No configurado");
        filas[0].TextContent.Should().Contain("Sin configurar");
        filas[0].QuerySelector("button.interruptor").Should().BeNull("un trabajo sin configurar no tiene ningún interruptor que conmutar");

        // WhatsApp SÍ está configurado en este test — si ambas filas leyeran
        // la misma opción por error, esta aserción lo detectaría.
        filas[1].TextContent.Should().NotContain("No configurado");
        filas[1].QuerySelector("button.interruptor").Should().NotBeNull();
    }

    [Fact]
    public void Muestra_No_configurado_para_WhatsApp_sin_integracion_configurada_y_deja_M365_intacto()
    {
        ConfigurarServiciosComunes(m365Configurado: true, whatsAppConfigurado: false);

        var cut = Render<AutomatizacionesPanel>();

        var filas = cut.FindAll(".fila-automatizaciones:not(.fila-cabecera)");
        filas[1].TextContent.Should().Contain("No configurado");
        filas[1].TextContent.Should().Contain("Sin configurar");
        filas[1].QuerySelector("button.interruptor").Should().BeNull("un trabajo sin configurar no tiene ningún interruptor que conmutar");

        // M365 SÍ está configurado en este test — prueba dirigida a que la
        // rama de WhatsApp no esté leyendo por accidente OpcionesMicrosoft365
        // (o simplemente devolviendo true sin mirar ninguna opción).
        filas[0].TextContent.Should().NotContain("No configurado");
        filas[0].QuerySelector("button.interruptor").Should().NotBeNull();
    }

    [Fact]
    public void Muestra_el_interruptor_normal_para_ambos_trabajos_configurados()
    {
        ConfigurarServiciosComunes(m365Configurado: true, whatsAppConfigurado: true);

        var cut = Render<AutomatizacionesPanel>();

        var filas = cut.FindAll(".fila-automatizaciones:not(.fila-cabecera)");
        foreach (var fila in filas)
        {
            fila.TextContent.Should().Contain("Sin datos");
            fila.TextContent.Should().NotContain("No configurado");
            fila.QuerySelector("button.interruptor").Should().NotBeNull();
        }
    }

    /// <summary>REC-126: el mensaje de error de la última ejecución fallida debe llegar al panel, no solo el badge "Fallida".</summary>
    [Fact]
    public void Muestra_el_mensaje_de_error_de_una_ejecucion_fallida()
    {
        Services.AddScoped<IMediator>(_ => new MediatorFalso
        {
            Respuesta = (IReadOnlyList<AutomatizacionDto>)
            [
                new(CatalogoAutomatizaciones.IngestaCorreoM365, "Ingesta de correo M365", "desc", Conmutable: true, Activo: true,
                    UltimaEjecucionUtc: DateTime.UtcNow, UltimoResultadoExitoso: false,
                    UltimoMensajeError: "El token de Microsoft 365 caducó."),
            ]
        });
        Services.AddScoped<ToastService>();
        Services.AddSingleton<IOptions<Microsoft365GraphOptions>>(
            Options.Create(new Microsoft365GraphOptions { ClientId = "id", ClientSecret = "secret", UrlPublicaBase = "https://hydra.local" }));
        Services.AddSingleton<IOptions<WhatsAppCloudApiOptions>>(Options.Create(new WhatsAppCloudApiOptions()));
        Services.AddSingleton<IOptions<AlertasPorCorreoOptions>>(Options.Create(new AlertasPorCorreoOptions()));
        Services.AddSingleton<IOptions<VigilanciaNormativaBoeOptions>>(Options.Create(new VigilanciaNormativaBoeOptions()));

        var cut = Render<AutomatizacionesPanel>();

        var fila = cut.Find(".fila-automatizaciones:not(.fila-cabecera)");
        fila.TextContent.Should().Contain("Fallida");
        fila.TextContent.Should().Contain("El token de Microsoft 365 caducó.");
    }

    /// <summary>Éxito con datos evaluados/afectados (REC-126) — sin mensaje de error que mostrar.</summary>
    [Fact]
    public void Muestra_evaluados_y_afectados_de_una_ejecucion_exitosa()
    {
        Services.AddScoped<IMediator>(_ => new MediatorFalso
        {
            Respuesta = (IReadOnlyList<AutomatizacionDto>)
            [
                new(CatalogoAutomatizaciones.IngestaCorreoM365, "Ingesta de correo M365", "desc", Conmutable: true, Activo: true,
                    UltimaEjecucionUtc: DateTime.UtcNow, UltimoResultadoExitoso: true,
                    UltimosElementosEvaluados: 12, UltimosElementosAfectados: 3),
            ]
        });
        Services.AddScoped<ToastService>();
        Services.AddSingleton<IOptions<Microsoft365GraphOptions>>(
            Options.Create(new Microsoft365GraphOptions { ClientId = "id", ClientSecret = "secret", UrlPublicaBase = "https://hydra.local" }));
        Services.AddSingleton<IOptions<WhatsAppCloudApiOptions>>(Options.Create(new WhatsAppCloudApiOptions()));
        Services.AddSingleton<IOptions<AlertasPorCorreoOptions>>(Options.Create(new AlertasPorCorreoOptions()));
        Services.AddSingleton<IOptions<VigilanciaNormativaBoeOptions>>(Options.Create(new VigilanciaNormativaBoeOptions()));

        var cut = Render<AutomatizacionesPanel>();

        var fila = cut.Find(".fila-automatizaciones:not(.fila-cabecera)");
        fila.TextContent.Should().Contain("12 evaluados, 3 afectados");
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
