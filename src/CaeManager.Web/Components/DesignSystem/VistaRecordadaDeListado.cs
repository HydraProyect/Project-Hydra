using CaeManager.Application.Configuracion;
using CaeManager.Application.Configuracion.Commands.GuardarVistaRecordada;
using CaeManager.Application.Configuracion.Commands.OlvidarVistaRecordada;
using CaeManager.Application.Configuracion.Queries;
using CaeManager.Web.Recursos;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.Extensions.Localization;

namespace CaeManager.Web.Components.DesignSystem;

/// <summary>
/// Vista recordada de un listado — la pieza compartida (decisión del 2026-10-08: los filtros, el
/// orden y la agrupación se recuerdan por Usuario y Tenant, para que nadie reconfigure la pantalla
/// cada vez que entra). No pinta nada: «Restablecer vista» lo dibuja <see cref="BarraFiltros"/> con
/// la <see cref="ConexionVistaRecordada"/> que la página le pasa.
///
/// <para>
/// QUÉ ES LA VISTA: la lista blanca <see cref="ParametrosDeVista"/> presente en la URL, la misma de
/// los filtros guardados (<see cref="VistaDeListado"/>). Selección, fila abierta, página y grupos
/// abiertos no viajan en esa lista y no se recuerdan.
/// </para>
///
/// <para>
/// RESTAURAR: una vez, al montarse con circuito (nunca en el prerender: allí una navegación es una
/// redirección) y solo si la URL llega SIN cadena de consulta. Con cualquier parámetro manda la
/// URL: un enlace compartido se ve como se compartió. La vista recordada se entrega a la página por
/// <see cref="OnAplicar"/>, el mismo camino que un filtro guardado: la página valida cada valor
/// como valida la URL (lo recordado no es autoridad), escribe la URL en UNA navegación y recarga.
/// </para>
///
/// <para>
/// RECORDAR: cada cambio de dirección dentro de la página se compara con lo último recordado. Si la
/// vista cambió, se escribe pasado <see cref="Rebote"/> sin más cambios —cada escritura deja una
/// fila de auditoría, así que una ráfaga de filtros es una sola—; si volvió a la de inicio, se
/// olvida en vez de guardar un diccionario vacío. Restaurar no escribe.
/// </para>
///
/// <para>
/// AL SALIR con una escritura pendiente: si se sale navegando a otra pantalla, el aviso de cambio
/// de dirección llega con la pieza aún viva y la escritura se hace en ese momento, sin esperar al
/// rebote. Si la pieza se retira sin ese aviso, el temporizador se cancela —no queda ninguna tarea
/// viva detrás de un componente retirado— y lo pendiente pasa a la <see cref="Conexion"/>: si la
/// página sigue (la pieza estaba bajo una rama que se repinta, o bajo una pestaña), la pieza que se
/// monte después lo recoge y vuelve a esperar el rebote; si no (se cerró la pestaña, se recargó,
/// cayó el circuito), ese último cambio no se recuerda.
/// </para>
///
/// <para>
/// Los tres casos de uso son de autoservicio: bajo una Sesión Privilegiada se rechazan. Recordar
/// la vista es una comodidad, así que cualquier fallo va al registro y la página no se entera.
/// </para>
///
/// <code>
/// private readonly ConexionVistaRecordada _vistaRecordada = new();
///
/// &lt;BarraFiltros … VistaRecordada="_vistaRecordada"&gt; … &lt;/BarraFiltros&gt;
/// &lt;VistaRecordadaDeListado Conexion="_vistaRecordada" Pantalla="@PantallasConVistaRecordada.Empresas"
///                          ParametrosDeVista="ParametrosDeVista" OnAplicar="AplicarVistaGuardadaAsync" AlCambiar="StateHasChanged" /&gt;
/// </code>
/// </summary>
public sealed class VistaRecordadaDeListado : ComponentBase, IDisposable
{
    /// <summary>Tiempo sin cambios de vista tras el que se escribe.</summary>
    public static readonly TimeSpan Rebote = TimeSpan.FromSeconds(2);

