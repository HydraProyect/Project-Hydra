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
