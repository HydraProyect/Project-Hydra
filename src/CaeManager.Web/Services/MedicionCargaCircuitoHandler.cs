using CaeManager.Application.Common;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.Extensions.Options;

namespace CaeManager.Web.Services;

/// <summary>
/// El equivalente de <see cref="ServerTimingCosteCargaMiddleware"/> en el circuito
/// interactivo, donde no hay respuesta HTTP en la que poner una cabecera: un
/// registro Information por carga de página con el coste de acceso a datos de
/// esa carga (mismo <see cref="CosteCarga"/> del scope del circuito) y la
/// plantilla de la ruta (<see cref="PlantillasDeRutaDePagina"/>), nunca la ruta
/// concreta, ni ids, ni Tenant, ni usuario.
///
/// <para>
/// Una carga empieza al abrirse el circuito (el render interactivo de la página
/// inicial) y en cada navegación, y termina en la siguiente navegación, al
/// cerrarse el circuito o al vencer <see cref="OpcionesServerTiming.VentanaCargaCircuitoSegundos"/>,
/// lo que llegue antes. El coste de la carga es la diferencia entre dos
/// instantáneas del acumulador, que nunca se reinicia.
/// </para>
///
/// <para>
/// Scoped: una instancia por circuito, con el <see cref="CosteCarga"/> y el
/// <see cref="NavigationManager"/> de ese circuito. Con
/// <see cref="OpcionesServerTiming.Habilitado"/> apagado no se suscribe a nada.
/// </para>
/// </summary>
public sealed class MedicionCargaCircuitoHandler : CircuitHandler, IDisposable
{
    public const string MotivoNavegacion = "navegacion";
    public const string MotivoVentana = "ventana";
    public const string MotivoCircuitoCerrado = "circuito";

    private readonly CosteCarga _coste;
    private readonly NavigationManager _navegacion;
    private readonly PlantillasDeRutaDePagina _plantillas;
    private readonly OpcionesServerTiming _opciones;
    private readonly TimeProvider _reloj;
    private readonly ILogger<MedicionCargaCircuitoHandler> _logger;
    private Carga? _enCurso;
    private bool _suscrito;

    public MedicionCargaCircuitoHandler(
        CosteCarga coste,
        NavigationManager navegacion,
        PlantillasDeRutaDePagina plantillas,
        IOptions<OpcionesServerTiming> opciones,
        TimeProvider reloj,
        ILogger<MedicionCargaCircuitoHandler> logger)
    {
        _coste = coste;
        _navegacion = navegacion;
        _plantillas = plantillas;
        _opciones = opciones.Value;
        _reloj = reloj;
        _logger = logger;
    }

    public override Task OnCircuitOpenedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        if (_opciones.Habilitado && !_suscrito)
        {
            _suscrito = true;
            _navegacion.LocationChanged += AlNavegar;
            Proteger(() => IniciarCarga(_navegacion.ToBaseRelativePath(_navegacion.Uri)));
        }

