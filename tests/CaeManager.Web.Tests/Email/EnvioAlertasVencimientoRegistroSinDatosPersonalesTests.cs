using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Infrastructure.Alertas;
using CaeManager.Infrastructure.Autorizacion;
using CaeManager.Infrastructure.Configuracion;
using CaeManager.Infrastructure.Coordinacion;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace CaeManager.Web.Tests.Email;

/// <summary>
/// ASVS 4.0.3 V7.1.1: el registro del envío del resumen de alertas de vencimiento no lleva datos
/// personales. Un <c>IEmailService</c> que falla compone su error con la dirección, y una excepción
/// de envío cita la dirección en su mensaje; ninguna de las dos cosas puede llegar al registro.
/// Cada prueba ejercita el punto de registro real del servicio con un registro que captura el
/// texto formateado, los valores estructurados y la excepción de cada entrada.
/// </summary>
public class EnvioAlertasVencimientoRegistroSinDatosPersonalesTests
{
    private const string DireccionPersonal = "persona.rechazada@example.com";
    private static readonly Guid TenantId = Guid.NewGuid();

    [Fact]
    public async Task Un_envio_fallido_se_registra_sin_la_direccion_ni_el_mensaje_del_error()
    {
        var registro = new RegistroCapturador();
        var correo = new CorreoQueFalla(Error.Crear("Email.ErrorRed", $"Rechazado para {DireccionPersonal}: buzón inexistente"));
        var servicio = CrearServicio(registro);

        var mensajeError = await servicio.EnviarResumenAsync(
            correo, [new CuentaVisibleEnRol(Guid.NewGuid(), "Marta Gómez", DireccionPersonal, null)],
            "Asunto", "<p>Cuerpo</p>", TenantId, CancellationToken.None);

        var aviso = registro.Entradas.Should().ContainSingle(e => e.Nivel == LogLevel.Warning).Subject;
        aviso.Contenido.Should().NotContain(DireccionPersonal).And.NotContain("buzón inexistente");
        aviso.Contenido.Should().Contain("Email.ErrorRed", "el código basta para clasificar el fallo")
            .And.Contain(TenantId.ToString());
        mensajeError.Should().Contain(DireccionPersonal,
            "el estado de la automatización, no el registro, sigue diciendo qué destinatario falló");
    }

    [Fact]
    public async Task Una_excepcion_de_un_tenant_se_registra_solo_con_su_tipo()
    {
        var registro = new RegistroCapturador();
        var excepcion = new IOException($"Conexión cerrada al enviar a {DireccionPersonal}",
            new TimeoutException($"Sin respuesta de {DireccionPersonal}"));
        using var ambitos = new ServiceCollection()
            .AddSingleton<IRegistroAutomatizacionesService>(new RegistroQueLanza(excepcion))
            .BuildServiceProvider();
        var servicio = new ServicioConUnTenant(ambitos.GetRequiredService<IServiceScopeFactory>(), registro, TenantId);

        // Bucle real por Tenant: el catch que registra el fallo es el del servicio, no un auxiliar.
        await servicio.StartAsync(CancellationToken.None);
        await EsperarAsync(() => registro.Entradas.Any(e => e.Nivel == LogLevel.Error));
        await servicio.StopAsync(CancellationToken.None);

        var entrada = registro.Entradas.Should().ContainSingle(e => e.Nivel == LogLevel.Error).Subject;
        entrada.Excepcion.Should().BeNull("la excepción entera lleva su mensaje y el de su causa");
        entrada.Contenido.Should().NotContain(DireccionPersonal)
            .And.Contain(nameof(IOException)).And.Contain(TenantId.ToString());
    }

    [Fact]
    public async Task Una_excepcion_del_ciclo_se_registra_solo_con_su_tipo()
    {
        var registro = new RegistroCapturador();
        var servicio = CrearServicio(registro, new LiderQueLanza(new IOException($"Conexión cerrada al enviar a {DireccionPersonal}")));

        await servicio.StartAsync(CancellationToken.None);
        await EsperarAsync(() => registro.Entradas.Any(e => e.Nivel == LogLevel.Error));
        await servicio.StopAsync(CancellationToken.None);

        var entrada = registro.Entradas.Should().ContainSingle(e => e.Nivel == LogLevel.Error).Subject;
        entrada.Excepcion.Should().BeNull();
        entrada.Contenido.Should().NotContain(DireccionPersonal).And.Contain(nameof(IOException));
    }

    private static async Task EsperarAsync(Func<bool> condicion)
    {
        using var limite = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (!condicion())
            await Task.Delay(20, limite.Token);
    }

    private static EnvioAlertasVencimientoHostedService CrearServicio(RegistroCapturador registro, IEleccionLiderService? lider = null) =>
        new(new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            lider ?? new LiderQueLanza(new InvalidOperationException("sin uso")),
            Options.Create(new AlertasPorCorreoOptions()), registro);

    private sealed class CorreoQueFalla(Error error) : IEmailService
    {
        public Task<Result> EnviarAsync(
            string destinatarioEmail, string asunto, string cuerpoHtml, TipoAvisoCorreo tipo,
            string? responderA = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Fallo(error));
    }

    private sealed class ServicioConUnTenant(IServiceScopeFactory ambitos, RegistroCapturador registro, Guid tenantId)
        : EnvioAlertasVencimientoHostedService(ambitos, new LiderQueEjecuta(), Options.Create(new AlertasPorCorreoOptions()), registro)
    {
        protected override Task<List<Guid>> ObtenerTenantsActivosAsync(CancellationToken stoppingToken) =>
            Task.FromResult(new List<Guid> { tenantId });
    }

    private sealed class LiderQueEjecuta : IEleccionLiderService
    {
        public async Task<bool> IntentarEjecutarComoLiderAsync(
            string clave, Func<CancellationToken, Task> trabajo, CancellationToken cancellationToken)
        {
            await trabajo(cancellationToken);
            return true;
        }
    }

    private sealed class RegistroQueLanza(Exception excepcion) : IRegistroAutomatizacionesService
    {
        public Task<bool> EstaActivoAsync(string trabajoId, CancellationToken cancellationToken) =>
            Task.FromException<bool>(excepcion);

        public Task RegistrarEjecucionAsync(
            string trabajoId, bool exitosa, CancellationToken cancellationToken,
            string? mensajeError = null, int? elementosEvaluados = null, int? elementosAfectados = null) =>
            Task.CompletedTask;
    }

    private sealed class LiderQueLanza(Exception excepcion) : IEleccionLiderService
    {
        public Task<bool> IntentarEjecutarComoLiderAsync(
            string clave, Func<CancellationToken, Task> trabajo, CancellationToken cancellationToken) =>
            Task.FromException<bool>(excepcion);
    }

    private sealed class RegistroCapturador : ILogger<EnvioAlertasVencimientoHostedService>
    {
        private readonly object _cerrojo = new();
        private readonly List<(LogLevel Nivel, string Contenido, Exception? Excepcion)> _entradas = [];

        public IReadOnlyList<(LogLevel Nivel, string Contenido, Exception? Excepcion)> Entradas
        {
            get { lock (_cerrojo) return _entradas.ToList(); }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            // Texto formateado + valores estructurados (lo que un proveedor JSON escribiría) + excepción entera.
            var estructurados = state is IEnumerable<KeyValuePair<string, object?>> pares
                ? string.Join(" ", pares.Select(p => $"{p.Key}={p.Value}"))
                : string.Empty;
            lock (_cerrojo)
                _entradas.Add((logLevel, formatter(state, exception) + " " + estructurados + " " + exception, exception));
        }
    }
}
