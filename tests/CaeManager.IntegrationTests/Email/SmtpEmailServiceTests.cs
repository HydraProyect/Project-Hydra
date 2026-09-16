using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using CaeManager.Infrastructure.Email;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace CaeManager.IntegrationTests.Email;

/// <summary>
/// Auditoría del correo de activación de cuenta: el fallo de envío por
/// configuración ausente solo llegaba a <c>LogWarning</c>, nunca a
/// <c>LogError</c>, y Sentry no captura por debajo de Error — un fallo real
/// en producción no dejaba ningún rastro visible salvo revisar el log del
/// servidor a mano. Este test cierra ese camino de fallo silencioso.
/// </summary>
public class SmtpEmailServiceTests
{
    [Fact]
    public async Task Sin_Smtp_configurado_el_fallo_llega_como_error_no_como_aviso()
    {
        var logger = new LoggerEspia();
        var servicio = new SmtpEmailService(Options.Create(new SmtpEmailOptions()), logger);

        var resultado = await servicio.EnviarAsync("destino@ejemplo.com", "Asunto", "<p>Cuerpo</p>");

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Email.NoConfigurado");
        logger.Errores.Should().ContainSingle(
            "un fallo por configuración ausente no puede quedar en Warning: es el único rastro que existe cuando " +
            "Sentry:Dsn está vacío (como en producción hoy) y el modal de activación no lo cubre — por ejemplo, " +
            "una notificación de rol ya asignado");
    }

    [Fact]
    public async Task Con_servidor_SMTP_inalcanzable_el_fallo_llega_como_error()
    {
        var logger = new LoggerEspia();
        var opciones = Options.Create(new SmtpEmailOptions
        {
            Host = "127.0.0.1",
            Puerto = 1,
            Usuario = "usuario",
            Contrasena = "contrasena",
            BuzonRemitente = "info@talveg.es",
        });
        var servicio = new SmtpEmailService(opciones, logger);

        var resultado = await servicio.EnviarAsync("destino@ejemplo.com", "Asunto", "<p>Cuerpo</p>");

        resultado.EsFallido.Should().BeTrue();
        logger.Errores.Should().ContainSingle();
    }

    /// <summary>
    /// Reproduce en aislado el defecto real de dinahosting: el certificado del
    /// servidor es un comodín compartido (<c>*.correoseguro.dinaserver.com</c>)
    /// que nunca coincide con <c>mail.talveg.es</c>, el único host que resuelve
    /// por DNS. Sin esta validación por SAN, <c>System.Net.Mail.SmtpClient</c>
    /// (la implementación anterior) rechaza el certificado y el envío falla
    /// siempre, en producción incluida — verificado en vivo el 2026-09-16 con
    /// un handshake TLS real contra mail.talveg.es.
    /// </summary>
    [Fact]
    public void Certificado_con_SAN_distinto_del_host_pero_igual_al_nombre_configurado_se_acepta()
    {
        var config = new SmtpEmailOptions
        {
            Host = "mail.talveg.es",
            NombreCertificadoTls = "correoseguro.dinaserver.com",
        };

        using var certificado = CrearCertificadoConSan("correoseguro.dinaserver.com");

        var aceptado = SmtpEmailService.ValidarCertificadoServidor(
            config, certificado, cadena: null, SslPolicyErrors.RemoteCertificateNameMismatch);

        aceptado.Should().BeTrue();
    }

    [Fact]
    public void Certificado_cuyo_SAN_no_coincide_con_ningun_nombre_esperado_se_rechaza()
    {
        var config = new SmtpEmailOptions
        {
            Host = "mail.talveg.es",
            NombreCertificadoTls = "correoseguro.dinaserver.com",
        };

        using var certificado = CrearCertificadoConSan("otro-servidor.example.com");

        var aceptado = SmtpEmailService.ValidarCertificadoServidor(
            config, certificado, cadena: null, SslPolicyErrors.RemoteCertificateNameMismatch);

        aceptado.Should().BeFalse();
    }

    [Fact]
    public void Un_error_de_cadena_de_confianza_se_rechaza_aunque_el_nombre_coincida()
    {
        var config = new SmtpEmailOptions
        {
            Host = "mail.talveg.es",
            NombreCertificadoTls = "correoseguro.dinaserver.com",
        };

        using var certificado = CrearCertificadoConSan("correoseguro.dinaserver.com");

        var aceptado = SmtpEmailService.ValidarCertificadoServidor(
            config, certificado, cadena: null,
            SslPolicyErrors.RemoteCertificateNameMismatch | SslPolicyErrors.RemoteCertificateChainErrors);

        aceptado.Should().BeFalse(
            "relajar la comprobación de nombre no puede convertirse en aceptar cualquier cadena de confianza rota");
    }

    private static X509Certificate2 CrearCertificadoConSan(string nombreSan)
    {
        using var rsa = RSA.Create(2048);
        var solicitud = new CertificateRequest($"CN={nombreSan}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        var constructorSan = new SubjectAlternativeNameBuilder();
        constructorSan.AddDnsName(nombreSan);
        solicitud.CertificateExtensions.Add(constructorSan.Build());

        return solicitud.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
    }

    private sealed class LoggerEspia : ILogger<SmtpEmailService>
    {
        public List<string> Errores { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Error) Errores.Add(formatter(state, exception));
        }
    }
}
