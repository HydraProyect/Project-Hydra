using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using CaeManager.Application.Common;
using CaeManager.Application.Reclamaciones.Commands.EnviarReclamacion;
using CaeManager.Infrastructure.Email;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MimeKit;
using MimeKit.Text;
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

        var resultado = await servicio.EnviarAsync("destino@ejemplo.com", "Asunto", "<p>Cuerpo</p>", TipoAvisoCorreo.Transaccional, new EncabezadoCorreo(AccionEsperada.Ninguna, "Prueba", "Vista previa"));

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

        var resultado = await servicio.EnviarAsync("destino@ejemplo.com", "Asunto", "<p>Cuerpo</p>", TipoAvisoCorreo.Transaccional, new EncabezadoCorreo(AccionEsperada.Ninguna, "Prueba", "Vista previa"));

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
    private static readonly EncabezadoCorreo Enc = new(AccionEsperada.Ninguna, "Prueba", "Vista previa");

    [Fact]
    public void Sin_UrlBasePublica_la_franja_de_marca_es_texto_no_imagen()
    {
        var html = SmtpEmailService.EnvolverEnPlantillaDeMarca(
            "<p>contenido</p>", TipoAvisoCorreo.Transaccional, Enc, new SmtpEmailOptions());

        html.Should().NotContain("<img", "sin URL pública configurada no hay dónde alojar la imagen");
        html.Should().Contain("TALVEG");
    }

    [Fact]
    public void Con_UrlBasePublica_la_franja_de_marca_es_una_imagen_servida_por_esa_url()
    {
        var html = SmtpEmailService.EnvolverEnPlantillaDeMarca(
            "<p>contenido</p>", TipoAvisoCorreo.Transaccional, Enc,
            new SmtpEmailOptions { UrlBasePublica = "https://app.talveg.es" });

        html.Should().Contain("<img src=\"https://app.talveg.es/img/correo/franja-marca.png\"");
    }

    [Fact]
    public void El_pie_de_seguridad_dice_que_no_se_puede_desactivar()
    {
        var html = SmtpEmailService.EnvolverEnPlantillaDeMarca(
            "<p>contenido</p>", TipoAvisoCorreo.Seguridad, Enc,
            new SmtpEmailOptions { BuzonRemitente = "info@talveg.es" });

        html.Should().Contain("no se puede desactivar");
        html.Should().Contain("mailto:info@talveg.es");
    }

    [Fact]
    public void El_pie_informativo_explica_el_motivo_de_recibirlo()
    {
        var html = SmtpEmailService.EnvolverEnPlantillaDeMarca(
            "<p>contenido</p>", TipoAvisoCorreo.Informativo, Enc, new SmtpEmailOptions());

        html.Should().Contain("responsabilidad de coordinación");
    }

    [Fact]
    public void El_pie_de_requerimiento_no_invita_a_responder_porque_el_correo_no_lleva_ReplyTo()
    {
        var html = SmtpEmailService.EnvolverEnPlantillaDeMarca(
            "<p>contenido</p>", TipoAvisoCorreo.Requerimiento, Enc, new SmtpEmailOptions());

        html.Should().Contain("en nombre de quien te lo reclama");
        html.Should().NotContain("responder",
            "el correo sale con From = buzón de TALVEG y sin Reply-To: una respuesta no llegaría a quien reclama");
    }

    /// <summary>
    /// La otra mitad del test de #698, y el motivo por el que aquel decía
    /// "porque el correo no lleva ReplyTo" en vez de "nunca": con la decisión
    /// D3 la respuesta sí tiene a dónde ir —el Gestor CAE que reclama— y
    /// callarlo deja al destinatario sin saber que puede contestar.
    /// </summary>
    [Fact]
    public void El_pie_de_requerimiento_invita_a_responder_cuando_hay_ReplyTo()
    {
        var html = SmtpEmailService.EnvolverEnPlantillaDeMarca(
            "<p>contenido</p>", TipoAvisoCorreo.Requerimiento, Enc, new SmtpEmailOptions(),
            responderA: "marta@arcosspa.example");

        html.Should().Contain("en nombre de quien te lo reclama");
        html.Should().Contain("Puedes responder a este mensaje: tu respuesta le llegará directamente a esa persona");
    }

    [Fact]
    public void El_pie_de_requerimiento_no_escribe_la_direccion_de_respuesta_en_el_cuerpo()
    {
        var html = SmtpEmailService.EnvolverEnPlantillaDeMarca(
            "<p>contenido</p>", TipoAvisoCorreo.Requerimiento, Enc, new SmtpEmailOptions(),
            responderA: "marta@arcosspa.example");

        html.Should().NotContain(
            "marta@arcosspa.example",
            "la dirección ya viaja en la cabecera Reply-To; repetirla en el cuerpo solo añade un dato personal " +
            "más a un correo que sale fuera de la organización");
    }

    /// <summary>
    /// Un <c>Reply-To</c> que no invitara a responder sería tan inútil como
    /// no ponerlo: los dos tests de arriba y este describen la misma regla
    /// desde sus dos lados, y ninguno de los otros tipos de aviso la toca.
    /// </summary>
    [Theory]
    [InlineData(TipoAvisoCorreo.Informativo)]
    [InlineData(TipoAvisoCorreo.Transaccional)]
    public void Un_ReplyTo_no_cambia_el_pie_de_los_demas_tipos_de_aviso(TipoAvisoCorreo tipo)
    {
        var sinReplyTo = SmtpEmailService.EnvolverEnPlantillaDeMarca("<p>contenido</p>", tipo, Enc, new SmtpEmailOptions());
        var conReplyTo = SmtpEmailService.EnvolverEnPlantillaDeMarca(
            "<p>contenido</p>", tipo, Enc, new SmtpEmailOptions(), responderA: "marta@arcosspa.example");

        conReplyTo.Should().Be(sinReplyTo);
    }

    [Fact]
    public void El_contenido_del_llamador_llega_intacto_dentro_del_envoltorio()
    {
        const string cuerpo = "<h3>Título</h3><p>Un párrafo con <a href=\"https://x\">enlace</a>.</p>";

        var html = SmtpEmailService.EnvolverEnPlantillaDeMarca(cuerpo, TipoAvisoCorreo.Transaccional, Enc, new SmtpEmailOptions());

        html.Should().Contain(cuerpo, "el envoltorio no debe alterar lo que ya compone cada llamador");
    }

    [Theory]
    [InlineData(TipoAvisoCorreo.Seguridad, AccionEsperada.Accion, "Seguridad · Acción requerida")]
    [InlineData(TipoAvisoCorreo.Seguridad, AccionEsperada.Ninguna, "Seguridad · No requiere acción")]
    [InlineData(TipoAvisoCorreo.Informativo, AccionEsperada.Revision, "Aviso · Requiere revisión")]
    [InlineData(TipoAvisoCorreo.Requerimiento, AccionEsperada.Respuesta, "Requerimiento · Respuesta necesaria")]
    public void La_franja_de_tipo_dice_la_clase_del_aviso_y_si_hay_que_hacer_algo_con_texto(
        TipoAvisoCorreo tipo, AccionEsperada accion, string esperado)
    {
        var html = SmtpEmailService.EnvolverEnPlantillaDeMarca(
            "<p>x</p>", tipo, new EncabezadoCorreo(accion, "Tu cuenta", "vista previa"), new SmtpEmailOptions());

        html.Should().Contain(esperado, "el receptor tiene que saber qué es el correo y si le toca hacer algo, sin depender del color");
        html.Should().Contain(">Tu cuenta<", "el ámbito va en la misma franja");
    }

    [Fact]
    public void El_preheader_va_oculto_y_codificado()
    {
        var html = SmtpEmailService.EnvolverEnPlantillaDeMarca(
            "<p>x</p>", TipoAvisoCorreo.Informativo,
            new EncabezadoCorreo(AccionEsperada.Ninguna, "Documentación", "3 <vencidos> & 2 urgentes"), new SmtpEmailOptions());

        html.Should().Contain("display:none");
        html.Should().Contain("3 &lt;vencidos&gt; &amp; 2 urgentes", "el texto de vista previa no puede inyectar HTML");
    }

    [Fact]
    public void El_modo_noche_es_una_mejora_y_el_diseno_base_no_depende_de_ella()
    {
        var html = SmtpEmailService.EnvolverEnPlantillaDeMarca(
            "<p>x</p>", TipoAvisoCorreo.Seguridad, Enc, new SmtpEmailOptions { UrlBasePublica = "https://app.talveg.es" });

        html.Should().Contain("prefers-color-scheme: dark");
        html.Should().Contain("name=\"color-scheme\"");
        // El fondo va en bgcolor Y en style: si el cliente descarta la hoja <style> (o invierte por su cuenta),
        // el bloque conserva su color propio y el lockup no queda crema sobre blanco.
        html.Should().Contain("bgcolor=\"#122A21\" style=\"background:#122A21;");
        html.Should().Contain("bgcolor=\"#FBFAF6\"");
    }
}

