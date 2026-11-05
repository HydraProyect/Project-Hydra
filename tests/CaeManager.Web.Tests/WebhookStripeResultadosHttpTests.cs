using System.Net;
using System.Text;
using CaeManager.Application.Comercial.Common;
using CaeManager.Domain.Common;
using CaeManager.Web.Api.Comercial;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CaeManager.Web.Tests;

/// <summary>
/// Traducción a HTTP de cada resultado de <see cref="IProcesadorWebhookSuscripcion"/>, sobre el
/// endpoint real: solo «proveedor no disponible» y «suscripción ocupada» piden reintento a Stripe
/// (no 2xx); el resto se reconoce con 200 porque reintentar no lo arreglaría. Y el endpoint pasa
/// al procesador el evento que verificó, no otro.
/// </summary>
public class WebhookStripeResultadosHttpTests
{
    [Theory]
    [InlineData(ResultadoWebhookSuscripcion.ProveedorNoDisponible, HttpStatusCode.ServiceUnavailable)]
    [InlineData(ResultadoWebhookSuscripcion.SuscripcionOcupada, HttpStatusCode.ServiceUnavailable)]
    [InlineData(ResultadoWebhookSuscripcion.Aplicado, HttpStatusCode.OK)]
    [InlineData(ResultadoWebhookSuscripcion.SinCambios, HttpStatusCode.OK)]
    [InlineData(ResultadoWebhookSuscripcion.SinSuscripcion, HttpStatusCode.OK)]
    [InlineData(ResultadoWebhookSuscripcion.SinTenantVinculado, HttpStatusCode.OK)]
    [InlineData(ResultadoWebhookSuscripcion.TenantNoSuscribible, HttpStatusCode.OK)]
    [InlineData(ResultadoWebhookSuscripcion.EstadoNoReconocido, HttpStatusCode.OK)]
    public async Task Cada_resultado_del_procesador_responde_su_codigo(ResultadoWebhookSuscripcion resultado, HttpStatusCode esperado)
    {
        var procesador = new ProcesadorFijo(new ProcesamientoWebhookSuscripcion(resultado, "PaymentProvider.TiempoAgotado"));

        var estado = await EnviarAsync(new ProveedorQueAceptaLaFirma(), procesador);

        estado.Should().Be(esperado);
        procesador.Recibido.Should().NotBeNull();
        procesador.Recibido!.Suscripcion!.IdSuscripcion.Should().Be("sub_http");
    }

    [Fact]
    public async Task Una_firma_rechazada_no_llega_al_procesador()
    {
        var procesador = new ProcesadorFijo(new ProcesamientoWebhookSuscripcion(ResultadoWebhookSuscripcion.Aplicado));

        var estado = await EnviarAsync(new ProveedorQueAceptaLaFirma(aceptar: false), procesador);

        estado.Should().Be(HttpStatusCode.Unauthorized);
        procesador.Recibido.Should().BeNull();
    }

    private static async Task<HttpStatusCode> EnviarAsync(IPaymentProvider proveedor, IProcesadorWebhookSuscripcion procesador)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(proveedor);
        builder.Services.AddSingleton(procesador);
        await using var app = builder.Build();
        app.MapWebhookStripeEndpoints();
        await app.StartAsync();
        try
        {
            var direccion = app.Services.GetRequiredService<IServer>().Features
                .Get<IServerAddressesFeature>()!.Addresses.Single();
            using var http = new HttpClient { BaseAddress = new Uri(direccion) };
            using var peticion = new HttpRequestMessage(HttpMethod.Post, "/api/comercial/webhooks/stripe/")
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            };
            peticion.Headers.Add("Stripe-Signature", "t=1,v1=fixture");
            using var respuesta = await http.SendAsync(peticion);
            return respuesta.StatusCode;
        }
        finally
        {
            await app.StopAsync();
        }
    }

    private sealed class ProveedorQueAceptaLaFirma(bool aceptar = true) : IPaymentProvider
    {
        public string Codigo => "fixture";

        public Result<EventoWebhookProveedorDto> VerificarYLeerWebhook(string payloadJson, string? firma) =>
            aceptar
                ? Result.Exito(new EventoWebhookProveedorDto("customer.subscription.updated",
                    new SuscripcionProveedorDto("sub_http", "cus_http", EstadoSuscripcionProveedor.Activa)))
                : Result.Fallo<EventoWebhookProveedorDto>(Error.Crear("PaymentProvider.FirmaInvalida", "firma"));

        public Task<Result<SuscripcionProveedorDto>> ObtenerSuscripcionAsync(string idSuscripcion, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("El endpoint no consulta Stripe: lo hace el procesador.");
    }

    private sealed class ProcesadorFijo(ProcesamientoWebhookSuscripcion resultado) : IProcesadorWebhookSuscripcion
    {
        public EventoWebhookProveedorDto? Recibido { get; private set; }

        public Task<ProcesamientoWebhookSuscripcion> ProcesarAsync(EventoWebhookProveedorDto evento, CancellationToken cancellationToken)
        {
            Recibido = evento;
            return Task.FromResult(resultado);
        }
    }
}
