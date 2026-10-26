using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CaeManager.Application.Comercial.Common;
using CaeManager.Application.Common;
using CaeManager.Application.Integraciones;
using CaeManager.Application.Tenants;
using CaeManager.Domain.Integraciones;
using CaeManager.Infrastructure.Comercial;
using CaeManager.Infrastructure.Integraciones;
using CaeManager.Web.Api.Comercial;
using CaeManager.Web.Api.Integraciones;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CaeManager.Web.Tests;

public class WebhooksAutenticacionHttpTests
{
    private const string Secreto = "secreto_solo_fixture_http_72941";
    private const string SecretoAjeno = "secreto_ajeno_fixture_http_86352";

    [Theory]
    [InlineData("whatsapp", "ausente")]
    [InlineData("whatsapp", "erronea")]
    [InlineData("microsoft365", "ausente")]
    [InlineData("microsoft365", "erronea")]
    [InlineData("stripe", "ausente")]
    [InlineData("stripe", "erronea")]
    public async Task Credencial_ausente_o_erronea_rechaza_sin_efectos_ni_secretos_en_logs(
        string proveedor, string credencial)
    {
        var resultado = await EnviarAsync(proveedor, credencial);

        resultado.Estado.Should().Be(HttpStatusCode.Unauthorized);
        resultado.Llamadas.Should().NotContain("Agregar");
        resultado.Llamadas.Should().NotContain("SaveChangesAsync");
        resultado.Llamadas.Should().NotContain("Despertar");
        resultado.Llamadas.Should().NotContain("get_Tenants");
        resultado.Llamadas.Should().NotContain("ParticionarPorPhoneNumberId");
        resultado.Llamadas.Should().NotContain("ResolverPorPhoneNumberIdAsync");
        if (proveedor == "microsoft365")
            resultado.Llamadas.Count(x => x == "VerificarAsync").Should().Be(credencial == "ausente" ? 0 : 1);
        resultado.Logs.Should().NotContain(Secreto).And.NotContain(SecretoAjeno);
    }

    [Theory]
    [InlineData("whatsapp", HttpStatusCode.OK)]
    [InlineData("microsoft365", HttpStatusCode.Accepted)]
    [InlineData("stripe", HttpStatusCode.OK)]
    public async Task Credencial_valida_alcanza_la_rama_posterior_a_la_autenticacion(
        string proveedor, HttpStatusCode esperado)
    {
        var resultado = await EnviarAsync(proveedor, "valida");

        resultado.Estado.Should().Be(esperado);
        if (proveedor != "stripe")
        {
            resultado.Llamadas.Count(x => x == "Agregar").Should().Be(1);
            resultado.Llamadas.Count(x => x == "SaveChangesAsync").Should().Be(1);
        }
        if (proveedor == "whatsapp")
            resultado.Llamadas.Count(x => x == "Despertar").Should().Be(1);
        resultado.Logs.Should().NotContain(Secreto);
    }

