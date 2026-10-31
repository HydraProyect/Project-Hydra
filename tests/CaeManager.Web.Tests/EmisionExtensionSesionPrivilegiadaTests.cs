using System.Net;
using System.Security.Claims;
using Bunit;
using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Infrastructure.Autenticacion;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Features.Extension;
using CaeManager.Web.Features.Extension.Pages;
using CaeManager.Web.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CaeManager.Web.Tests;

public class EmisionExtensionSesionPrivilegiadaTests : BunitContext
{
    private static readonly Guid Usuario = Guid.NewGuid();
    private static readonly Guid Sid = Guid.NewGuid();
    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly Guid Simulado = Guid.NewGuid();
    private static readonly Guid SesionPrivilegiada = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private readonly Almacen _almacen = new();
    private readonly Sesiones _sesiones = new();
    private readonly EphemeralDataProtectionProvider _protector = new();
    private readonly RegistroCapturado _registro = new();

    [Fact]
    public async Task Pantalla_deniega_antes_de_leer_cuenta_y_muestra_motivo()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        this.ConRolDeEscritura(Roles.GestorCae);
        AddAuthorization().SetAuthorized("soporte").SetClaims(
            new Claim(ClaimTypes.NameIdentifier, Usuario.ToString()),
            new Claim(SignInManagerCuentaDesactivada.ClaimSid, Sid.ToString("N")));
        Services.AddLocalization();
        Services.AddScoped<PuertaAccesoDatos>();
        Services.AddSingleton<IClienteActivoSeleccionado>(new Seleccion(true));
        Services.AddSingleton<IRegistroSesionesUsuario>(_sesiones);
        Services.AddSingleton<IDataProtectionProvider>(_protector);
        Services.AddSingleton(Gestor());
        Services.AddSingleton<ILoggerFactory>(new LoggerFactory([_registro]));
        Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        var pagina = Render<ConectarExtension>();

        await pagina.FindAll("button").Single(b => b.TextContent.Contains("Generar token")).ClickAsync();