/// <summary>
/// Piezas de HTML que componen los llamadores (<c>CorreoHtml</c>). Clase de
/// nivel superior por el mismo motivo que las anteriores.
/// </summary>
public class CorreoHtmlTests
{
    [Fact]
    public void El_boton_lleva_fondo_borde_y_ancho_completo_explicitos()
    {
        var html = CorreoHtml.BotonCta("Establecer mi contraseña", "https://app.talveg.es/x?a=1&b=2");

        html.Should().Contain("bgcolor=\"#122A21\"");
        html.Should().Contain("border:2px solid #8FC7BC", "el borde celeste es lo que salva el botón cuando el cliente invierte colores");
        html.Should().Contain("width=\"100%\"");
        html.Should().Contain("href=\"https://app.talveg.es/x?a=1&amp;b=2\"", "la URL se codifica");
    }

    [Theory]
    [InlineData(CorreoHtml.EstadoVisual.Vencido, "Vencido")]
    [InlineData(CorreoHtml.EstadoVisual.Urgente, "Urgente")]
    [InlineData(CorreoHtml.EstadoVisual.Proximo, "Próximo")]
    [InlineData(CorreoHtml.EstadoVisual.SinSubir, "Sin subir")]
    public void Un_estado_lleva_palabra_y_nunca_verde_ni_celeste_de_marca(CorreoHtml.EstadoVisual estado, string palabra)
    {
        var html = CorreoHtml.Estado(estado);

        html.Should().Contain(palabra, "el significado no puede depender solo del color ni del símbolo");
        html.Should().NotContain("#122A21").And.NotContain("#8FC7BC",
            "dentro de la app el verde es «Vigente»: usarlo aquí para un estado sería una trampa");
    }