    /// <summary>La vista de inicio serializada: no hay nada que recordar.</summary>
    private const string SinVista = "{}";

    [Inject] private IMediator Mediator { get; set; } = default!;
    [Inject] private NavigationManager Navegacion { get; set; } = default!;
    [Inject] private ToastService ToastService { get; set; } = default!;
    [Inject] private IStringLocalizer<TextosComunes> Comunes { get; set; } = default!;
    [Inject] private ILogger<VistaRecordadaDeListado> Logger { get; set; } = default!;
    [Inject] private IServiceProvider Servicios { get; set; } = default!;

    /// <summary>Lo que la página pasa a su <see cref="BarraFiltros"/>.</summary>
    [Parameter, EditorRequired] public ConexionVistaRecordada Conexion { get; set; } = default!;

    /// <summary>Una de <see cref="PantallasConVistaRecordada"/>: la clave bajo la que se recuerda.</summary>
    [Parameter, EditorRequired] public string Pantalla { get; set; } = string.Empty;

    /// <summary>Lista blanca de parámetros de vista de la URL de la página. Lo demás ni se recuerda ni se restaura.</summary>
    [Parameter, EditorRequired] public IReadOnlyList<string> ParametrosDeVista { get; set; } = [];

    /// <summary>
    /// Parámetros de la lista blanca que forman parte de la vista de inicio: se recuerdan y se
    /// restauran, pero no cuentan como desviación ni los quita «Restablecer vista». Es el Cliente
    /// empresarial elegido en Proyectos, sin el que esa pantalla no tiene lista.
    /// </summary>
    [Parameter] public IReadOnlyList<string> ParametrosDeContexto { get; set; } = [];

    /// <summary>
    /// La vista a aplicar: un valor por cada parámetro de <see cref="ParametrosDeVista"/>, <c>null</c> los que
    /// no trae. La página valida como valida la URL, escribe la URL en una navegación y recarga.
    /// </summary>
    [Parameter, EditorRequired] public EventCallback<IReadOnlyDictionary<string, string?>> OnAplicar { get; set; }

    /// <summary>«Difiere de la de inicio» cambió: la página se repinta para que la barra lo vea.</summary>
    [Parameter, EditorRequired] public EventCallback AlCambiar { get; set; }

    private string _ruta = string.Empty;
    private bool _escuchando;
    private bool _desechado;

    /// <summary>La vista que se da por recordada: con ella se compara cada cambio de dirección.</summary>
    private string _recordada = SinVista;

    /// <summary>La vista que espera al rebote para escribirse, o <c>null</c>.</summary>
    private string? _pendiente;
    private CancellationTokenSource? _temporizador;

    /// <summary>Hubo un cambio de dirección mientras se leía la vista recordada: el usuario ya actuó y no se le pisa.</summary>
    private bool _huboCambios;

    /// <summary>
    /// «Restablecer vista» pulsado y la vista de inicio que se espera ver en la URL. La página puede
    /// no aplicarla (Proyectos pregunta antes si hay algo a medias), y con circuito la URL cambia
    /// después de que la página haya terminado: lo recordado se sustituye cuando la URL lo confirma.
    /// </summary>
    private string? _restableciendo;

    private TimeProvider Tiempo => Servicios.GetService<TimeProvider>() ?? TimeProvider.System;

    protected override void OnInitialized()
    {
        Conexion.Conectar(this);
        _ruta = RutaDe(Navegacion.Uri);

        // La barra se pintó antes que esta pieza: si la URL ya trae una vista propia, se repinta.
        ActualizarDiferencia(VistaDeLaUrl());
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        // Solo con circuito: el prerender no pasa por aquí, así que ni restaura (sería una
        // redirección) ni se queda escuchando la dirección de una petición que ya terminó.
        if (!firstRender || _desechado)
            return;

        _recordada = Serializar(VistaDeLaUrl());
        Navegacion.LocationChanged += AlCambiarDireccion;
        _escuchando = true;

        // Una pieza anterior de esta misma visita se retiró con un cambio sin escribir: se retoma.
        if (Conexion.PendienteHeredado is { } heredado)
        {
            Conexion.PendienteHeredado = null;
            Programar(heredado);
        }

        // Una vez por visita a la página, no una por montaje de la pieza.
        if (Conexion.RestauracionIntentada)
            return;

        Conexion.RestauracionIntentada = true;

        // La URL manda: con cualquier parámetro no se restaura nada ni hace falta leer lo recordado.
        if (TieneConsulta(Navegacion.Uri))
            return;

        string? valoresJson;
        try
        {
            valoresJson = await Mediator.Send(new ObtenerVistaRecordadaQuery(Pantalla));
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "No se pudo leer la vista recordada de la pantalla {Pantalla}", Pantalla);
            return;
        }

