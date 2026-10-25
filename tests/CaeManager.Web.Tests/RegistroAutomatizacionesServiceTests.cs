using CaeManager.Application.Configuracion;
using System.Reflection;
using CaeManager.Domain.Common;
using CaeManager.Infrastructure.Alertas;
using CaeManager.Infrastructure.Coordinacion;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using CaeManager.Application.Common;
using CaeManager.Domain.Configuracion;
using CaeManager.Infrastructure.Configuracion;
using FluentAssertions;

namespace CaeManager.Web.Tests;

/// <summary>
/// Sin fila sembrada, un trabajo debe seguir activo por defecto — condición
/// explícita al cablear <c>IngestaWhatsApp</c> en el gate de
/// <c>IngestaWebhookWhatsAppHostedService</c> (salud de plataforma, A-06):
/// si el fallback hubiera sido "inactivo", el despliegue habría apagado la
/// ingesta de WhatsApp en producción en silencio, sin que nadie tocara nada.
/// </summary>
public class RegistroAutomatizacionesServiceTests
{
    [Fact]
    public async Task Un_trabajo_sin_fila_previa_sigue_activo_por_defecto()
    {
        var servicio = new RegistroAutomatizacionesService(new EstadoAutomatizacionRepositorioFalso(), new UnitOfWorkFalso());

        var activo = await servicio.EstaActivoAsync("ingesta-webhook-whatsapp", CancellationToken.None);

        activo.Should().BeTrue();
    }

    [Fact]
    public async Task Un_trabajo_explicitamente_desactivado_no_esta_activo()
    {
        var repositorio = new EstadoAutomatizacionRepositorioFalso();
        var estado = new EstadoAutomatizacion("ingesta-webhook-whatsapp");
        estado.CambiarActivo(false);
        repositorio.Agregar(estado);
        var servicio = new RegistroAutomatizacionesService(repositorio, new UnitOfWorkFalso());

        var activo = await servicio.EstaActivoAsync("ingesta-webhook-whatsapp", CancellationToken.None);

        activo.Should().BeFalse();
    }

    /// <summary>
    /// REC-126: antes de este cambio el panel de Automatizaciones solo podía
    /// mostrar "Fallida", nunca por qué — un fallo de envío, de sondeo o de
    /// destino quedaba indistinguible de cualquier otro.
    /// </summary>
    [Fact]
    public async Task Registrar_una_ejecucion_fallida_guarda_el_mensaje_de_error()
    {
        var repositorio = new EstadoAutomatizacionRepositorioFalso();
        var servicio = new RegistroAutomatizacionesService(repositorio, new UnitOfWorkFalso());

        await servicio.RegistrarEjecucionAsync(
            "barrido-retencion-datos", exitosa: false, CancellationToken.None,
            mensajeError: "La base de datos no respondió a tiempo.");

        var estado = await repositorio.ObtenerPorTrabajoAsync("barrido-retencion-datos", CancellationToken.None);
        estado!.UltimoResultadoExitoso.Should().BeFalse();
        estado.UltimoMensajeError.Should().Be("La base de datos no respondió a tiempo.");
    }

    [Fact]
    public async Task Registrar_una_ejecucion_exitosa_guarda_evaluados_y_afectados_y_limpia_el_error_anterior()
    {
        var repositorio = new EstadoAutomatizacionRepositorioFalso();
        var estado = new EstadoAutomatizacion("barrido-retencion-datos");
        estado.RegistrarEjecucion(DateTime.UtcNow.AddDays(-1), exitosa: false, mensajeError: "Fallo de la ejecución anterior.");
        repositorio.Agregar(estado);
        var servicio = new RegistroAutomatizacionesService(repositorio, new UnitOfWorkFalso());

        await servicio.RegistrarEjecucionAsync(
            "barrido-retencion-datos", exitosa: true, CancellationToken.None,
            elementosEvaluados: 12, elementosAfectados: 3);

        var actualizado = await repositorio.ObtenerPorTrabajoAsync("barrido-retencion-datos", CancellationToken.None);
        actualizado!.UltimoResultadoExitoso.Should().BeTrue();
        actualizado.UltimosElementosEvaluados.Should().Be(12);
        actualizado.UltimosElementosAfectados.Should().Be(3);
        actualizado.UltimoMensajeError.Should().BeNull("una ejecución exitosa no debe arrastrar el error de la anterior");
    }

    [Fact]
    public void Un_mensaje_de_error_mas_largo_que_el_limite_se_trunca()
    {
        var estado = new EstadoAutomatizacion("cualquier-trabajo");
        var mensajeLargo = new string('x', EstadoAutomatizacion.LongitudMaximaUltimoMensajeError + 50);

        estado.RegistrarEjecucion(DateTime.UtcNow, exitosa: false, mensajeError: mensajeLargo);

        estado.UltimoMensajeError!.Length.Should().Be(EstadoAutomatizacion.LongitudMaximaUltimoMensajeError);
    }

