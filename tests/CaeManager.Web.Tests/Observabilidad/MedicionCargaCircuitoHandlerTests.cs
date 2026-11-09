using CaeManager.Application.Common;
using CaeManager.Web.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Endpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CaeManager.Web.Tests.Observabilidad;

/// <summary>
/// El registro por carga de página del circuito interactivo: una entrada por carga con
/// el coste de esa carga (no el acumulado del circuito) y la plantilla de la ruta, nunca
/// la ruta concreta ni sus ids.
/// </summary>
public class MedicionCargaCircuitoHandlerTests
{
    [Fact]
    public void La_plantilla_sustituye_a_la_ruta_concreta_y_gana_la_mas_especifica()
    {
        var plantillas = Plantillas("/", "/empresas", "/empresas/nueva", "/empresas/{Id:guid}");

        plantillas.Resolver($"/empresas/{Guid.NewGuid()}").Should().Be("/empresas/{Id:guid}");
        plantillas.Resolver("/empresas/nueva").Should().Be("/empresas/nueva");
        plantillas.Resolver("/empresas").Should().Be("/empresas");
        plantillas.Resolver("empresas").Should().Be("/empresas");
        plantillas.Resolver("/no/existe/123").Should().Be(PlantillasDeRutaDePagina.SinPlantilla);
    }

    [Fact]
    public void Solo_cuentan_las_rutas_de_componentes()
    {
        var plantillas = new PlantillasDeRutaDePagina(new DefaultEndpointDataSource(
            Endpoint("/api/v1/empresas/{id}", componente: false)));

        plantillas.Resolver("/api/v1/empresas/7").Should().Be(PlantillasDeRutaDePagina.SinPlantilla);
    }

    [Fact]
    public async Task Cada_carga_registra_solo_su_coste_y_su_plantilla()
    {
        var (handler, coste, navegacion, logger, reloj) = Crear(habilitado: true, uriInicial: "http://localhost/empresas");

        await handler.OnCircuitOpenedAsync(null!, default);
        coste.RegistrarComando(TimeSpan.FromMilliseconds(10));
        coste.RegistrarComando(TimeSpan.FromMilliseconds(5));
        coste.RegistrarConexion();
        coste.RegistrarDespacho();

        var id = Guid.NewGuid();
        navegacion.Navegar($"http://localhost/empresas/{id}?pestana=documentos#x");
        coste.RegistrarComando(TimeSpan.FromMilliseconds(2));
        await handler.OnCircuitClosedAsync(null!, default);

        logger.Entradas.Should().HaveCount(2);
        var primera = logger.Entradas[0];
        primera.Nivel.Should().Be(LogLevel.Information);
        primera.Valor("Plantilla").Should().Be("/empresas");
        primera.Valor("Comandos").Should().Be(2L);
        primera.Valor("Conexiones").Should().Be(1L);
        primera.Valor("Despachos").Should().Be(1L);
        primera.Valor("BaseDatosMs").Should().Be(15.0);
        primera.Valor("Motivo").Should().Be(MedicionCargaCircuitoHandler.MotivoNavegacion);

        var segunda = logger.Entradas[1];
        segunda.Valor("Plantilla").Should().Be("/empresas/{Id:guid}");
        segunda.Valor("Comandos").Should().Be(1L);
        segunda.Valor("Motivo").Should().Be(MedicionCargaCircuitoHandler.MotivoCircuitoCerrado);

        logger.Entradas.Should().AllSatisfy(e =>
        {
            e.Mensaje.Should().NotContain(id.ToString()).And.NotContain("pestana");
            e.Valores.Select(v => v.Value?.ToString() ?? "").Should().NotContain(v => v.Contains(id.ToString()));
        });
        reloj.Temporizadores.Should().OnlyContain(t => t.Liberado, "toda carga terminada suelta su temporizador");
    }

    [Fact]
    public async Task La_ventana_cierra_la_carga_y_lo_posterior_no_se_registra_dos_veces()
    {
        var (handler, coste, _, logger, reloj) = Crear(habilitado: true, uriInicial: "http://localhost/");

        await handler.OnCircuitOpenedAsync(null!, default);
        coste.RegistrarComando(TimeSpan.FromMilliseconds(1));
        reloj.Temporizadores.Should().ContainSingle().Which.Vencimiento.Should().Be(TimeSpan.FromSeconds(5));

        reloj.Temporizadores[0].Disparar();
        coste.RegistrarComando(TimeSpan.FromMilliseconds(1));
        await handler.OnCircuitClosedAsync(null!, default);

        logger.Entradas.Should().ContainSingle();
        logger.Entradas[0].Valor("Motivo").Should().Be(MedicionCargaCircuitoHandler.MotivoVentana);
        logger.Entradas[0].Valor("Comandos").Should().Be(1L);
    }

