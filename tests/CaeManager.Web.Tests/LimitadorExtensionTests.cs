using System.Net;
using System.Threading.RateLimiting;
using CaeManager.Infrastructure.Autenticacion;
using CaeManager.Web.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CaeManager.Web.Tests;

public class LimitadorExtensionTests
{
    [Fact]
    public async Task Los_tokens_de_una_misma_sesion_comparten_cupo_y_otra_cuenta_tiene_el_suyo()
    {
        await using var sitio = await Sitio.IniciarAsync();
        var cuenta = Guid.NewGuid();
        var sesion = Guid.NewGuid();
        var token = sitio.Token(cuenta, sesion);
        (await sitio.PedirAsync("/extension/uno", token)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await sitio.PedirAsync("/extension/dos", sitio.Token(cuenta, sesion))).StatusCode.Should().Be(HttpStatusCode.OK);
        using var rechazada = await sitio.PedirAsync("/extension/uno", token);
        rechazada.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        rechazada.Headers.RetryAfter!.Delta.Should().BeGreaterThan(TimeSpan.Zero);
        (await sitio.PedirAsync("/extension/uno", sitio.Token(Guid.NewGuid()))).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Un_token_de_una_sesion_anterior_no_agota_el_cupo_de_la_sesion_viva_de_la_misma_cuenta()
    {
        await using var sitio = await Sitio.IniciarAsync();
        var cuenta = Guid.NewGuid();
        var revocado = sitio.Token(cuenta, Guid.NewGuid());
        var vivo = sitio.Token(cuenta, Guid.NewGuid());
        (await sitio.PedirAsync("/extension/uno", revocado)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await sitio.PedirAsync("/extension/uno", revocado)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await sitio.PedirAsync("/extension/uno", revocado)).StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        (await sitio.PedirAsync("/extension/uno", vivo)).StatusCode.Should().Be(HttpStatusCode.OK);
    }
    [Fact]
    public async Task Los_tokens_invalidos_comparten_cupo_por_ip_y_no_afectan_otras_rutas()
    {
        await using var sitio = await Sitio.IniciarAsync();
        (await sitio.PedirAsync("/extension/uno", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await sitio.PedirAsync("/extension/dos", "invalido")).StatusCode.Should().Be(HttpStatusCode.OK);
        using var rechazada = await sitio.PedirAsync("/extension/uno", "otro-invalido");
        rechazada.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        rechazada.Headers.RetryAfter!.Delta.Should().BeGreaterThan(TimeSpan.Zero);
        (await sitio.PedirAsync("/extension-extra", null)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Un_token_de_otra_clave_no_gasta_el_cupo_de_la_cuenta_que_nombra()
    {
        await using var sitio = await Sitio.IniciarAsync();
        var victima = Guid.NewGuid();
        var legitimo = sitio.Token(victima);
        legitimo.Length.Should().BeLessThan(1024);
        var forjado = TokenSesionExtension.Proteger(new EphemeralDataProtectionProvider(), victima, "SELLO", Guid.NewGuid());
        (await sitio.PedirAsync("/extension/uno", forjado)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await sitio.PedirAsync("/extension/uno", forjado)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await sitio.PedirAsync("/extension/uno", forjado)).StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        (await sitio.PedirAsync("/extension/uno", legitimo)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Un_token_mas_largo_que_el_tope_cuenta_por_ip_sin_descifrarse()
    {
        await using var sitio = await Sitio.IniciarAsync();
        var cuenta = Guid.NewGuid();
        var largo = sitio.Token(cuenta) + new string('A', 2000);
        (await sitio.PedirAsync("/extension/uno", largo)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await sitio.PedirAsync("/extension/uno", largo)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await sitio.PedirAsync("/extension/uno", largo)).StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        (await sitio.PedirAsync("/extension/uno", sitio.Token(cuenta))).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Un_fallo_al_descifrar_cuenta_por_ip_y_no_devuelve_500()
    {
        await using var sitio = await Sitio.IniciarAsync(new ProveedorQueFalla());
        var token = sitio.Token(Guid.NewGuid());
        (await sitio.PedirAsync("/extension/uno", token)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await sitio.PedirAsync("/extension/dos", token)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await sitio.PedirAsync("/extension/uno", token)).StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
    }

    private sealed class ProveedorQueFalla : IDataProtectionProvider
    {
        public IDataProtector CreateProtector(string purpose) => throw new InvalidOperationException("almacen de claves caido");
    }

    private sealed class Sitio(WebApplication app, HttpClient cliente, IDataProtectionProvider proteccion) : IAsyncDisposable
    {
        public static async Task<Sitio> IniciarAsync(IDataProtectionProvider? proveedorDelLimitador = null)
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            var proteccion = new EphemeralDataProtectionProvider();
            builder.Services.AddRateLimiter(opciones =>
            {
                opciones.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
                opciones.OnRejected = LimitadorExtension.RechazarAsync;
                opciones.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(contexto =>
                    LimitadorExtension.ParticionGlobal(contexto, 2, proveedorDelLimitador ?? proteccion)
                    ?? RateLimitPartition.GetNoLimiter("sin-limite"));
            });
            var app = builder.Build();
            app.UseRateLimiter();
            app.MapGet("/extension/uno", () => "ok");
            app.MapGet("/extension/dos", () => "ok");
            app.MapGet("/extension-extra", () => "ok");
            await app.StartAsync();
            var direccion = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
            return new Sitio(app, new HttpClient { BaseAddress = new Uri(direccion) }, proteccion);
        }

        public string Token(Guid usuario, Guid? sid = null) => TokenSesionExtension.Proteger(proteccion, usuario, "SELLO", sid ?? Guid.NewGuid());

        public async Task<HttpResponseMessage> PedirAsync(string ruta, string? token)
        {
            using var peticion = new HttpRequestMessage(HttpMethod.Get, ruta);
            if (token is not null) peticion.Headers.TryAddWithoutValidation("Authorization", "Extension " + token);
            return await cliente.SendAsync(peticion);
        }

        public async ValueTask DisposeAsync()
        {
            cliente.Dispose();
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}