    [Theory]
    [InlineData("2026-07-01T21:30:00Z", "2026-07-01T22:30:00Z", 2)]
    [InlineData("2026-07-01T22:30:00Z", "2026-07-02T00:30:00Z", 1)]
    [InlineData("2026-01-01T22:30:00Z", "2026-01-01T23:30:00Z", 2)]
    public async Task Reiniciar_respeta_el_dia_de_Madrid_y_reanuda_al_dia_siguiente(
        string primerInstante, string siguienteInstante, int enviosEsperados)
    {
        var repositorio = new EstadoAutomatizacionRepositorioFalso();
        var registro = new RegistroAutomatizacionesService(repositorio, new UnitOfWorkFalso());
        using var servicios = new ServiceCollection()
            .AddSingleton<IEstadoAutomatizacionRepository>(repositorio)
            .AddSingleton<IRegistroAutomatizacionesService>(registro)
            .BuildServiceProvider();
        var tenantId = Guid.NewGuid();
        var reloj = new RelojFijo(DateTimeOffset.Parse(primerInstante));
        using var tiempo = DiaDeNegocio.FijarRelojEnEsteFlujo(reloj);

        var primero = new EnvioFalso(servicios.GetRequiredService<IServiceScopeFactory>());
        await ProcesarAsync(primero, tenantId);
        primero.Envios.Should().Be(1);
        // El registro usa UTC real: fijamos el instante persistido de la ejecución simulada.
        var estado = await repositorio.ObtenerPorTrabajoAsync(CatalogoAutomatizaciones.AlertasVencimientoDiarias);
        estado!.RegistrarEjecucion(reloj.GetUtcNow().UtcDateTime, true);

        var segundo = new EnvioFalso(servicios.GetRequiredService<IServiceScopeFactory>());
        await ProcesarAsync(segundo, tenantId);
        segundo.Envios.Should().Be(0, "un segundo arranque del mismo día no repite el envío");

        reloj.Instante = DateTimeOffset.Parse(siguienteInstante);
        var siguiente = new EnvioFalso(servicios.GetRequiredService<IServiceScopeFactory>());
        await ProcesarAsync(siguiente, tenantId);
        (primero.Envios + segundo.Envios + siguiente.Envios).Should().Be(enviosEsperados);
    }

    [Fact]
    public async Task Un_envio_fallido_del_mismo_dia_permite_reintentar()
    {
        var repositorio = new EstadoAutomatizacionRepositorioFalso();
        var estado = new EstadoAutomatizacion(CatalogoAutomatizaciones.AlertasVencimientoDiarias);
        estado.RegistrarEjecucion(DateTime.UtcNow, false);
        repositorio.Agregar(estado);
        using var servicios = new ServiceCollection()
            .AddSingleton<IEstadoAutomatizacionRepository>(repositorio)
            .AddSingleton<IRegistroAutomatizacionesService>(new RegistroAutomatizacionesService(repositorio, new UnitOfWorkFalso()))
            .BuildServiceProvider();
        var servicio = new EnvioFalso(servicios.GetRequiredService<IServiceScopeFactory>());

        await ProcesarAsync(servicio, Guid.NewGuid());

        servicio.Envios.Should().Be(1);
        estado.UltimoResultadoExitoso.Should().BeTrue();
    }

    private static Task ProcesarAsync(EnvioAlertasVencimientoHostedService servicio, Guid tenantId) =>
        (Task)typeof(EnvioAlertasVencimientoHostedService)
            .GetMethod("ProcesarTenantAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(servicio, [tenantId, CancellationToken.None])!;

    private sealed class RelojFijo(DateTimeOffset instante) : TimeProvider
    {
        public DateTimeOffset Instante { get; set; } = instante;
        public override DateTimeOffset GetUtcNow() => Instante;
    }

    // Doble de la frontera de envío: jamás resuelve IEmailService ni abre conexiones.
    private sealed class EnvioFalso(IServiceScopeFactory ambitos) : EnvioAlertasVencimientoHostedService(
        ambitos, new LiderFalso(), Options.Create(new AlertasPorCorreoOptions()),
        NullLogger<EnvioAlertasVencimientoHostedService>.Instance)
    {
        public int Envios { get; private set; }

        protected override Task<(string? MensajeError, int AlertasEvaluadas)> EjecutarEnvioAsync(
            IServiceScope ambito, Guid tenantId, CancellationToken stoppingToken)
        {
            Envios++;
            return Task.FromResult<(string?, int)>((null, 1));
        }
    }

    private sealed class LiderFalso : IEleccionLiderService
    {
        public async Task<bool> IntentarEjecutarComoLiderAsync(string nombre, Func<CancellationToken, Task> accion, CancellationToken cancellationToken)
        {
            await accion(cancellationToken);
            return true;
        }
    }

    private sealed class EstadoAutomatizacionRepositorioFalso : IEstadoAutomatizacionRepository
    {
        private readonly List<EstadoAutomatizacion> _estados = [];

        public void Agregar(EstadoAutomatizacion estado) => _estados.Add(estado);

        public Task<EstadoAutomatizacion?> ObtenerPorTrabajoAsync(string trabajoId, CancellationToken cancellationToken = default) =>
            Task.FromResult(_estados.FirstOrDefault(e => e.TrabajoId == trabajoId));

        public Task<IReadOnlyList<EstadoAutomatizacion>> ObtenerTodosAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult((IReadOnlyList<EstadoAutomatizacion>)_estados);
    }

    private sealed class UnitOfWorkFalso : IUnitOfWork
    {
        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) => Task.FromResult(0);
    }
}
