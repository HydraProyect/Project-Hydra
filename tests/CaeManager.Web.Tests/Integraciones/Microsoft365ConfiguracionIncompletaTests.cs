using CaeManager.Infrastructure.Integraciones;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace CaeManager.Web.Tests.Integraciones;

/// <summary>
/// Reserva de la revisión de #739: con <see cref="Microsoft365GraphOptions.EstaConfigurado"/> a
/// <c>false</c> no se registran la ingesta del webhook ni la renovación de la suscripción, y no
/// quedaba ningún rastro. El aviso de arranque dice QUÉ falta (nunca valores ni rutas).
/// </summary>
public class Microsoft365ConfiguracionIncompletaTests
{
    private const string Secreto = "secreto-que-no-debe-salir-en-el-log";
    private const string RutaCertificado = "/run/secretos/RUTA-QUE-NO-DEBE-SALIR-cert.pem";
    private const string RutaClave = "/run/secretos/RUTA-QUE-NO-DEBE-SALIR-clave.pem";

    [Fact]
    public void Sin_nada_informado_no_hay_problemas_porque_apagado_es_lo_normal()
    {
        new Microsoft365GraphOptions().ProblemasDeConfiguracion().Should().BeEmpty();
    }

    [Fact]
    public void Configurado_con_certificado_o_con_secreto_no_hay_problemas()
    {
        new Microsoft365GraphOptions
        {
            ClientId = "id",
            UrlPublicaBase = "https://x",
            CertificadoRuta = RutaCertificado,
            ClavePrivadaRuta = RutaClave,
        }.ProblemasDeConfiguracion().Should().BeEmpty();

        new Microsoft365GraphOptions { ClientId = "id", UrlPublicaBase = "https://x", ClientSecret = Secreto }
            .ProblemasDeConfiguracion().Should().BeEmpty();
    }

    [Fact]
    public void Solo_la_ruta_del_certificado_dice_que_falta_la_clave_privada()
    {
        var problemas = new Microsoft365GraphOptions
        {
            ClientId = "id",
            UrlPublicaBase = "https://x",
            ClientSecret = Secreto,
            CertificadoRuta = RutaCertificado,
        }.ProblemasDeConfiguracion();

        problemas.Should().ContainSingle().Which.Should().Contain("CertificadoRuta").And.Contain("falta ClavePrivadaRuta");
    }

    [Fact]
    public void Solo_la_clave_privada_dice_que_falta_la_ruta_del_certificado()
    {
        var problemas = new Microsoft365GraphOptions
        {
            ClientId = "id",
            UrlPublicaBase = "https://x",
            ClientSecret = Secreto,
            ClavePrivadaRuta = RutaClave,
        }.ProblemasDeConfiguracion();

        problemas.Should().ContainSingle().Which.Should().Contain("ClavePrivadaRuta").And.Contain("falta CertificadoRuta");
    }