        return Task.CompletedTask;
    }

    public override Task OnCircuitClosedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        Proteger(() => TerminarCargaEnCurso(MotivoCircuitoCerrado));
        Desuscribir();
        return Task.CompletedTask;
    }

    private void AlNavegar(object? remitente, LocationChangedEventArgs e) =>
        Proteger(() => IniciarCarga(_navegacion.ToBaseRelativePath(e.Location)));

    /// <summary>
    /// Una medición nunca puede romper el circuito ni la navegación (ni tumbar el proceso
    /// desde el temporizador): un fallo se registra solo con el tipo de la excepción, porque
    /// su mensaje puede llevar la ruta concreta.
    /// </summary>
    private void Proteger(Action accion)
    {
        try
        {
            accion();
        }
        catch (Exception ex)
        {
            try
            {
                _logger.LogWarning("La medición del coste de carga del circuito falló y se ignora: {TipoExcepcion}", ex.GetType().Name);
            }
            catch
            {
                // Si el propio registro falla (desde el temporizador no hay nadie por encima que
                // capture), se pierde la medición, no el proceso.
            }
        }
    }

    /// <summary>
    /// Empieza la carga de <paramref name="rutaRelativa"/> y termina la anterior
    /// con <see cref="MotivoNavegacion"/>. La consulta y el fragmento se quitan
    /// antes de resolver la plantilla.
    /// </summary>
    public void IniciarCarga(string rutaRelativa)
    {
        // Un cambio de solo consulta o fragmento (filtros, búsqueda, pestaña) no es una carga de
        // página nueva: sigue contando en la carga en curso. La ruta se compara en memoria y no
        // se registra nunca.
        var ruta = QuitarConsulta(rutaRelativa);
        if (Volatile.Read(ref _enCurso) is { } enCurso
            && string.Equals(enCurso.Ruta, ruta, StringComparison.OrdinalIgnoreCase))
            return;

        // Una sola instantánea de frontera: cierra la carga anterior y abre la nueva, así ningún
        // comando cuenta en las dos.
        var frontera = _coste.Leer();
        var nueva = new Carga(ruta, _plantillas.Resolver(ruta), frontera, _reloj.GetTimestamp());
        var anterior = Interlocked.Exchange(ref _enCurso, nueva);
        Terminar(anterior, MotivoNavegacion, frontera);

        var ventana = TimeSpan.FromSeconds(Math.Max(1, _opciones.VentanaCargaCircuitoSegundos));
        nueva.Temporizador = _reloj.CreateTimer(
            _ => Proteger(() => Terminar(nueva, MotivoVentana)), null, ventana, Timeout.InfiniteTimeSpan);
        // Si la carga terminó antes de asignar el temporizador (otra navegación o el cierre
        // en la misma ventana), nadie más lo liberaría.
        if (nueva.Terminada)
            nueva.Temporizador.Dispose();
    }

    public void TerminarCargaEnCurso(string motivo) => Terminar(Interlocked.Exchange(ref _enCurso, null), motivo);

    private void Terminar(Carga? carga, string motivo, InstantaneaCosteCarga? fin = null)
    {
        if (carga is null || !carga.MarcarTerminada())
            return;

        carga.Temporizador?.Dispose();
        var coste = (fin ?? _coste.Leer()).Menos(carga.Inicio);
        var duracion = _reloj.GetElapsedTime(carga.InicioMarca);
        _logger.LogInformation(
            "Coste de carga en circuito de {Plantilla}: {BaseDatosMs:0.0} ms de BD en {Comandos} comandos, "
            + "{Conexiones} aperturas de conexión, {EsperaPuertaMs:0.0} ms de espera en la puerta, "
            + "{Despachos} despachos de MediatR; {DuracionMs:0} ms medidos hasta {Motivo}",
            carga.Plantilla, coste.BaseDatos.TotalMilliseconds, coste.Comandos, coste.Conexiones,
            coste.EsperaPuerta.TotalMilliseconds, coste.Despachos, duracion.TotalMilliseconds, motivo);
    }

    private static string QuitarConsulta(string ruta)
    {
        var corte = ruta.IndexOfAny(['?', '#']);
        return corte >= 0 ? ruta[..corte] : ruta;
    }

    private void Desuscribir()
    {
        if (!_suscrito)
            return;
        _suscrito = false;
        _navegacion.LocationChanged -= AlNavegar;
    }

    public void Dispose()
    {
        Desuscribir();
        Interlocked.Exchange(ref _enCurso, null)?.Temporizador?.Dispose();
    }

    private sealed class Carga(string ruta, string plantilla, InstantaneaCosteCarga inicio, long inicioMarca)
    {
        private int _terminada;

        /// <summary>Solo para comparar en memoria; nunca se registra.</summary>
        public string Ruta { get; } = ruta;

        public string Plantilla { get; } = plantilla;

        public InstantaneaCosteCarga Inicio { get; } = inicio;

        public long InicioMarca { get; } = inicioMarca;

        public ITimer? Temporizador { get; set; }

        public bool Terminada => Volatile.Read(ref _terminada) == 1;

        public bool MarcarTerminada() => Interlocked.Exchange(ref _terminada, 1) == 0;
    }
}
