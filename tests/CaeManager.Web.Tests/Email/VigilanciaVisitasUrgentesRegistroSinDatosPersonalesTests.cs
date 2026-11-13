using CaeManager.Infrastructure.Configuracion;
using CaeManager.Infrastructure.Coordinacion;
using CaeManager.Infrastructure.Visitas;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CaeManager.Web.Tests.Email;

/// <summary>
/// ASVS 4.0.3 V7.1.1: el registro de la vigilancia de gestiones urgentes de visita no lleva datos
/// personales. Una excepción de acceso a datos o de composición del aviso puede citar en su mensaje (o en el
/// de su causa) el correo de una cuenta; al registro de la aplicación solo va el tipo. El mensaje completo
/// sigue yendo al estado de la automatización (base de datos, visible al propio Tenant), que no cambia.
/// Cada prueba ejercita uno de los tres puntos de registro reales del servicio.
/// </summary>
public class VigilanciaVisitasUrgentesRegistroSinDatosPersonalesTests
{
    private const string DireccionPersonal = "persona.rechazada@example.com";
    private static readonly Guid TenantId = Guid.NewGuid();

    private static IOException ExcepcionConDireccion() =>
        new($"Conexión cerrada al leer la cuenta {DireccionPersonal}",
            new TimeoutException($"Sin respuesta de {DireccionPersonal}"));

    [Fact]
    public async Task Un_fallo_de_un_tenant_dentro_de_su_ejecucion_se_registra_solo_con_su_tipo()
    {
        var registro = new RegistroQueCapturaTodo<VigilanciaVisitasUrgentesHostedService>();
        var automatizaciones = new RegistroAutomatizacionesFalso();
        using var ambitos = Ambitos(automatizaciones);
        var servicio = new VigilanciaConRecuentoQueLanza(ambitos, registro, ExcepcionConDireccion());

        await servicio.ProcesarTenantsAsync([TenantId], CancellationToken.None);

        registro.DebeEstarLimpioDeDatosPersonales(DireccionPersonal, "Conexión cerrada", "Sin respuesta");
        registro.Entradas.Should().ContainSingle(e => e.Nivel == LogLevel.Error).Which.Contenido
            .Should().Contain(nameof(IOException)).And.Contain(TenantId.ToString());
        automatizaciones.MensajesDeError.Should().ContainSingle().Which.Should().Contain(DireccionPersonal,
            "el estado de la automatización, no el registro de la aplicación, conserva el mensaje completo");
    }

    [Fact]
    public async Task Un_fallo_de_un_tenant_fuera_de_su_ejecucion_se_registra_solo_con_su_tipo()
    {
        var registro = new RegistroQueCapturaTodo<VigilanciaVisitasUrgentesHostedService>();
        using var ambitos = Ambitos(new RegistroAutomatizacionesFalso { LanzaAlConsultarInterruptor = ExcepcionConDireccion() });
        var servicio = new VigilanciaConRecuentoQueLanza(ambitos, registro, new InvalidOperationException("sin uso"));

        await servicio.ProcesarTenantsAsync([TenantId], CancellationToken.None);

        registro.DebeEstarLimpioDeDatosPersonales(DireccionPersonal, "Conexión cerrada", "Sin respuesta");
        registro.Entradas.Should().ContainSingle(e => e.Nivel == LogLevel.Error).Which.Contenido
            .Should().Contain(nameof(IOException)).And.Contain(TenantId.ToString());
    }

    [Fact]
    public async Task Un_fallo_del_ciclo_entero_se_registra_solo_con_su_tipo()
    {
        var registro = new RegistroQueCapturaTodo<VigilanciaVisitasUrgentesHostedService>();
        using var ambitos = Ambitos(new RegistroAutomatizacionesFalso());
        var servicio = new VigilanciaConLiderQueLanza(ambitos, registro, ExcepcionConDireccion());

        // Bucle real del servicio: el catch que registra el fallo es el suyo, no un auxiliar.
        await servicio.StartAsync(CancellationToken.None);
        await EsperarAsync(() => registro.Entradas.Any(e => e.Nivel == LogLevel.Error));
        await servicio.StopAsync(CancellationToken.None);

        registro.DebeEstarLimpioDeDatosPersonales(DireccionPersonal, "Conexión cerrada", "Sin respuesta");
        registro.Entradas.Should().ContainSingle(e => e.Nivel == LogLevel.Error).Which.Contenido
            .Should().Contain(nameof(IOException));
    }

    private static async Task EsperarAsync(Func<bool> condicion)
    {
        using var limite = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (!condicion())
            await Task.Delay(20, limite.Token);
    }

    private static ServiceProvider Ambitos(RegistroAutomatizacionesFalso automatizaciones) => new ServiceCollection()
        .AddScoped<IRegistroAutomatizacionesService>(_ => automatizaciones)
        .BuildServiceProvider();

    private sealed class RegistroAutomatizacionesFalso : IRegistroAutomatizacionesService
    {
        public Exception? LanzaAlConsultarInterruptor { get; init; }
        public List<string?> MensajesDeError { get; } = [];

        public Task<bool> EstaActivoAsync(string trabajoId, CancellationToken cancellationToken) =>
            LanzaAlConsultarInterruptor is { } excepcion ? Task.FromException<bool>(excepcion) : Task.FromResult(true);

        public Task RegistrarEjecucionAsync(
            string trabajoId, bool exitosa, CancellationToken cancellationToken,
            string? mensajeError = null, int? elementosEvaluados = null, int? elementosAfectados = null)
        {
            if (!exitosa) MensajesDeError.Add(mensajeError);
            return Task.CompletedTask;
        }
    }

    /// <summary>Sustituye el recuento por uno que lanza: así el servicio no llega a tocar la base.</summary>
    private sealed class VigilanciaConRecuentoQueLanza(
        IServiceProvider servicios, RegistroQueCapturaTodo<VigilanciaVisitasUrgentesHostedService> registro, Exception excepcion)
        : VigilanciaVisitasUrgentesHostedService(servicios.GetRequiredService<IServiceScopeFactory>(), new LiderSinUso(), registro)
    {
        protected override Task<int> ContarGestionesUrgentesAsync(IServiceScope ambito, CancellationToken stoppingToken) =>
            Task.FromException<int>(excepcion);

        protected override Task SincronizarAvisosAsync(IServiceScope ambito, int gestiones, CancellationToken stoppingToken) =>
            Task.CompletedTask;
    }

    private sealed class VigilanciaConLiderQueLanza(
        IServiceProvider servicios, RegistroQueCapturaTodo<VigilanciaVisitasUrgentesHostedService> registro, Exception excepcion)
        : VigilanciaVisitasUrgentesHostedService(
            servicios.GetRequiredService<IServiceScopeFactory>(), new LiderQueLanza(excepcion), registro);

    private sealed class LiderSinUso : IEleccionLiderService
    {
        public Task<bool> IntentarEjecutarComoLiderAsync(string nombre, Func<CancellationToken, Task> accion, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Estos tests no pasan por la elección de líder.");
    }

    private sealed class LiderQueLanza(Exception excepcion) : IEleccionLiderService
    {
        public Task<bool> IntentarEjecutarComoLiderAsync(string nombre, Func<CancellationToken, Task> accion, CancellationToken cancellationToken) =>
            Task.FromException<bool>(excepcion);
    }
}
