using CaeManager.Application.AltaAutonoma;
using CaeManager.Infrastructure.AltaAutonoma;
using CaeManager.Infrastructure.Coordinacion;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CaeManager.Web.Tests.AltaAutonoma;

/// <summary>
/// El servicio de fondo de la limpieza de Solicitudes de alta (A8): apagado por configuración, una pasada inmediata
/// bajo la elección de líder, sin que un fallo lo tumbe, e intervalo con suelo de un minuto. Las funciones SQL y su
/// efecto sobre la base los mide la suite de integración.
/// </summary>
public class LimpiezaSolicitudesAltaHostedServiceTests
{
    private static readonly TimeSpan Espera = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task Apagado_por_configuracion_no_pide_el_liderazgo_ni_toca_nada()
    {
        var liderazgo = new LiderFalso();
        var limpieza = new LimpiezaFalsa();
        using var servicio = Crear(new LimpiezaSolicitudesAltaOptions { Activa = false }, liderazgo, limpieza);

        await servicio.StartAsync(CancellationToken.None);
        await servicio.ExecuteTask!.WaitAsync(Espera);
        await servicio.StopAsync(CancellationToken.None);

        liderazgo.Claves.Should().BeEmpty();
        limpieza.Llamadas.Should().Be(0);
    }

    [Fact]
    public async Task Activo_hace_una_pasada_al_arrancar_como_lider_con_su_clave()
    {
        var liderazgo = new LiderFalso();
        var limpieza = new LimpiezaFalsa();
        using var servicio = Crear(new LimpiezaSolicitudesAltaOptions(), liderazgo, limpieza);

        await servicio.StartAsync(CancellationToken.None);
        await limpieza.PrimeraLlamada.Task.WaitAsync(Espera);
        await servicio.StopAsync(CancellationToken.None);

        liderazgo.Claves.Should().ContainSingle().Which.Should().Be("limpieza-solicitudes-alta");
        limpieza.Llamadas.Should().BeGreaterThanOrEqualTo(2, "caducar y borrar, al menos una llamada cada una");
    }

    [Fact]
    public async Task Un_fallo_de_la_pasada_se_registra_y_no_tumba_el_servicio()
    {
        var liderazgo = new LiderFalso { Falla = true };
        var limpieza = new LimpiezaFalsa();
        using var servicio = Crear(new LimpiezaSolicitudesAltaOptions(), liderazgo, limpieza);

        await servicio.StartAsync(CancellationToken.None);
        await liderazgo.Llamado.Task.WaitAsync(Espera);
        servicio.ExecuteTask!.IsFaulted.Should().BeFalse("el servicio sigue vivo esperando a la próxima pasada");
        await servicio.StopAsync(CancellationToken.None);
    }

    [Theory]
    [InlineData(0, 60)]
    [InlineData(10, 60)]
    [InlineData(3600, 3600)]
    public void El_intervalo_tiene_un_suelo_de_un_minuto(int segundos, int esperados)
    {
        var efectivo = LimpiezaSolicitudesAltaHostedService.IntervaloEfectivo(
            new LimpiezaSolicitudesAltaOptions { Intervalo = TimeSpan.FromSeconds(segundos) });

        efectivo.Should().Be(TimeSpan.FromSeconds(esperados));
    }

    private static LimpiezaSolicitudesAltaHostedService Crear(
        LimpiezaSolicitudesAltaOptions opciones, LiderFalso liderazgo, LimpiezaFalsa limpieza)
    {
        var servicios = new ServiceCollection();
        servicios.AddSingleton<ILimpiezaSolicitudesAlta>(limpieza);
        servicios.AddSingleton(Options.Create(opciones));
        servicios.AddLogging();
        servicios.AddScoped<ServicioLimpiezaSolicitudesAlta>();
        var proveedor = servicios.BuildServiceProvider();

        return new LimpiezaSolicitudesAltaHostedService(
            proveedor.GetRequiredService<IServiceScopeFactory>(), liderazgo, Options.Create(opciones),
            NullLogger<LimpiezaSolicitudesAltaHostedService>.Instance);
    }

    private sealed class LiderFalso : IEleccionLiderService
    {
        public List<string> Claves { get; } = [];
        public bool Falla { get; init; }
        public TaskCompletionSource Llamado { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<bool> IntentarEjecutarComoLiderAsync(
            string clave, Func<CancellationToken, Task> trabajo, CancellationToken cancellationToken)
        {
            Claves.Add(clave);
            Llamado.TrySetResult();
            if (Falla)
                throw new InvalidOperationException("Falla la elección de líder.");

            await trabajo(cancellationToken);
            return true;
        }
    }

    private sealed class LimpiezaFalsa : ILimpiezaSolicitudesAlta
    {
        private int _llamadas;

        public int Llamadas => Volatile.Read(ref _llamadas);
        public TaskCompletionSource PrimeraLlamada { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<int> CaducarVencidasAsync(int tamanoLote, CancellationToken cancellationToken) => Contar();

        public Task<int> BorrarDatosPersonalesAsync(int tamanoLote, CancellationToken cancellationToken) => Contar();

        private Task<int> Contar()
        {
            if (Interlocked.Increment(ref _llamadas) >= 2)
                PrimeraLlamada.TrySetResult();
            return Task.FromResult(0);
        }
    }
}
