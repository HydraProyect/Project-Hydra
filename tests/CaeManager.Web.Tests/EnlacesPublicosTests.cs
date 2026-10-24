using CaeManager.Infrastructure.Email;
using CaeManager.Web.Services;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

public class EnlacesPublicosTests
{
    [Fact]
    public void La_base_configurada_prevalece_sobre_un_host_manipulado()
    {
        var enlace = SeguridadCuentaPruebas.Enlaces("https://cuentas.example/talveg/")
            .Activacion(Guid.Empty, "codigo+&", "https://host-manipulado.example/");
        enlace.Should().Be($"https://cuentas.example/talveg/cuenta/restablecer-contrasena?userId={Guid.Empty}&code=codigo%2B%26");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("//host-manipulado.example")]
    [InlineData("https://usuario:clave@cuentas.example")]
    [InlineData("https://cuentas.example?otra=base")]
    [InlineData("file:///tmp/")]
    public void Fuera_de_development_falla_cerrado_sin_base_publica_valida(string? configurada)
    {
        SeguridadCuentaPruebas.Enlaces(configurada).Activacion(Guid.Empty, "codigo", "https://host-manipulado.example/")
            .Should().BeNull();
    }

    [Fact]
    public void Solo_development_puede_usar_la_base_de_la_peticion_si_falta_configuracion()
    {
        SeguridadCuentaPruebas.Enlaces(null, "Development").Activacion(Guid.Empty, "codigo", "http://127.0.0.1:5186/")
            .Should().StartWith("http://127.0.0.1:5186/cuenta/");
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData("Testing")]
    public void Fuera_de_development_una_base_http_no_vale(string entorno)
    {
        SeguridadCuentaPruebas.Enlaces("http://cuentas.example/", entorno)
            .Activacion(Guid.Empty, "codigo", "https://host-manipulado.example/").Should().BeNull();
    }

    [Fact]
    public void En_development_una_base_http_configurada_si_vale()
    {
        SeguridadCuentaPruebas.Enlaces("http://localhost:5186/", "Development")
            .Activacion(Guid.Empty, "codigo", "https://host-manipulado.example/")
            .Should().StartWith("http://localhost:5186/cuenta/");
    }

    [Fact]
    public void La_base_ausente_se_registra_una_sola_vez_aunque_lleguen_muchas_peticiones_anonimas()
    {
        var registro = new RegistroDeErrores<EnlacesPublicos>();
        var enlaces = new EnlacesPublicos(
            Microsoft.Extensions.Options.Options.Create(new CaeManager.Infrastructure.Email.SmtpEmailOptions()),
            new Microsoft.Extensions.Hosting.Internal.HostingEnvironment { EnvironmentName = "Production" }, registro);

        for (var i = 0; i < 50; i++) enlaces.ObtenerBase("https://host-manipulado.example/").Should().BeNull();

        registro.Errores.Should().Be(1, "un anónimo no debe poder inundar el log repitiendo la petición");
    }

    [Theory]
    [InlineData("Production", null, false)]
    [InlineData("Production", "", false)]
    [InlineData("Production", "http://cuentas.example/", false)]
    [InlineData("Production", "https://usuario:clave@cuentas.example/", false)]
    [InlineData("Production", "https://cuentas.example/?x=1", false)]
    [InlineData("Production", "/ruta/relativa", false)]
    [InlineData("Production", "https://cuentas.example/", true)]
    [InlineData("Development", null, true)]
    [InlineData("Development", "http://localhost:5186/", true)]
    [InlineData("Development", "no es una url", false)]
    public void La_validacion_de_arranque_exige_https_fuera_de_development(string entorno, string? url, bool valida)
    {
        var validador = new ValidadorUrlBasePublica(new Microsoft.Extensions.Hosting.Internal.HostingEnvironment { EnvironmentName = entorno });

        var resultado = validador.Validate(null, new CaeManager.Infrastructure.Email.SmtpEmailOptions { UrlBasePublica = url });

        resultado.Succeeded.Should().Be(valida);
        if (!valida) resultado.FailureMessage.Should().Contain("Smtp__UrlBasePublica");
    }

    [Theory]
    [InlineData("Production", null, true)]
    [InlineData("Production", "https://cuentas.example/", false)]
    [InlineData("Development", null, false)]
    public async Task El_host_no_arranca_fuera_de_development_sin_base_publica_valida(string entorno, string? url, bool debeFallar)
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateEmptyApplicationBuilder(
            new Microsoft.Extensions.Hosting.HostApplicationBuilderSettings { EnvironmentName = entorno });
        builder.Services.Configure<CaeManager.Infrastructure.Email.SmtpEmailOptions>(o => o.UrlBasePublica = url);
        // Mismo registro que Program.cs.
        builder.Services.AddSingleton<Microsoft.Extensions.Options.IValidateOptions<CaeManager.Infrastructure.Email.SmtpEmailOptions>, ValidadorUrlBasePublica>();
        builder.Services.AddOptions<CaeManager.Infrastructure.Email.SmtpEmailOptions>().ValidateOnStart();
        using var host = builder.Build();