    private static async Task<(HttpStatusCode Estado, string[] Llamadas, string Logs)> EnviarAsync(
        string proveedor, string credencial)
    {
        var llamadas = new ConcurrentQueue<string>();
        var logs = new CapturaLogs();
        var tenantId = Guid.NewGuid();
        var conexionId = Guid.NewGuid();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(logs);
        builder.Services.AddSingleton(Options.Create(new WhatsAppCloudApiOptions { AppSecret = Secreto }));
        builder.Services.AddSingleton<IEventoWebhookRepository>(Crear<IEventoWebhookRepository>(llamadas, (_, _) => null));
        builder.Services.AddSingleton<IUnitOfWork>(Crear<IUnitOfWork>(llamadas, (_, _) => Task.FromResult(1)));
        builder.Services.AddSingleton<ISenalIngestaWhatsApp>(Crear<ISenalIngestaWhatsApp>(llamadas, (_, _) => null));
        builder.Services.AddSingleton<IWhatsAppCloudApiClient>(Crear<IWhatsAppCloudApiClient>(llamadas,
            (metodo, _) => metodo == "ParticionarPorPhoneNumberId"
                ? new FragmentoWebhookWhatsAppDto[] { new("linea_fixture", "{}") }
                : throw new InvalidOperationException(metodo)));
        builder.Services.AddSingleton<IWebhookWhatsAppTenantResolver>(Crear<IWebhookWhatsAppTenantResolver>(llamadas,
            (_, _) => Task.FromResult(new VerificacionWebhookWhatsAppDto(true, tenantId, conexionId))));
        // El resolver criptográfico de Graph se prueba aparte: aquí se observa que el
        // endpoint respeta su resultado antes de persistir o establecer el Tenant.
        builder.Services.AddSingleton<IWebhookTenantResolver>(Crear<IWebhookTenantResolver>(llamadas,
            (_, argumentos) => Task.FromResult(new VerificacionWebhookDto(
                (string)argumentos![1]! == Secreto, tenantId))));
        builder.Services.AddSingleton<IMicrosoft365GraphClient>(Crear<IMicrosoft365GraphClient>(llamadas,
            (metodo, argumentos) =>
            {
                using var json = JsonDocument.Parse((string)argumentos![0]!);
                var notificacion = json.RootElement.GetProperty("value")[0];
                var propiedad = metodo == "ExtraerClientStateDeNotificacion" ? "clientState" : "subscriptionId";
                return notificacion.TryGetProperty(propiedad, out var valor) ? valor.GetString() : null;
            }));
        builder.Services.AddSingleton<ITenantsQueryContext>(Crear<ITenantsQueryContext>(llamadas,
            (metodo, _) => throw new InvalidOperationException($"Consulta prematura: {metodo}")));
        builder.Services.AddSingleton<IPaymentProvider>(servicios => new StripePaymentProvider(
            Options.Create(new StripeOptions { ApiKey = "sk_test_falsa", WebhookSecret = Secreto }),
            servicios.GetRequiredService<ILogger<StripePaymentProvider>>()));
        await using var app = builder.Build();
        app.MapWebhookWhatsAppEndpoints();
        app.MapWebhookMicrosoft365Endpoints();
        app.MapWebhookStripeEndpoints();
        await app.StartAsync();
        try
        {
            var direccion = app.Services.GetRequiredService<IServer>().Features
                .Get<IServerAddressesFeature>()!.Addresses.Single();
            using var http = new HttpClient { BaseAddress = new Uri(direccion) };
            var payload = proveedor == "stripe"
                ? """{"id":"evt_http","object":"event","type":"customer.updated","data":{"object":{"id":"cus_http","object":"customer"}}}"""
                : "{}";
            if (proveedor == "microsoft365")
            {
                var notificacion = new
                {
                    clientState = credencial == "ausente" ? null : credencial == "valida" ? Secreto : SecretoAjeno,
                    subscriptionId = "subscription_fixture"
                };
                payload = JsonSerializer.Serialize(new { value = new[] { notificacion } });
            }
            var ruta = proveedor == "stripe" ? "/api/comercial/webhooks/stripe/"
                : proveedor == "microsoft365" ? $"/api/integraciones/webhooks/microsoft365/{conexionId}"
                : "/api/integraciones/webhooks/whatsapp/";
            using var peticion = new HttpRequestMessage(HttpMethod.Post, ruta)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };
            if (proveedor != "microsoft365" && credencial != "ausente")
            {
                var secretoFirma = credencial == "valida" ? Secreto : SecretoAjeno;
                var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                var contenidoFirmado = proveedor == "stripe" ? $"{timestamp}.{payload}" : payload;
                var firma = Convert.ToHexString(HMACSHA256.HashData(
                    Encoding.UTF8.GetBytes(secretoFirma), Encoding.UTF8.GetBytes(contenidoFirmado))).ToLowerInvariant();
                peticion.Headers.Add(proveedor == "stripe" ? "Stripe-Signature" : "X-Hub-Signature-256",
                    proveedor == "stripe" ? $"t={timestamp},v1={firma}" : $"sha256={firma}");
            }
            using var respuesta = await http.SendAsync(peticion);
            return (respuesta.StatusCode, llamadas.ToArray(), string.Join('\n', logs.Mensajes));
        }
        finally
        {
            await app.StopAsync();
        }
    }

    private static T Crear<T>(ConcurrentQueue<string> llamadas, Func<string, object?[]?, object?> ejecutar)
        where T : class
    {
        var resultado = DispatchProxy.Create<T, Espia>();
        var espia = (Espia)(object)resultado;
        espia.Llamadas = llamadas;
        espia.Ejecutar = ejecutar;
        return resultado;
    }

    public class Espia : DispatchProxy
    {
        public ConcurrentQueue<string> Llamadas { get; set; } = null!;
        public Func<string, object?[]?, object?> Ejecutar { get; set; } = null!;
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            var nombre = targetMethod!.Name;
            Llamadas.Enqueue(nombre);
            return Ejecutar(nombre, args);
        }
    }

    private sealed class CapturaLogs : ILoggerProvider
    {
        public ConcurrentQueue<string> Mensajes { get; } = new();
        public ILogger CreateLogger(string categoryName) => new CapturaLogger(Mensajes);
        public void Dispose() { }

        private sealed class CapturaLogger(ConcurrentQueue<string> mensajes) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter) => mensajes.Enqueue($"{formatter(state, exception)} {exception}");
        }
    }
}