        // Mientras se leía, la página pudo retirarse o el usuario pudo cambiar ya la vista.
        if (_desechado || _huboCambios || TieneConsulta(Navegacion.Uri) || !EsMiRuta(Navegacion.Uri))
            return;

        // Un JSON ilegible no es una vista. Lo que traiga fuera de la lista blanca no llega a la página.
        if (VistaDeListado.Leer(valoresJson) is not { } leida)
            return;

        var recordada = leida.Where(p => ParametrosDeVista.Contains(p.Key)).ToDictionary(p => p.Key, p => p.Value);
        if (recordada.Count == 0)
            return;

        // Restaurar no es un cambio del usuario: lo que la navegación de la página traiga de vuelta
        // coincide con lo recordado y no se escribe. Si la página descartó un valor que ya no vale,
        // la diferencia sí se escribe, una vez, y lo recordado queda corregido.
        _recordada = Serializar(recordada);
        try
        {
            await OnAplicar.InvokeAsync(VistaDeListado.Completa(ParametrosDeVista, recordada));
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "No se pudo restaurar la vista recordada de la pantalla {Pantalla}", Pantalla);
        }
    }

    public void Dispose()
    {
        if (_desechado)
            return;

        _desechado = true;
        if (_escuchando)
            Navegacion.LocationChanged -= AlCambiarDireccion;

        // El temporizador no sobrevive a la pieza; lo pendiente lo hereda la que se monte después,
        // si la página sigue. Ver «AL SALIR» en el resumen de la clase.
        CancelarTemporizador();
        Conexion.PendienteHeredado = _pendiente;
        _pendiente = null;
        Conexion.Desconectar(this);
    }

    private void AlCambiarDireccion(object? sender, LocationChangedEventArgs e)
    {
        if (_desechado)
            return;

        if (!EsMiRuta(e.Location))
        {
            // Se sale a otra pantalla con un cambio sin escribir: se escribe ya, con la pieza aún viva.
            if (_pendiente is { } pendiente)
            {
                CancelarTemporizador();
                _ = InvokeAsync(() => PersistirAsync(pendiente));
            }

            return;
        }

        _huboCambios = true;
        var vista = VistaDeListado.DeLaUrl(new Uri(e.Location), ParametrosDeVista);
        ActualizarDiferencia(vista);

        var serializada = Serializar(vista);
        if (_restableciendo is { } esperada)
        {
            if (serializada == esperada)
            {
                _ = InvokeAsync(CompletarRestablecerAsync);
                return;
            }

            // Llegó otra vista: la página no restableció, o el usuario ya cambió algo más.
            _restableciendo = null;
        }

        if (serializada == _recordada)
        {
            // Volvió a lo ya recordado antes de que venciera el rebote: no hay nada que escribir.
            CancelarTemporizador();
            _pendiente = null;
            return;
        }

        Programar(serializada);
    }

    /// <summary>
    /// «Restablecer vista»: la página quita de la URL todos los parámetros de vista —también el orden
    /// y la agrupación, que «Quitar filtros» no quita— en una navegación y recarga, y lo recordado
    /// pasa a ser la vista de inicio. Los parámetros de contexto se quedan como están.
    /// </summary>
    internal async Task RestablecerAsync()
    {
        if (_desechado)
            return;

        var inicio = VistaDeLaUrl().Where(p => ParametrosDeContexto.Contains(p.Key)).ToDictionary(p => p.Key, p => p.Value);
        var serializada = Serializar(inicio);

        CancelarTemporizador();
        _pendiente = null;
        _restableciendo = serializada;

        await OnAplicar.InvokeAsync(VistaDeListado.Completa(ParametrosDeVista, inicio));

        // Sin circuito de por medio la URL ya cambió; con él, lo completa el aviso de cambio de dirección.
        if (Serializar(VistaDeLaUrl()) == serializada)
            await CompletarRestablecerAsync();
    }

    /// <summary>La URL ya muestra la vista de inicio: lo recordado pasa a ser esa vista, sin esperar al rebote.</summary>
    private async Task CompletarRestablecerAsync()
    {
        if (_restableciendo is not { } serializada)
            return;

        _restableciendo = null;
        CancelarTemporizador();
        _pendiente = null;
        _recordada = serializada;

        await EnviarAsync(serializada);
        ToastService.Mostrar(Comunes["VistaRestablecidaToast"], TonoToast.Exito);
    }

    private void Programar(string serializada)
    {
        CancelarTemporizador();
        _pendiente = serializada;
        var temporizador = _temporizador = new CancellationTokenSource();
        _ = EsperarYPersistirAsync(serializada, temporizador.Token);
    }

    private async Task EsperarYPersistirAsync(string serializada, CancellationToken token)
    {
        try
        {
            await Task.Delay(Rebote, Tiempo, token);
            await InvokeAsync(() => token.IsCancellationRequested || _desechado ? Task.CompletedTask : PersistirAsync(serializada));
        }
        catch (OperationCanceledException)
        {
            // Llegó otro cambio, o la pieza se retiró: esta espera ya no escribe.
        }
        catch (ObjectDisposedException)
        {
            // El circuito se cerró mientras vencía el rebote.
        }
    }

    private Task PersistirAsync(string serializada)
    {
        _pendiente = null;
        _recordada = serializada;
        return EnviarAsync(serializada);
    }

    /// <summary>Guarda la vista, u olvida la recordada si es la de inicio. Nunca lanza.</summary>
    private async Task EnviarAsync(string serializada)
    {
        try
        {
            var resultado = serializada == SinVista
                ? await Mediator.Send(new OlvidarVistaRecordadaCommand(Pantalla))
                : await Mediator.Send(new GuardarVistaRecordadaCommand(Pantalla, serializada));

            // P. ej. bajo una Sesión Privilegiada: lo esperado, sin aviso. Al registro va la pantalla, no la vista.
            if (resultado is { EsFallido: true })
                Logger.LogInformation("No se recordó la vista de la pantalla {Pantalla}: {Codigo}", Pantalla, resultado.Error.Codigo);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "No se pudo recordar la vista de la pantalla {Pantalla}", Pantalla);
        }
    }

    private void CancelarTemporizador()
    {
        if (_temporizador is not { } temporizador)
            return;

        _temporizador = null;
        temporizador.Cancel();
        temporizador.Dispose();
    }

    private void ActualizarDiferencia(IReadOnlyDictionary<string, string> vista)
    {
        var difiere = vista.Keys.Any(p => !ParametrosDeContexto.Contains(p));
        if (difiere == Conexion.DifiereDeInicio)
            return;

        Conexion.DifiereDeInicio = difiere;
        _ = InvokeAsync(AlCambiar.InvokeAsync);
    }

    private Dictionary<string, string> VistaDeLaUrl() =>
        VistaDeListado.DeLaUrl(Navegacion.ToAbsoluteUri(Navegacion.Uri), ParametrosDeVista);

    private string Serializar(IReadOnlyDictionary<string, string> vista) => VistaDeListado.Serializar(ParametrosDeVista, vista);

    private bool EsMiRuta(string direccion) => string.Equals(RutaDe(direccion), _ruta, StringComparison.OrdinalIgnoreCase);

    private string RutaDe(string direccion) => Navegacion.ToAbsoluteUri(direccion).AbsolutePath.TrimEnd('/');

    private bool TieneConsulta(string direccion) => Navegacion.ToAbsoluteUri(direccion).Query.Length > 1;
}
