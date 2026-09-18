using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using CaeManager.Application.Common;
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
    /// Reproduce en aislado el certificado REAL de dinahosting (dos SAN: el
    /// comodín compartido <c>*.correoseguro.dinaserver.com</c> y, además, el
    /// nombre exacto sin comodín — confirmado con <c>openssl x509 -text</c> el
    /// 2026-09-16), que nunca coincide con <c>mail.talveg.es</c>, el único
    /// host que resuelve por DNS. Sin esta validación por SAN,
    /// <c>System.Net.Mail.SmtpClient</c> (la implementación anterior) rechaza
    /// el certificado y el envío falla siempre, en producción incluida.
    ///
    /// <para>
    /// Revisión de Codex (P1): un test con un único SAN exacto no distingue
    /// "coincide por igualdad" de "coincide por comodín", y
    /// <see cref="SmtpEmailOptions.NombreCertificadoTls"/> configurado como
    /// <c>correoseguro.dinaserver.com</c> (sin comodín) nunca puede
    /// satisfacer la rama de comodín de <c>CoincideDominio</c> — un dominio
    /// pelado jamás es "más largo" que su propio sufijo con comodín. La
    /// aceptación real depende, hoy, de que el SAN exacto siga publicado
    /// junto al comodín; ver el test de rechazo justo debajo, que deja
    /// escrita esa dependencia en vez de darla por sentada.
    /// </para>
    /// </summary>
    [Fact]
    public void Certificado_real_con_comodin_y_nombre_exacto_se_acepta()
    {
        var config = new SmtpEmailOptions
        {
            Host = "mail.talveg.es",
            NombreCertificadoTls = "correoseguro.dinaserver.com",
        };

        using var certificado = CrearCertificadoConSan("*.correoseguro.dinaserver.com", "correoseguro.dinaserver.com");

        var aceptado = SmtpEmailService.ValidarCertificadoServidor(
            config, certificado, cadena: null, SslPolicyErrors.RemoteCertificateNameMismatch);

        aceptado.Should().BeTrue();
    }

    /// <summary>
    /// Documenta la fragilidad que señaló la revisión: un comodín nunca
    /// coincide con el dominio pelado que anuncia (correcto según RFC 6125 —
    /// <c>*.dominio</c> exige al menos una etiqueta debajo, nunca cero). Si
    /// dinahosting alguna vez publicara <b>solo</b> el comodín, sin el SAN
    /// exacto que hoy acompaña, el envío volvería a fallar en silencio salvo
    /// que se reconfigure <c>Smtp:NombreCertificadoTls</c> con una etiqueta
    /// real bajo el comodín (p. ej. <c>mail.correoseguro.dinaserver.com</c>).
    /// </summary>
    [Fact]
    public void Certificado_que_solo_trae_el_comodin_sin_el_nombre_exacto_se_rechaza()
    {
        var config = new SmtpEmailOptions
        {
            Host = "mail.talveg.es",
            NombreCertificadoTls = "correoseguro.dinaserver.com",
        };

        using var certificado = CrearCertificadoConSan("*.correoseguro.dinaserver.com");

        var aceptado = SmtpEmailService.ValidarCertificadoServidor(
            config, certificado, cadena: null, SslPolicyErrors.RemoteCertificateNameMismatch);

        aceptado.Should().BeFalse(
            "un comodín no cubre el dominio pelado que anuncia — la aceptación depende del SAN exacto, no del comodín");
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

    private static X509Certificate2 CrearCertificadoConSan(params string[] nombresSan)
    {
        using var rsa = RSA.Create(2048);
        var solicitud = new CertificateRequest($"CN={nombresSan[0]}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        var constructorSan = new SubjectAlternativeNameBuilder();
        foreach (var nombreSan in nombresSan) constructorSan.AddDnsName(nombreSan);
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

/// <summary>
/// Sistema de correo TALVEG: el envoltorio de marca lo aplica
/// <c>SmtpEmailService</c>, no cada llamador — estos tests fijan el
/// contrato de <c>EnvolverEnPlantillaDeMarca</c> para que un cambio futuro
/// no lo rompa en silencio.
///
/// <para>
/// Clase de nivel superior, no anidada en <see cref="SmtpEmailServiceTests"/>:
/// <c>scripts/repartir-clases-de-test.sh</c> reparte los bloques de CI
/// descubriendo clases por su <c>FullyQualifiedName</c>, y una clase anidada
/// aparece ahí como <c>Externa+Anidada</c> — el <c>+</c> rompe el regex de
/// reparto, que la confunde con el namespace y duplica la ejecución de la
/// clase externa en dos bloques (CI del PR #698, "Los bloques ejecutaron
/// 1241 tests de 1235").
/// </para>
/// </summary>
public class SmtpEmailServiceEnvolverEnPlantillaDeMarcaTests
{
    [Fact]
    public void Sin_UrlBasePublica_la_franja_de_marca_es_texto_no_imagen()
    {
        var html = SmtpEmailService.EnvolverEnPlantillaDeMarca(
            "<p>contenido</p>", TipoAvisoCorreo.Transaccional, new SmtpEmailOptions());

        html.Should().NotContain("<img", "sin URL pública configurada no hay dónde alojar la imagen");
        html.Should().Contain("TALVEG");
    }

    [Fact]
    public void Con_UrlBasePublica_la_franja_de_marca_es_una_imagen_servida_por_esa_url()
    {
        var html = SmtpEmailService.EnvolverEnPlantillaDeMarca(
            "<p>contenido</p>", TipoAvisoCorreo.Transaccional,
            new SmtpEmailOptions { UrlBasePublica = "https://app.talveg.es" });

        html.Should().Contain("<img src=\"https://app.talveg.es/img/correo/franja-marca.png\"");
    }

    [Fact]
    public void El_pie_de_seguridad_dice_que_no_se_puede_desactivar()
    {
        var html = SmtpEmailService.EnvolverEnPlantillaDeMarca(
            "<p>contenido</p>", TipoAvisoCorreo.Seguridad,
            new SmtpEmailOptions { BuzonRemitente = "info@talveg.es" });

        html.Should().Contain("no se puede desactivar");
        html.Should().Contain("mailto:info@talveg.es");
    }

    [Fact]
    public void El_pie_informativo_explica_el_motivo_de_recibirlo()
    {
        var html = SmtpEmailService.EnvolverEnPlantillaDeMarca(
            "<p>contenido</p>", TipoAvisoCorreo.Informativo, new SmtpEmailOptions());

        html.Should().Contain("responsabilidad de coordinación");
    }

    [Fact]
    public void El_contenido_del_llamador_llega_intacto_dentro_del_envoltorio()
    {
        const string cuerpo = "<h3>Título</h3><p>Un párrafo con <a href=\"https://x\">enlace</a>.</p>";

        var html = SmtpEmailService.EnvolverEnPlantillaDeMarca(cuerpo, TipoAvisoCorreo.Transaccional, new SmtpEmailOptions());

        html.Should().Contain(cuerpo, "el envoltorio no debe alterar lo que ya compone cada llamador");
    }
}
