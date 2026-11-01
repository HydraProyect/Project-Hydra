using CaeManager.Application.Common;
using CaeManager.Infrastructure.Autorizacion;
using CaeManager.Infrastructure.Configuracion;
using CaeManager.Infrastructure.Coordinacion;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.Visitas;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CaeManager.Web.Tests;

/// <summary>
/// C-02, defecto adjunto: el bucle no aislaba Tenants y el fallo de uno, relanzado, dejaba sin ciclo a
/// los siguientes. La parte de datos, con <c>cae_app_runtime</c>, está en
/// <c>VigilanciaVisitasUrgentesBajoRlsTests</c>.
/// </summary>
public class VigilanciaVisitasUrgentesHostedServiceTests
{
    private static readonly Guid TenantQueFalla = Guid.NewGuid();
    private static readonly Guid TenantSiguiente = Guid.NewGuid();

    /// <summary>
    /// El aviso cuenta el Tenant entero. Quien llega por su rol de origen en otra organización y aquí
    /// ejerce otro no lo recibe; quien de verdad lo ejerce, propio o delegado, sí.
    /// </summary>
    [Fact]
    public void Un_delegado_solo_recibe_el_aviso_si_ejerce_aqui_el_rol()
    {
        var propio = Cuenta();
        var administradorDelegado = Cuenta();
        var direccionDelegada = Cuenta();
        var delegadoSinOperacionEntera = Cuenta();
        var soporte = Cuenta();
        var direccion = Cuenta();

        var destinatarios = VigilanciaVisitasUrgentesHostedService.DestinatariosDelAviso(
            [propio, administradorDelegado, delegadoSinOperacionEntera, soporte],
            [direccion, direccionDelegada, propio],
            new Dictionary<Guid, string>
            {
                [administradorDelegado.Id] = Roles.Administrador,
                [direccionDelegada.Id] = Roles.DireccionCae,
                [delegadoSinOperacionEntera.Id] = "GestorCae",
                [soporte.Id] = "Consulta",
            });

        destinatarios.Should().BeEquivalentTo([propio, administradorDelegado, direccion, direccionDelegada]);
    }

    private static CuentaVisibleEnRol Cuenta() => new(Guid.NewGuid(), "Cuenta", null, null);

    [Fact]
    public async Task El_fallo_de_un_Tenant_se_registra_y_no_deja_sin_ciclo_al_siguiente()
    {
        var registros = new Registros();
        using var servicios = Servicios(registros);
        var servicio = new VigilanciaFalsa(servicios, tenant => tenant == TenantQueFalla
            ? throw new InvalidOperationException("Sin parámetros del sistema.")
            : 0);

        await servicio.ProcesarTenantsAsync([TenantQueFalla, TenantSiguiente], CancellationToken.None);

        servicio.Consultados.Should().Equal(TenantQueFalla, TenantSiguiente);
        registros.Ejecuciones.Should().ContainSingle(e => e.TenantId == TenantQueFalla)
            .Which.Should().Match<Ejecucion>(e => !e.Exitosa && e.MensajeError == "Sin parámetros del sistema.");
        registros.Ejecuciones.Should().ContainSingle(e => e.TenantId == TenantSiguiente)
            .Which.Should().Match<Ejecucion>(e => e.Exitosa && e.Evaluados == 0);
    }

    /// <summary>
    /// El contexto del ámbito que falla conserva lo que no pudo guardar: registrar con él reintentaría ese
    /// guardado, fallaría otra vez y el panel de Automatizaciones se quedaría sin el fallo real.
    /// </summary>
    [Fact]
    public async Task El_fallo_se_registra_en_un_ambito_nuevo_y_no_en_el_que_ha_fallado()
    {
        var registros = new Registros();
        using var servicios = Servicios(registros);
        var servicio = new VigilanciaFalsa(servicios, _ => throw new InvalidOperationException("Fallo al guardar."));

        await servicio.ProcesarTenantsAsync([TenantQueFalla], CancellationToken.None);

        var ejecucion = registros.Ejecuciones.Should().ContainSingle().Subject;
        ejecucion.Exitosa.Should().BeFalse();
        ejecucion.Instancia.Should().NotBe(registros.PrimeraInstancia, "el registro del interruptor es el del ámbito que falló");
    }

    /// <summary>
    /// Relanzar tras registrar no se nota en las ejecuciones, porque el bucle lo absorbe: se nota en
    /// que el mismo fallo se contaría dos veces en el log.
    /// </summary>
    [Fact]
    public async Task Un_fallo_ya_registrado_no_se_relanza()
    {
        var registros = new Registros();
        var log = new LogDeErrores();
        using var servicios = Servicios(registros);
        var servicio = new VigilanciaFalsa(servicios, _ => throw new InvalidOperationException("Sin conexión."), log);

        await servicio.ProcesarTenantsAsync([TenantQueFalla], CancellationToken.None);

        log.Errores.Should().Be(1);
    }