        pagina.Find(".aviso-error").TextContent.Should().Contain("Sesión Privilegiada");
        _almacen.Lecturas.Should().Be(0);
        _sesiones.Consultas.Should().Be(0);
        _registro.DebeNombrarLaDenegacion();
    }

    [Fact]
    public async Task Post_deniega_antes_de_leer_cuenta()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.AddProvider(_registro);
        builder.Services.AddSingleton<IClienteActivoSeleccionado>(new Seleccion(true));
        builder.Services.AddSingleton<IRegistroSesionesUsuario>(_sesiones);
        builder.Services.AddSingleton<IDataProtectionProvider>(_protector);
        builder.Services.AddSingleton(Gestor());
        builder.Services.AddAntiforgery();
        await using var app = builder.Build();
        app.Use(async (contexto, siguiente) =>
        {
            contexto.User = new ClaimsPrincipal(new ClaimsIdentity([
                new Claim(ClaimTypes.NameIdentifier, Usuario.ToString()),
                new Claim(SignInManagerCuentaDesactivada.ClaimSid, Sid.ToString("N"))], "cookie"));
            await siguiente();
        });
        app.UseAntiforgery();
        app.MapGet("/test-antiforgery", (HttpContext contexto, IAntiforgery antiforgery) =>
            antiforgery.GetAndStoreTokens(contexto).RequestToken);
        app.MapExtensionTokenEndpoints();
        await app.StartAsync();
        using var cliente = new HttpClient { BaseAddress = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First()) };
        var antiforgery = await cliente.GetStringAsync("/test-antiforgery");
        using var respuesta = await cliente.PostAsync("/cuenta/extension/token", new FormUrlEncodedContent(
            [new KeyValuePair<string, string>("__RequestVerificationToken", antiforgery)]));

        respuesta.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await respuesta.Content.ReadAsStringAsync()).Should().Contain("Sesión Privilegiada").And.NotContain("token\":");
        _almacen.Lecturas.Should().Be(0);
        _sesiones.Consultas.Should().Be(0);
        _registro.DebeNombrarLaDenegacion();
    }

    [Fact]
    public async Task Sin_sesion_privilegiada_emite_token_legible()
    {
        var resultado = await EmisorTokenExtension.EmitirAsync(Usuario, Sid, Gestor(), _protector, _sesiones, new Seleccion(false), new LoggerFactory([_registro]));
        DebeNombrarSoloALaCuentaAutenticada(resultado);
        TokenSesionExtension.Leer(_protector, resultado.Valor.Token)!.SecurityStamp.Should().Be("sello");
        _registro.Avisos.Should().BeEmpty();
    }

    /// <summary>
    /// La selección real, leída de su cookie protegida y ligada al usuario. La coordenada no distingue capacidades:
    /// lo mismo vale para Soporte en lectura que para la impersonación, cuyo Usuario simulado el emisor nunca ve.
    /// </summary>
    [Fact]
    public async Task Seleccion_real_de_sesion_privilegiada_deniega_antes_de_leer_cuenta()
    {
        var resultado = await EmitirConCookieAsync(ClienteActivoSeleccionado.Proteger(_protector, Usuario, Tenant, null, SesionPrivilegiada));

        resultado.Error.Codigo.Should().Be("Autorizacion.SesionPrivilegiadaSoloLectura");
        _almacen.Lecturas.Should().Be(0);
        _sesiones.Consultas.Should().Be(0);
        _registro.DebeNombrarLaDenegacion();
    }

    /// <summary>
    /// Contraste del test de la caducada: la misma carga, solo con vigencia, deniega. Sin él, un cambio de propósito
    /// del protector dejaría aquel test en verde por no descifrar, no por haber caducado.
    /// </summary>
    [Fact]
    public async Task Seleccion_vigente_con_la_misma_carga_que_la_caducada_deniega()
    {
        var resultado = await EmitirConCookieAsync(CookieConVigencia(Usuario, TimeSpan.FromHours(1)));

        resultado.Error.Codigo.Should().Be("Autorizacion.SesionPrivilegiadaSoloLectura");
    }

    [Fact]
    public async Task Seleccion_caducada_no_deniega_y_emite_para_la_cuenta_autenticada()
    {
        var resultado = await EmitirConCookieAsync(CookieConVigencia(Usuario, TimeSpan.FromHours(-1)));

        DebeNombrarSoloALaCuentaAutenticada(resultado);
    }

    [Fact]
    public async Task Seleccion_manipulada_no_deniega_y_emite_para_la_cuenta_autenticada()
    {
        var cookie = ClienteActivoSeleccionado.Proteger(_protector, Usuario, Tenant, null, SesionPrivilegiada);

        var resultado = await EmitirConCookieAsync(cookie[..^4] + (cookie.EndsWith("AAAA") ? "BBBB" : "AAAA"));

        DebeNombrarSoloALaCuentaAutenticada(resultado);
    }

    [Fact]
    public async Task Seleccion_emitida_para_otro_usuario_no_deniega_y_emite_para_la_cuenta_autenticada()
    {
        var resultado = await EmitirConCookieAsync(CookieConVigencia(Simulado, TimeSpan.FromHours(1)));

        DebeNombrarSoloALaCuentaAutenticada(resultado);
    }

    /// <summary>Lo que hace RevalidacionClienteActivoMiddleware con una Sesión Privilegiada caducada o revocada.</summary>
    [Fact]
    public async Task Seleccion_retirada_por_la_revalidacion_no_deniega_y_emite_para_la_cuenta_autenticada()
    {
        var seleccion = SeleccionReal(ClienteActivoSeleccionado.Proteger(_protector, Usuario, Tenant, null, SesionPrivilegiada));
        seleccion.SesionPrivilegiadaIdSeleccionada.Should().Be(SesionPrivilegiada);
        seleccion.Invalidar();

        var resultado = await EmisorTokenExtension.EmitirAsync(Usuario, Sid, Gestor(), _protector, _sesiones, seleccion, new LoggerFactory([_registro]));

        DebeNombrarSoloALaCuentaAutenticada(resultado);
    }


    private UserManager<ApplicationUser> Gestor() => new(_almacen, null!, null!, null!, null!, null!, null!, null!, null!);

    private void DebeNombrarSoloALaCuentaAutenticada(Result<(string Token, DateTime ExpiraEnUtc)> resultado)
    {
        resultado.EsExitoso.Should().BeTrue();
        TokenSesionExtension.Leer(_protector, resultado.Valor.Token)!.UsuarioId.Should().Be(Usuario);
        _almacen.IdsLeidos.Should().Equal(Usuario.ToString());
        _registro.Avisos.Should().BeEmpty();
    }

    private Task<Result<(string Token, DateTime ExpiraEnUtc)>> EmitirConCookieAsync(string cookie) =>
        EmisorTokenExtension.EmitirAsync(Usuario, Sid, Gestor(), _protector, _sesiones, SeleccionReal(cookie), new LoggerFactory([_registro]));

    private ClienteActivoSeleccionado SeleccionReal(string cookie)
    {
        var contexto = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, Usuario.ToString())], "cookie")),
        };
        contexto.Request.Headers.Cookie = $"{ClienteActivoSeleccionado.NombreCookie}={cookie}";
        return new ClienteActivoSeleccionado(new HttpContextAccessor { HttpContext = contexto }, _protector);
    }

    private string CookieConVigencia(Guid usuario, TimeSpan vigencia) =>
        _protector.CreateProtector("CaeManager.Web.ClienteActivoSeleccionado.v3").ToTimeLimitedDataProtector()
            .Protect($"{usuario:N}|{Tenant:N}|{Guid.Empty:N}|{SesionPrivilegiada:N}", vigencia);

    private sealed class RegistroCapturado : ILoggerProvider, ILogger
    {
        public List<string> Avisos { get; } = [];

        public void DebeNombrarLaDenegacion()
        {
            Avisos.Should().ContainSingle();
            Avisos[0].Should().Contain(Usuario.ToString()).And.Contain(SesionPrivilegiada.ToString());
        }

        public ILogger CreateLogger(string categoryName) =>
            categoryName == typeof(EmisorTokenExtension).FullName ? this : Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
                Avisos.Add(formatter(state, exception));
        }

        public void Dispose() { }
    }

    private sealed class Seleccion(bool privilegiada) : IClienteActivoSeleccionado
    {
        public Guid? TenantIdSeleccionado => null;
        public Guid? AsignacionOperacionIdSeleccionada => null;
        public Guid? SesionPrivilegiadaIdSeleccionada => privilegiada ? SesionPrivilegiada : null;
    }

    private sealed class Sesiones : IRegistroSesionesUsuario
    {
        public int Consultas { get; private set; }
        public Task<bool> EstaVigenteAsync(Guid usuarioId, Guid sid, CancellationToken ct = default) { Consultas++; return Task.FromResult(true); }
        public Task RegistrarAsync(Guid usuarioId, Guid tenantPropietarioId, Guid sid, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> RevocarAsync(Guid usuarioId, Guid sid, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<int> CerrarTodasAsync(Guid usuarioId, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class Almacen : IUserStore<ApplicationUser>
    {
        public List<string> IdsLeidos { get; } = [];
        public int Lecturas => IdsLeidos.Count;
        public Task<ApplicationUser?> FindByIdAsync(string id, CancellationToken ct) { IdsLeidos.Add(id); return Task.FromResult<ApplicationUser?>(new ApplicationUser { Id = Guid.Parse(id), SecurityStamp = "sello" }); }
        public Task<IdentityResult> CreateAsync(ApplicationUser u, CancellationToken ct) => throw new NotSupportedException();
        public Task<IdentityResult> DeleteAsync(ApplicationUser u, CancellationToken ct) => throw new NotSupportedException();
        public Task<ApplicationUser?> FindByNameAsync(string n, CancellationToken ct) => throw new NotSupportedException();
        public Task<string?> GetNormalizedUserNameAsync(ApplicationUser u, CancellationToken ct) => throw new NotSupportedException();
        public Task<string> GetUserIdAsync(ApplicationUser u, CancellationToken ct) => throw new NotSupportedException();
        public Task<string?> GetUserNameAsync(ApplicationUser u, CancellationToken ct) => throw new NotSupportedException();
        public Task SetNormalizedUserNameAsync(ApplicationUser u, string? n, CancellationToken ct) => throw new NotSupportedException();
        public Task SetUserNameAsync(ApplicationUser u, string? n, CancellationToken ct) => throw new NotSupportedException();
        public Task<IdentityResult> UpdateAsync(ApplicationUser u, CancellationToken ct) => throw new NotSupportedException();
        public void Dispose() { }
    }
}