        var arrancar = () => host.StartAsync();

        if (debeFallar)
            await arrancar.Should().ThrowAsync<Microsoft.Extensions.Options.OptionsValidationException>().WithMessage("*Smtp:UrlBasePublica*");
        else
            await arrancar.Should().NotThrowAsync();
        await host.StopAsync();
    }

    private sealed class RegistroDeErrores<T> : Microsoft.Extensions.Logging.ILogger<T>
    {
        private int _errores;
        public int Errores => Volatile.Read(ref _errores);
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId,
            TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == Microsoft.Extensions.Logging.LogLevel.Error) Interlocked.Increment(ref _errores);
        }
    }
    [Theory]
    [InlineData(typeof(CaeManager.Web.Features.Usuarios.Pages.Usuarios), "NavigationManager")]
    [InlineData(typeof(CaeManager.Web.Features.Delegaciones.OperadoresCaeExternosPanel), "Navegacion")]
    public void Los_productores_de_activacion_usan_la_base_publica_y_fallan_cerrado(Type tipo, string propiedadNavegacion)
    {
        var componente = Activator.CreateInstance(tipo)!;
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        tipo.GetProperty(propiedadNavegacion, flags)!.SetValue(componente, new NavegacionManipulada());
        tipo.GetProperty("EnlacesPublicos", flags)!.SetValue(componente, SeguridadCuentaPruebas.Enlaces());
        var metodo = tipo.GetMethod("EnlaceActivacion", flags)!;
        ((string?)metodo.Invoke(componente, [Guid.Empty, "codigo"]))
            .Should().StartWith("https://cuentas.example/cuenta/restablecer-contrasena?");
        tipo.GetProperty("EnlacesPublicos", flags)!.SetValue(componente, SeguridadCuentaPruebas.Enlaces(null));
        metodo.Invoke(componente, [Guid.Empty, "codigo"]).Should().BeNull();
    }

    [Fact]
    public async Task La_activacion_sin_base_publica_no_envia_correo()
    {
        var tipo = typeof(CaeManager.Web.Features.Usuarios.Pages.Usuarios);
        var componente = new CaeManager.Web.Features.Usuarios.Pages.Usuarios();
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        tipo.GetProperty("NavigationManager", flags)!.SetValue(componente, new NavegacionManipulada());
        tipo.GetProperty("EnlacesPublicos", flags)!.SetValue(componente, SeguridadCuentaPruebas.Enlaces(null));
        tipo.GetProperty("EmailService", flags)!.SetValue(componente, new CorreoProhibido());
        tipo.GetProperty("TextosUsuarios", flags)!.SetValue(componente, new LocalizadorUsuario());
        var enlace = tipo.GetMethod("EnlaceActivacion", flags)!.Invoke(componente, [Guid.Empty, "codigo"]);
        var envio = (Task<CaeManager.Domain.Common.Result>)tipo.GetMethod("EnviarCorreoActivacionAsync", flags)!
            .Invoke(componente, [Guid.Empty, "ana@example.test", "Ana", enlace, CancellationToken.None])!;
        (await envio).EsFallido.Should().BeTrue();
    }

    private sealed class LocalizadorUsuario : Microsoft.Extensions.Localization.IStringLocalizer<CaeManager.Web.Features.Usuarios.Recursos.TextosUsuarios>
    {
        public Microsoft.Extensions.Localization.LocalizedString this[string name] => new(name, name);
        public Microsoft.Extensions.Localization.LocalizedString this[string name, params object[] arguments] => new(name, name);
        public IEnumerable<Microsoft.Extensions.Localization.LocalizedString> GetAllStrings(bool includeParentCultures) => [];
    }
    private sealed class CorreoProhibido : CaeManager.Application.Common.IEmailService
    {
        public Task<CaeManager.Domain.Common.Result> EnviarAsync(string destinatarioEmail, string asunto, string cuerpoHtml,
            CaeManager.Application.Common.TipoAvisoCorreo tipo, string? responderA = null, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("No debe enviarse un enlace sin base pública.");
    }
    private sealed class NavegacionManipulada : Microsoft.AspNetCore.Components.NavigationManager
    {
        public NavegacionManipulada() => Initialize("https://host-manipulado.example/", "https://host-manipulado.example/");
        protected override void NavigateToCore(string uri, bool forceLoad) => throw new NotSupportedException();
    }
}