    [Fact]
    public void El_cuerpo_de_la_reclamacion_marca_vencido_o_proximo_con_palabra_y_no_lleva_boton()
    {
        var ayer = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-1);
        var manana = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(1);

        var html = CuerpoDeReclamacion.Construir(
            "Refrielectric <S.L.>", [((string?)"Juan", "Seguro RC", ayer), ("Ana", "Formación PRL", manana)]);

        html.Should().Contain("Vencido").And.Contain("Próximo");
        html.Should().Contain("Refrielectric &lt;S.L.&gt;", "la razón social viene de datos y se codifica");
        html.Should().NotContain("cta-a", "no hay enlace de subida: la acción es responder, no un botón inventado");
    }

    [Fact]
    public void La_reclamacion_de_empresa_no_lleva_la_columna_Trabajador()
    {
        var html = CuerpoDeReclamacion.Construir(
            "Refrielectric", [((string?)null, "Seguro RC", new DateOnly(2026, 9, 3))]);

        html.Should().NotContain("Trabajador");
    }

    [Fact]
    public void Que_debes_hacer_pide_responder_solo_si_hay_ReplyTo()
    {
        CuerpoDeReclamacion.QueDebesHacer(true).Should().Contain("responde a este correo");
        CuerpoDeReclamacion.QueDebesHacer(false).Should().NotContain("responde",
            "sin Reply-To la respuesta no llegaría a quien reclama: no se promete");
    }

    [Fact]
    public void Titulo_y_texto_del_boton_se_codifican()
    {
        CorreoHtml.Titulo("<b>x</b>").Should().Contain("&lt;b&gt;x&lt;/b&gt;");
        CorreoHtml.BotonCta("<i>", "https://x").Should().Contain("&lt;i&gt;");
    }
}

/// <summary>
/// El mensaje que de verdad sale por el hilo (decisión D3, 2026-09-19).
/// Comprueba las cabeceras sobre el <see cref="MimeMessage"/> construido, sin
/// abrir conexión SMTP: el servidor de pruebas no añadiría nada aquí — quien
/// decide qué cabeceras lleva el correo es
/// <c>SmtpEmailService.ConstruirMensaje</c>, y el envío posterior no las toca.
///
/// <para>
/// Clase de nivel superior por el mismo motivo que
/// <see cref="SmtpEmailServiceEnvolverEnPlantillaDeMarcaTests"/>: una clase
/// anidada rompe el regex de <c>scripts/repartir-clases-de-test.sh</c>.
/// </para>
/// </summary>
public class SmtpEmailServiceConstruirMensajeTests
{
    private static readonly EncabezadoCorreo Enc = new(AccionEsperada.Ninguna, "Prueba", "Vista previa");