    [Fact]
    public void Un_ClientId_sin_credencial_ni_url_lo_dice()
    {
        var problemas = new Microsoft365GraphOptions { ClientId = "id" }.ProblemasDeConfiguracion();

        problemas.Should().HaveCount(2);
        problemas.Should().Contain(p => p.Contains("UrlPublicaBase"));
        problemas.Should().Contain(p => p.Contains("credencial"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task El_servicio_de_arranque_avisa_con_LogWarning_de_lo_que_falta_y_sin_valores(bool faltaLaClave)
    {
        var opciones = new Microsoft365GraphOptions { ClientId = "id", UrlPublicaBase = "https://x", ClientSecret = Secreto };
        if (faltaLaClave) opciones.CertificadoRuta = RutaCertificado; else opciones.ClavePrivadaRuta = RutaClave;
        var logger = new LoggerCapturador();

        await new AvisoConfiguracionMicrosoft365HostedService(Options.Create(opciones), logger).StartAsync(CancellationToken.None);

        var aviso = logger.Entradas.Should().ContainSingle().Which;
        aviso.Nivel.Should().Be(LogLevel.Warning);
        aviso.Texto.Should().Contain(faltaLaClave ? "falta ClavePrivadaRuta" : "falta CertificadoRuta");
        aviso.Texto.Should().Contain("NO se han registrado", "debe decir la consecuencia, no solo el síntoma");
        aviso.Texto.Should().NotContain(Secreto).And.NotContain("RUTA-QUE-NO-DEBE-SALIR", "ni secretos ni rutas en el log");
    }

    [Fact]
    public async Task El_servicio_de_arranque_no_avisa_si_esta_apagado_ni_si_esta_completo()
    {
        var logger = new LoggerCapturador();

        await new AvisoConfiguracionMicrosoft365HostedService(Options.Create(new Microsoft365GraphOptions()), logger)
            .StartAsync(CancellationToken.None);
        await new AvisoConfiguracionMicrosoft365HostedService(
                Options.Create(new Microsoft365GraphOptions { ClientId = "id", UrlPublicaBase = "https://x", ClientSecret = Secreto }), logger)
            .StartAsync(CancellationToken.None);

        logger.Entradas.Should().BeEmpty();
    }

    /// <summary>
    /// El cableado real de <c>AddInfrastructure</c>: qué servicios de fondo del conector se registran en
    /// cada caso. Fija a la vez las dos propiedades que importan: el aviso aparece SOLO con configuración a
    /// medias o con certificado (para comprobar su legibilidad), y la ingesta y la renovación siguen
    /// registrándose exactamente cuando lo hacían antes.
    /// </summary>
    [Theory]
    [InlineData("nada", false, false)]
    [InlineData("a-medias", false, true)]
    [InlineData("completa-con-secreto", true, false)]
    [InlineData("completa-con-certificado", true, true)]
    public void El_registro_pone_el_aviso_con_configuracion_a_medias_o_con_certificado_y_no_cambia_cuando_arrancan_los_otros_dos(
        string caso, bool ingestaYRenovacion, bool aviso)
    {
        var valores = new Dictionary<string, string?>();
        switch (caso)
        {
            case "a-medias":
                valores["Integraciones:Microsoft365:ClientId"] = "id";
                valores["Integraciones:Microsoft365:UrlPublicaBase"] = "https://x";
                valores["Integraciones:Microsoft365:ClientSecret"] = Secreto;
                valores["Integraciones:Microsoft365:CertificadoRuta"] = RutaCertificado;
                break;
            case "completa-con-secreto":
                valores["Integraciones:Microsoft365:ClientId"] = "id";
                valores["Integraciones:Microsoft365:UrlPublicaBase"] = "https://x";
                valores["Integraciones:Microsoft365:ClientSecret"] = Secreto;
                break;
            case "completa-con-certificado":
                valores["Integraciones:Microsoft365:ClientId"] = "id";
                valores["Integraciones:Microsoft365:UrlPublicaBase"] = "https://x";
                valores["Integraciones:Microsoft365:CertificadoRuta"] = RutaCertificado;
                valores["Integraciones:Microsoft365:ClavePrivadaRuta"] = RutaClave;
                break;
        }

        var configuracion = new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(valores).Build();
        var servicios = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        CaeManager.Infrastructure.DependencyInjection.InfrastructureServiceCollectionExtensions
            .AddInfrastructure(servicios, configuracion, new EntornoFalso());

        var hospedados = servicios
            .Where(d => d.ServiceType == typeof(Microsoft.Extensions.Hosting.IHostedService))
            .Select(d => d.ImplementationType)
            .ToList();

        // Control positivo del instrumento: si la enumeración no viera NINGÚN servicio de fondo, los
        // «no contiene» de abajo pasarían sin observar nada.
        hospedados.Should().Contain(typeof(RedaccionPayloadWebhookHostedService), "este servicio se registra siempre");

        hospedados.Contains(typeof(IngestaWebhookHostedService)).Should().Be(ingestaYRenovacion);
        hospedados.Contains(typeof(RenovacionSuscripcionWebhookHostedService)).Should().Be(ingestaYRenovacion);
        hospedados.Contains(typeof(AvisoConfiguracionMicrosoft365HostedService)).Should().Be(aviso);
    }

    private sealed class EntornoFalso : Microsoft.Extensions.Hosting.IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";
        public string ApplicationName { get; set; } = "CaeManager";
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }

    private sealed class LoggerCapturador : ILogger<AvisoConfiguracionMicrosoft365HostedService>
    {
        public List<(LogLevel Nivel, string Texto)> Entradas { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entradas.Add((logLevel, formatter(state, exception)));
    }
}