    [Fact]
    public async Task Que_falle_el_propio_registro_del_fallo_tampoco_deja_sin_ciclo_al_siguiente()
    {
        var registros = new Registros { FallarAlRegistrarFallo = true };
        using var servicios = Servicios(registros);
        var servicio = new VigilanciaFalsa(servicios, tenant => tenant == TenantQueFalla
            ? throw new InvalidOperationException("Sin conexión.")
            : 0);

        await servicio.ProcesarTenantsAsync([TenantQueFalla, TenantSiguiente], CancellationToken.None);

        registros.Ejecuciones.Should().ContainSingle(e => e.TenantId == TenantSiguiente && e.Exitosa);
    }

    [Fact]
    public async Task Una_parada_del_proceso_corta_el_ciclo_sin_registrarse_como_fallo()
    {
        using var parada = new CancellationTokenSource();
        var registros = new Registros();
        using var servicios = Servicios(registros);
        var servicio = new VigilanciaFalsa(servicios, _ =>
        {
            parada.Cancel();
            parada.Token.ThrowIfCancellationRequested();
            return 0;
        });

        var ciclo = () => servicio.ProcesarTenantsAsync([TenantQueFalla, TenantSiguiente], parada.Token);

        await ciclo.Should().ThrowAsync<OperationCanceledException>();
        servicio.Consultados.Should().Equal(TenantQueFalla);
        registros.Ejecuciones.Should().BeEmpty();
    }

    private static ServiceProvider Servicios(Registros registros) => new ServiceCollection()
        .AddScoped<IRegistroAutomatizacionesService>(_ => new RegistroFalso(registros))
        .BuildServiceProvider();

    private sealed record Ejecucion(Guid? TenantId, bool Exitosa, string? MensajeError, int? Evaluados, RegistroFalso Instancia);

    private sealed class Registros
    {
        public bool FallarAlRegistrarFallo { get; init; }
        public RegistroFalso? PrimeraInstancia { get; set; }
        public List<Ejecucion> Ejecuciones { get; } = [];
    }

    private sealed class RegistroFalso : IRegistroAutomatizacionesService
    {
        private readonly Registros _registros;

        public RegistroFalso(Registros registros)
        {
            _registros = registros;
            _registros.PrimeraInstancia ??= this;
        }

        public Task<bool> EstaActivoAsync(string trabajoId, CancellationToken cancellationToken) => Task.FromResult(true);

        public Task RegistrarEjecucionAsync(
            string trabajoId, bool exitosa, CancellationToken cancellationToken,
            string? mensajeError = null, int? elementosEvaluados = null, int? elementosAfectados = null)
        {
            if (!exitosa && _registros.FallarAlRegistrarFallo)
                throw new InvalidOperationException("Tampoco se pudo registrar.");

            _registros.Ejecuciones.Add(new Ejecucion(AmbitoTenantExplicito.TenantIdActual, exitosa, mensajeError, elementosEvaluados, this));
            return Task.CompletedTask;
        }
    }

    private sealed class LogDeErrores : ILogger<VigilanciaVisitasUrgentesHostedService>
    {
        public int Errores { get; private set; }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Error) Errores++;
        }
    }

    /// <summary>Sustituye solo el recuento: con cero gestiones el servicio no llega a tocar la base.</summary>
    private sealed class VigilanciaFalsa(
        IServiceProvider servicios, Func<Guid, int> contar, ILogger<VigilanciaVisitasUrgentesHostedService>? log = null)
        : VigilanciaVisitasUrgentesHostedService(
            servicios.GetRequiredService<IServiceScopeFactory>(), new LiderSinUso(),
            log ?? NullLogger<VigilanciaVisitasUrgentesHostedService>.Instance)
    {
        public List<Guid> Consultados { get; } = [];

        protected override Task<int> ContarGestionesUrgentesAsync(IServiceScope ambito, CancellationToken stoppingToken)
        {
            var tenant = AmbitoTenantExplicito.TenantIdActual!.Value;
            Consultados.Add(tenant);
            return Task.FromResult(contar(tenant));
        }

        private sealed class LiderSinUso : IEleccionLiderService
        {
            public Task<bool> IntentarEjecutarComoLiderAsync(string nombre, Func<CancellationToken, Task> accion, CancellationToken cancellationToken) =>
                throw new InvalidOperationException("Estos tests no pasan por la elección de líder.");
        }
    }
}
