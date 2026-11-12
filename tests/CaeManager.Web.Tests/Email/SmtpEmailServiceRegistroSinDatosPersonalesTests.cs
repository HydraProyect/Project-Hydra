using CaeManager.Application.Common;
using CaeManager.Infrastructure.Email;
using FluentAssertions;
using MailKit;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;
using Xunit;

namespace CaeManager.Web.Tests.Email;

/// <summary>
/// ASVS 4.0.3 V7.1.1: el registro de un fallo SMTP no lleva datos personales. El
/// mensaje de una excepción de MailKit es la respuesta del servidor, que suele citar
/// la dirección rechazada; por eso <see cref="SmtpEmailService"/> registra el tipo,
/// el código SMTP y un identificador de correlación, y nunca la excepción.
/// Aquí el cliente SMTP falla de verdad (un <see cref="SmtpClient"/> que lanza en la
/// operación elegida), no un <c>IEmailService</c> falso: es la capa que registra.
/// </summary>
public class SmtpEmailServiceRegistroSinDatosPersonalesTests
{
    private const string DireccionPersonal = "persona.rechazada@example.com";

    [Theory]
    [InlineData("rechazo-smtp")]
    [InlineData("protocolo")]
    [InlineData("red")]
    [InlineData("credenciales")]
    [InlineData("tiempo")]
    public async Task Un_fallo_del_servidor_se_registra_sin_la_direccion_que_trae_su_respuesta(string fallo)
    {
        var registro = new RegistroConExcepciones();
        var cliente = new ClienteSmtpQueFalla { AlConectar = fallo == "rechazo-smtp" ? null : CrearExcepcion(fallo) };
        if (fallo == "rechazo-smtp")
            cliente.AlEnviar = CrearExcepcion(fallo);
        var servicio = CrearServicio(registro, cliente);

        var resultado = await servicio.EnviarAsync(DireccionPersonal, "Asunto", "<p>Cuerpo</p>", TipoAvisoCorreo.Informativo);

        resultado.EsExitoso.Should().BeFalse();
        var entrada = registro.Entradas.Should().ContainSingle(e => e.Nivel == LogLevel.Error).Subject;
        entrada.Excepcion.Should().BeNull("la excepción entera lleva el mensaje del servidor");
        registro.Entradas.Should().AllSatisfy(e => e.Texto.Should().NotContain(DireccionPersonal));
        entrada.Texto.Should().Contain("correlación ");
    }

    [Fact]
    public async Task Un_rechazo_smtp_registra_su_codigo_de_estado_y_de_error()
    {
        var registro = new RegistroConExcepciones();
        var cliente = new ClienteSmtpQueFalla { AlEnviar = CrearExcepcion("rechazo-smtp") };

        await CrearServicio(registro, cliente).EnviarAsync(DireccionPersonal, "Asunto", "<p>Cuerpo</p>", TipoAvisoCorreo.Informativo);

        var entrada = registro.Entradas.Should().ContainSingle(e => e.Nivel == LogLevel.Error).Subject;
        entrada.Texto.Should().Contain(nameof(SmtpStatusCode.MailboxUnavailable))
            .And.Contain(nameof(SmtpErrorCode.RecipientNotAccepted));
    }

    [Fact]
    public async Task Un_fallo_al_cerrar_tras_enviar_se_avisa_sin_la_direccion()
    {
        var registro = new RegistroConExcepciones();
        var cliente = new ClienteSmtpQueFalla { AlDesconectar = CrearExcepcion("red") };

        var resultado = await CrearServicio(registro, cliente)
            .EnviarAsync(DireccionPersonal, "Asunto", "<p>Cuerpo</p>", TipoAvisoCorreo.Informativo);

        resultado.EsExitoso.Should().BeTrue("el servidor ya aceptó el mensaje");
        var aviso = registro.Entradas.Should().ContainSingle(e => e.Nivel == LogLevel.Warning).Subject;
        aviso.Excepcion.Should().BeNull();
        aviso.Texto.Should().NotContain(DireccionPersonal).And.Contain(nameof(IOException));
    }

    private static SmtpEmailService CrearServicio(RegistroConExcepciones registro, ClienteSmtpQueFalla cliente) =>
        new(Options.Create(new SmtpEmailOptions
        {
            Host = "smtp.example.invalid",
            Usuario = "usuario-de-prueba",
            Contrasena = "contrasena-de-prueba",
            BuzonRemitente = "remitente@example.com",
        }), registro)
        {
            FabricaCliente = () => cliente,
        };

    private static Exception CrearExcepcion(string fallo) => fallo switch
    {
        "rechazo-smtp" => new SmtpCommandException(
            SmtpErrorCode.RecipientNotAccepted, SmtpStatusCode.MailboxUnavailable,
            MailboxAddress.Parse(DireccionPersonal), $"5.1.1 <{DireccionPersonal}>: Recipient address rejected"),
        "protocolo" => new SmtpProtocolException($"Respuesta inesperada sobre {DireccionPersonal}"),
        "red" => new IOException($"Conexión cerrada al enviar a {DireccionPersonal}"),
        "credenciales" => new MailKit.Security.AuthenticationException($"535 Rechazado para {DireccionPersonal}"),
        "tiempo" => new TimeoutException($"Sin respuesta al enviar a {DireccionPersonal}"),
        _ => throw new ArgumentOutOfRangeException(nameof(fallo)),
    };

    private sealed class ClienteSmtpQueFalla : SmtpClient
    {
        public Exception? AlConectar { get; init; }

        public Exception? AlEnviar { get; set; }

        public Exception? AlDesconectar { get; init; }

        public override Task ConnectAsync(string host, int port = 0, SecureSocketOptions options = SecureSocketOptions.Auto, CancellationToken cancellationToken = default) =>
            AlConectar is null ? Task.CompletedTask : Task.FromException(AlConectar);

        public override Task AuthenticateAsync(System.Text.Encoding encoding, System.Net.ICredentials credentials, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public override Task<string> SendAsync(FormatOptions options, MimeMessage message, CancellationToken cancellationToken = default, ITransferProgress? progress = null) =>
            AlEnviar is null ? Task.FromResult("250 OK") : Task.FromException<string>(AlEnviar);

        public override Task DisconnectAsync(bool quit, CancellationToken cancellationToken = default) =>
            AlDesconectar is null ? Task.CompletedTask : Task.FromException(AlDesconectar);
    }

    private sealed class RegistroConExcepciones : ILogger<SmtpEmailService>
    {
        public List<(LogLevel Nivel, string Texto, Exception? Excepcion)> Entradas { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entradas.Add((logLevel, formatter(state, exception) + " " + exception, exception));
    }
}