    private static SmtpEmailOptions Opciones() => new()
    {
        Host = "mail.talveg.es",
        Puerto = 587,
        Usuario = "usuario",
        Contrasena = "contrasena",
        BuzonRemitente = "info@talveg.es",
    };

    [Fact]
    public void La_reclamacion_lleva_ReplyTo_al_Gestor_CAE_que_reclama()
    {
        var mensaje = SmtpEmailService.ConstruirMensaje(
            "contacto@refrielectric.example", "Documentación pendiente", "<p>Nos faltan los EPI.</p>",
            TipoAvisoCorreo.Requerimiento, Enc, "marta@arcosspa.example", Opciones(), NullLogger.Instance);

        mensaje.ReplyTo.Mailboxes.Should().ContainSingle()
            .Which.Address.Should().Be("marta@arcosspa.example");
    }

    [Fact]
    public void El_From_sigue_siendo_el_buzon_de_TALVEG_aunque_haya_ReplyTo()
    {
        var mensaje = SmtpEmailService.ConstruirMensaje(
            "contacto@refrielectric.example", "Documentación pendiente", "<p>Nos faltan los EPI.</p>",
            TipoAvisoCorreo.Requerimiento, Enc, "marta@arcosspa.example", Opciones(), NullLogger.Instance);

        mensaje.From.Mailboxes.Should().ContainSingle()
            .Which.Address.Should().Be(
                "info@talveg.es",
                "es el buzón con SPF y DKIM publicados: sustituirlo por el correo de una persona haría que su " +
                "dominio no autorizase a nuestro servidor");
    }

    [Fact]
    public void Sin_ReplyTo_el_mensaje_no_lleva_esa_cabecera()
    {
        var mensaje = SmtpEmailService.ConstruirMensaje(
            "contacto@refrielectric.example", "Documentación pendiente", "<p>Nos faltan los EPI.</p>",
            TipoAvisoCorreo.Requerimiento, Enc, null, Opciones(), NullLogger.Instance);

        mensaje.ReplyTo.Mailboxes.Should().BeEmpty();
        mensaje.From.Mailboxes.Should().ContainSingle().Which.Address.Should().Be("info@talveg.es");
    }

    /// <summary>
    /// El valor viene de un <c>ApplicationUser</c>, no de un formulario
    /// validado: si no es una dirección, el correo sale igual y sin
    /// <c>Reply-To</c>. Lo contrario —dejar escapar la excepción de
    /// <c>MailboxAddress.Parse</c>— convertiría un dato sucio en un fallo de
    /// la reclamación entera.
    /// </summary>
    [Theory]
    [InlineData("no-es-un-correo")]
    [InlineData("marta@")]
    [InlineData("@arcosspa.example")]
    [InlineData("   ")]
    public void Un_ReplyTo_mal_formado_no_tumba_el_envio_y_el_correo_sale_sin_el(string responderA)
    {
        var mensaje = SmtpEmailService.ConstruirMensaje(
            "contacto@refrielectric.example", "Documentación pendiente", "<p>Nos faltan los EPI.</p>",
            TipoAvisoCorreo.Requerimiento, Enc, responderA, Opciones(), NullLogger.Instance);

        mensaje.ReplyTo.Mailboxes.Should().BeEmpty();
        Assert.IsType<TextPart>(mensaje.Body).Text.Should().NotContain(
            "Puedes responder a este mensaje",
            "sin Reply-To real el pie no puede prometer una respuesta que no llegaría a nadie");
    }

    [Fact]
    public void El_cuerpo_del_mensaje_con_ReplyTo_invita_a_responder()
    {
        var mensaje = SmtpEmailService.ConstruirMensaje(
            "contacto@refrielectric.example", "Documentación pendiente", "<p>Nos faltan los EPI.</p>",
            TipoAvisoCorreo.Requerimiento, Enc, "marta@arcosspa.example", Opciones(), NullLogger.Instance);

        var cuerpo = Assert.IsType<TextPart>(mensaje.Body);
        cuerpo.Text.Should().Contain("Puedes responder a este mensaje: tu respuesta le llegará directamente a esa persona");
    }
}
