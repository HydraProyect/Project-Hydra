using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using CaeManager.Application.Importacion;
using CaeManager.Application.Importacion.Maestra;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Features.Importacion;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CaeManager.Web.Tests;

/// <summary>
/// Autorización HTTP de la descarga de la plantilla maestra (revisión Codex de la PR #2164, r1, BAJO 1): el catálogo de
/// binarios cuenta la llamada, pero no la política; aquí se pide la ruta de verdad con cada rol.
/// </summary>
public class ImportacionPlantillaMaestraEndpointTests
{
    private const string Esquema = "Prueba";
    private const string CabeceraRol = "X-Rol-Prueba";

    [Theory]
    [InlineData(Roles.Administrador)]
    [InlineData(Roles.DireccionCae)]
    [InlineData(Roles.CoordinadorCae)]
    public async Task Los_roles_de_carga_masiva_descargan_la_plantilla(string rol)
    {
        await using var app = await ArrancarAsync();
        using var cliente = Cliente(app, rol);

        using var respuesta = await cliente.GetAsync(ImportacionEndpoints.RutaPlantillaMaestra);

        respuesta.StatusCode.Should().Be(HttpStatusCode.OK);
        respuesta.Content.Headers.ContentType!.MediaType.Should().Be("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
        (await respuesta.Content.ReadAsByteArrayAsync()).Should().Equal(PlantillaFalsa.Contenido);
    }

    [Theory]
    [InlineData(Roles.GestorCae)]
    [InlineData(Roles.Consulta)]
    [InlineData(Roles.Cliente)]
    public async Task Otro_rol_autenticado_recibe_403_sin_plantilla(string rol)
    {
        await using var app = await ArrancarAsync();
        using var cliente = Cliente(app, rol);

        using var respuesta = await cliente.GetAsync(ImportacionEndpoints.RutaPlantillaMaestra);

        respuesta.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        app.Services.GetRequiredService<PlantillaFalsa>().Generadas.Should().Be(0);
    }

    [Fact]
    public async Task Sin_sesion_recibe_401_sin_plantilla()
    {
        await using var app = await ArrancarAsync();
        using var cliente = Cliente(app, rol: null);

        using var respuesta = await cliente.GetAsync(ImportacionEndpoints.RutaPlantillaMaestra);

        respuesta.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        app.Services.GetRequiredService<PlantillaFalsa>().Generadas.Should().Be(0);
    }

    private static HttpClient Cliente(WebApplication app, string? rol)
    {
        var cliente = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            BaseAddress = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First()),
        };
        if (rol is not null) cliente.DefaultRequestHeaders.Add(CabeceraRol, rol);
        return cliente;
    }

    private static async Task<WebApplication> ArrancarAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddLogging();
        builder.Services.AddAuthentication(Esquema).AddScheme<AuthenticationSchemeOptions, SesionFalsa>(Esquema, _ => { });
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton<PlantillaFalsa>();
        builder.Services.AddSingleton<IPlantillaMaestraService>(sp => sp.GetRequiredService<PlantillaFalsa>());
        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapImportacionEndpoints();
        await app.StartAsync();
        return app;
    }

    /// <summary>Una sesión con el rol de la cabecera de prueba; sin cabecera, anónima.</summary>
    private sealed class SesionFalsa(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue(CabeceraRol, out var rol))
                return Task.FromResult(AuthenticateResult.NoResult());
            var identidad = new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()), new Claim(ClaimTypes.Role, rol.ToString())], Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identidad), Scheme.Name)));
        }
    }

    private sealed class PlantillaFalsa : IPlantillaMaestraService
    {
        public static readonly byte[] Contenido = [0x50, 0x4B, 0x03, 0x04, 0x01];
        public int Generadas { get; private set; }

        public byte[] GenerarPlantilla()
        {
            Generadas++;
            return Contenido;
        }

        public Task<PlanImportacionMaestraDto> AnalizarAsync(Stream archivo, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("El endpoint solo sirve la plantilla en blanco.");
    }
}
