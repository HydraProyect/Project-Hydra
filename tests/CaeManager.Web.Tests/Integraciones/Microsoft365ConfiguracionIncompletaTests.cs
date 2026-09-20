using CaeManager.Infrastructure.Configuracion;
using CaeManager.Infrastructure.Integraciones;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
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
    private const string Sensible = "valor-que-no-debe-salir-en-el-log";
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

        new Microsoft365GraphOptions { ClientId = "id", UrlPublicaBase = "https://x", ClientSecret = Sensible }
            .ProblemasDeConfiguracion().Should().BeEmpty();
    }

    [Fact]
    public void Solo_la_ruta_del_certificado_dice_que_falta_la_clave_privada()
    {
        var problemas = new Microsoft365GraphOptions
        {
            ClientId = "id",
            UrlPublicaBase = "https://x",
            ClientSecret = Sensible,
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
            ClientSecret = Sensible,
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

    /// <summary>
    /// La configuración de la composición REAL (<c>AddInfrastructure</c>) para un caso dado. Es la misma que
    /// arranca la aplicación: lo que se comprueba aquí es lo que de verdad se registra, no una réplica.
    /// </summary>
    private static ServiceCollection ComposicionReal(string caso)
    {
        var valores = new Dictionary<string, string?>();
        switch (caso)
        {
            case "a-medias":
                valores["Integraciones:Microsoft365:ClientId"] = "id";
                valores["Integraciones:Microsoft365:UrlPublicaBase"] = "https://x";
                valores["Integraciones:Microsoft365:ClientSecret"] = Sensible;
                valores["Integraciones:Microsoft365:CertificadoRuta"] = RutaCertificado;
                break;
            case "completa-con-secreto":
                valores["Integraciones:Microsoft365:ClientId"] = "id";
                valores["Integraciones:Microsoft365:UrlPublicaBase"] = "https://x";
                valores["Integraciones:Microsoft365:ClientSecret"] = Sensible;
                break;
            case "completa-con-certificado":
                valores["Integraciones:Microsoft365:ClientId"] = "id";
                valores["Integraciones:Microsoft365:UrlPublicaBase"] = "https://x";
                valores["Integraciones:Microsoft365:CertificadoRuta"] = RutaCertificado;
                valores["Integraciones:Microsoft365:ClavePrivadaRuta"] = RutaClave;
                break;
            case "certificado-sin-clientid":
                valores["Integraciones:Microsoft365:UrlPublicaBase"] = "https://x";
                valores["Integraciones:Microsoft365:CertificadoRuta"] = RutaCertificado;
                valores["Integraciones:Microsoft365:ClavePrivadaRuta"] = RutaClave;
                break;
        }

        var configuracion = new ConfigurationBuilder().AddInMemoryCollection(valores).Build();
        var servicios = new ServiceCollection();
        CaeManager.Infrastructure.DependencyInjection.InfrastructureServiceCollectionExtensions
            .AddInfrastructure(servicios, configuracion, new EntornoFalso());
        return servicios;
    }

    private static List<AvisoConfiguracionAMedias> AvisosGenericosDeM365(ServiceCollection servicios) =>
        servicios
            .Where(d => d.ServiceType == typeof(AvisoConfiguracionAMedias))
            .Select(d => (AvisoConfiguracionAMedias)d.ImplementationInstance!)
            .Where(a => a.Seccion == Microsoft365GraphOptions.SeccionConfiguracion)
            .ToList();

    /// <summary>
    /// El aviso de «configuración a medias» de Microsoft 365 va ahora por el mecanismo genérico y SIGUE SONANDO: de
    /// la composición real sale el aviso registrado y, arrancado el servicio genérico real con él, emite el
    /// <c>LogWarning</c> con lo que falta, la consecuencia y ningún valor ni ruta. El riesgo de trasladar un aviso
    /// no es romper el arranque, es que deje de sonar sin que nadie lo note.
    /// </summary>
    [Fact]
    public async Task El_aviso_a_medias_de_Microsoft365_sigue_sonando_por_el_mecanismo_generico_desde_la_composicion_real()
    {
        var avisos = AvisosGenericosDeM365(ComposicionReal("a-medias"));
        avisos.Should().ContainSingle("la composición real registra el aviso de Microsoft 365 con la configuración a medias");

        var captura = new CapturaDeLog();
        var servicios = new ServiceCollection();
        servicios.AddLogging(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(captura));
        foreach (var aviso in avisos)
            servicios.AddSingleton(aviso);
        servicios.AddHostedService<AvisoConfiguracionAMediasHostedService>();

        foreach (var servicio in servicios.BuildServiceProvider().GetServices<IHostedService>())
            await servicio.StartAsync(CancellationToken.None);

        var entrada = captura.Entradas.Should().ContainSingle().Which;
        entrada.Nivel.Should().Be(LogLevel.Warning);
        entrada.Texto.Should().Contain("Integraciones:Microsoft365").And.Contain("falta ClavePrivadaRuta");
        entrada.Texto.Should().Contain("NO se han registrado", "debe decir la consecuencia, no solo el síntoma");
        entrada.Texto.Should().NotContain(Sensible).And.NotContain("RUTA-QUE-NO-DEBE-SALIR", "ni secretos ni rutas en el log");
    }

    [Theory]
    [InlineData("nada", false)]
    [InlineData("a-medias", true)]
    [InlineData("completa-con-secreto", false)]
    [InlineData("completa-con-certificado", false)]
    [InlineData("certificado-sin-clientid", true)]
    public void La_composicion_real_registra_el_aviso_generico_solo_con_configuracion_a_medias(string caso, bool aviso)
    {
        AvisosGenericosDeM365(ComposicionReal(caso)).Any().Should().Be(aviso);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task El_servicio_propio_ya_no_repite_el_aviso_de_configuracion_a_medias(bool conCertificadoCompleto)
    {
        // Sin certificado completo el servicio ni se registra; y con él (configuración a medias por otro
        // motivo: falta ClientId) solo comprueba la legibilidad. En ambos casos NO habla de «a medias».
        var opciones = conCertificadoCompleto
            ? new Microsoft365GraphOptions { UrlPublicaBase = "https://x", CertificadoRuta = RutaCertificado, ClavePrivadaRuta = RutaClave }
            : new Microsoft365GraphOptions { ClientId = "id", UrlPublicaBase = "https://x", ClientSecret = Sensible, CertificadoRuta = RutaCertificado };
        opciones.ProblemasDeConfiguracion().Should().NotBeEmpty("control positivo: la configuración está a medias");
        var logger = new LoggerCapturador();

        await new AvisoConfiguracionMicrosoft365HostedService(Options.Create(opciones), logger, _ => Stream.Null)
            .StartAsync(CancellationToken.None);

        logger.Entradas.Should().BeEmpty("el aviso de «a medias» ya no lo da este servicio: lo da el mecanismo genérico");
    }

    [Fact]
    public async Task Apagado_o_completo_no_se_avisa_ni_por_el_mecanismo_generico_ni_por_el_servicio_propio()
    {
        foreach (var caso in new[] { "nada", "completa-con-secreto" })
            AvisosGenericosDeM365(ComposicionReal(caso)).Should().BeEmpty(caso);

        var logger = new LoggerCapturador();
        await new AvisoConfiguracionMicrosoft365HostedService(Options.Create(new Microsoft365GraphOptions()), logger)
            .StartAsync(CancellationToken.None);
        await new AvisoConfiguracionMicrosoft365HostedService(
                Options.Create(new Microsoft365GraphOptions { ClientId = "id", UrlPublicaBase = "https://x", ClientSecret = Sensible }), logger)
            .StartAsync(CancellationToken.None);

        logger.Entradas.Should().BeEmpty();
    }

    /// <summary>
    /// El cableado real de <c>AddInfrastructure</c>: qué servicios de fondo del conector se registran en cada caso. Fija
    /// las tres propiedades que importan: la ingesta y la renovación se registran exactamente cuando lo hacían antes;
    /// el servicio propio (legibilidad del PEM) se registra SOLO con certificado configurado (con la disyunción de
    /// antes, una configuración a medias lo registraría también y el aviso saldría dos veces); y el aviso de
    /// «a medias» viaja por el mecanismo genérico.
    /// </summary>
    [Theory]
    [InlineData("nada", false, false, false)]
    [InlineData("a-medias", false, false, true)]
    [InlineData("completa-con-secreto", true, false, false)]
    [InlineData("completa-con-certificado", true, true, false)]
    [InlineData("certificado-sin-clientid", false, true, true)]
    public void El_registro_pone_el_servicio_propio_solo_con_certificado_y_no_cambia_cuando_arrancan_los_otros_dos(
        string caso, bool ingestaYRenovacion, bool servicioPropio, bool avisoGenerico)
    {
        var servicios = ComposicionReal(caso);

        var hospedados = servicios
            .Where(d => d.ServiceType == typeof(IHostedService))
            .Select(d => d.ImplementationType)
            .ToList();

        // Control positivo del instrumento: si la enumeración no viera NINGÚN servicio de fondo, los
        // «no contiene» de abajo pasarían sin observar nada.
        hospedados.Should().Contain(typeof(RedaccionPayloadWebhookHostedService), "este servicio se registra siempre");

        hospedados.Contains(typeof(IngestaWebhookHostedService)).Should().Be(ingestaYRenovacion);
        hospedados.Contains(typeof(RenovacionSuscripcionWebhookHostedService)).Should().Be(ingestaYRenovacion);
        hospedados.Contains(typeof(AvisoConfiguracionMicrosoft365HostedService)).Should().Be(servicioPropio);
        AvisosGenericosDeM365(servicios).Any().Should().Be(avisoGenerico);
    }

    /// <summary>
    /// Microsoft 365 no es una conjunción de claves (certificado O secreto), así que su veredicto y su lista de
    /// problemas son propios; este invariante, sobre las 32 combinaciones de las cinco opciones, fija que no discrepen:
    /// configurado no avisa, apagado del todo no avisa, y a medias avisa siempre.
    /// </summary>
    [Fact]
    public void El_veredicto_y_los_problemas_no_discrepan_en_ninguna_combinacion_de_las_cinco_opciones()
    {
        var combinaciones = 0;
        var conAviso = 0;
        for (var mascara = 0; mascara < 32; mascara++)
        {
            var m = mascara;
            bool Informada(int i) => (m & (1 << i)) != 0;
            var opciones = new Microsoft365GraphOptions
            {
                ClientId = Informada(0) ? "id" : null,
                ClientSecret = Informada(1) ? Sensible : null,
                CertificadoRuta = Informada(2) ? RutaCertificado : null,
                ClavePrivadaRuta = Informada(3) ? RutaClave : null,
                UrlPublicaBase = Informada(4) ? "https://x" : null,
            };
            var problemas = opciones.ProblemasDeConfiguracion();
            var contexto = $"combinación {Convert.ToString(mascara, 2).PadLeft(5, '0')}";

            if (opciones.EstaConfigurado)
                problemas.Should().BeEmpty($"configurado no avisa ({contexto})");
            else if (mascara == 0)
                problemas.Should().BeEmpty($"apagado del todo no avisa ({contexto})");
            else
            {
                problemas.Should().NotBeEmpty($"a medias avisa siempre ({contexto})");
                conAviso++;
            }

            problemas.Should().NotContain(p => p.Contains(Sensible) || p.Contains("RUTA-QUE-NO-DEBE-SALIR"), $"({contexto})");
            combinaciones++;
        }

        combinaciones.Should().Be(32);
        conAviso.Should().BeGreaterThan(0, "control positivo: la rejilla contiene casos a medias");
    }

    public sealed record EntradaDeLog(LogLevel Nivel, string Texto);

    private sealed class CapturaDeLog : ILoggerProvider
    {
        public List<EntradaDeLog> Entradas { get; } = [];

        public ILogger CreateLogger(string categoria) => new Registro(this);

        public void Dispose() { }

        private sealed class Registro(CapturaDeLog captura) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter) =>
                captura.Entradas.Add(new EntradaDeLog(logLevel, formatter(state, exception)));
        }
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