    [Fact]
    public async Task Con_la_opcion_inactiva_no_registra_ni_se_suscribe()
    {
        var (handler, coste, navegacion, logger, reloj) = Crear(habilitado: false, uriInicial: "http://localhost/empresas");

        await handler.OnCircuitOpenedAsync(null!, default);
        coste.RegistrarComando(TimeSpan.FromMilliseconds(1));
        navegacion.Navegar("http://localhost/");
        await handler.OnCircuitClosedAsync(null!, default);

        logger.Entradas.Should().BeEmpty();
        reloj.Temporizadores.Should().BeEmpty();
    }

    [Fact]
    public async Task Tras_cerrar_el_circuito_una_navegacion_ya_no_abre_carga()
    {
        var (handler, _, navegacion, logger, reloj) = Crear(habilitado: true, uriInicial: "http://localhost/");

        await handler.OnCircuitOpenedAsync(null!, default);
        await handler.OnCircuitClosedAsync(null!, default);
        navegacion.Navegar("http://localhost/empresas");

        logger.Entradas.Should().ContainSingle();
        reloj.Temporizadores.Should().ContainSingle();
    }

    private static (MedicionCargaCircuitoHandler, CosteCarga, NavegacionDePrueba, LoggerQueCaptura, RelojDePrueba) Crear(
        bool habilitado, string uriInicial)
    {
        var coste = new CosteCarga();
        var navegacion = new NavegacionDePrueba(uriInicial);
        var logger = new LoggerQueCaptura();
        var reloj = new RelojDePrueba();
        var handler = new MedicionCargaCircuitoHandler(
            coste,
            navegacion,
            Plantillas("/", "/empresas", "/empresas/{Id:guid}"),
            Options.Create(new OpcionesServerTiming { Habilitado = habilitado }),
            reloj,
            logger);
        return (handler, coste, navegacion, logger, reloj);
    }

    private static PlantillasDeRutaDePagina Plantillas(params string[] rutas) =>
        new(new DefaultEndpointDataSource(rutas.Select(r => Endpoint(r, componente: true)).ToArray()));

    private static RouteEndpoint Endpoint(string plantilla, bool componente) => new(
        _ => Task.CompletedTask,
        RoutePatternFactory.Parse(plantilla),
        order: 0,
        componente ? new EndpointMetadataCollection(new ComponentTypeMetadata(typeof(ComponenteDePrueba))) : EndpointMetadataCollection.Empty,
        plantilla);

    private sealed class ComponenteDePrueba : ComponentBase;

    private sealed class NavegacionDePrueba : NavigationManager
    {
        public NavegacionDePrueba(string uri) => Initialize("http://localhost/", uri);

        public void Navegar(string uri)
        {
            Uri = uri;
            NotifyLocationChanged(isInterceptedLink: false);
        }

        protected override void NavigateToCore(string uri, bool forceLoad) => Navegar(ToAbsoluteUri(uri).ToString());
    }

    private sealed class LoggerQueCaptura : ILogger<MedicionCargaCircuitoHandler>
    {
        public List<Entrada> Entradas { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entradas.Add(new Entrada(logLevel, formatter(state, exception),
                state as IReadOnlyList<KeyValuePair<string, object?>> ?? []));
    }

    private sealed record Entrada(LogLevel Nivel, string Mensaje, IReadOnlyList<KeyValuePair<string, object?>> Valores)
    {
        public object? Valor(string nombre) => Valores.Single(v => v.Key == nombre).Value;
    }

    private sealed class RelojDePrueba : TimeProvider
    {
        public List<TemporizadorDePrueba> Temporizadores { get; } = [];

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var temporizador = new TemporizadorDePrueba(callback, state, dueTime);
            Temporizadores.Add(temporizador);
            return temporizador;
        }
    }

    private sealed class TemporizadorDePrueba(TimerCallback callback, object? estado, TimeSpan vencimiento) : ITimer
    {
        public TimeSpan Vencimiento { get; } = vencimiento;

        public bool Liberado { get; private set; }

        public void Disparar() => callback(estado);

        public bool Change(TimeSpan dueTime, TimeSpan period) => true;

        public void Dispose() => Liberado = true;

        public ValueTask DisposeAsync()
        {
            Liberado = true;
            return ValueTask.CompletedTask;
        }
    }
}
