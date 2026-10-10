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
/// LA BÚSQUEDA LIBRE NO SE RECUERDA: <see cref="ParametroDeBusqueda"/> es de la vista —cuenta para
/// ofrecer «Restablecer vista» y ese gesto la quita— pero ni se escribe ni se restaura. En el buscador
/// se teclean nombres y DNI, y recordarlo sin que nadie lo pida dejaría ese texto guardado (y en la
/// auditoría de cada escritura) más allá de la vida del dato buscado. Un filtro guardado sí la lleva:
/// lo guarda el usuario a propósito y con nombre. Una vista recordada escrita antes de esta regla
/// puede traerla: se restaura sin ella.
/// </para>
///
/// <para>
/// RESTAURAR: una vez por visita, con circuito (nunca en el prerender: allí una navegación es una
/// redirección), cuando la página puede validar lo recordado (<see cref="ListoParaRestaurar"/>) y solo
/// si la URL llega SIN cadena de consulta. Con cualquier parámetro manda la
/// URL: un enlace compartido se ve como se compartió. La vista recordada se entrega a la página por
/// <see cref="OnAplicar"/>, el mismo camino que un filtro guardado: la página valida cada valor
/// como valida la URL (lo recordado no es autoridad), escribe la URL en UNA navegación y recarga.
/// La página NO espera a esta lectura para cargar: con circuito pide su lista de fábrica y, si hay
/// vista recordada, la pide otra vez con ella (dos consultas, medido en rejilla y en acordeón y
/// fijado en <c>VistaRecordadaEnListadosTests</c>). A cambio, una lectura lenta o fallida nunca
/// deja la lista sin cargar.
/// </para>
///
/// <para>
/// RESTAURAR NUNCA REDUCE LO RECORDADO: si la página descarta un valor recordado (un Gestor CAE que su
/// directorio ya no ofrece, un estado que ya no existe), la vista que queda aplicada es la línea base
/// de la visita y no se escribe. Solo un cambio posterior del usuario escribe. Sin esto, una validación
/// hecha antes de tiempo —con el directorio aún sin cargar— borraría para siempre un valor que seguía
/// siendo bueno.
/// </para>
///
/// <para>
/// RECORDAR: cada cambio de dirección dentro de la página se compara con lo último recordado. Si la
/// vista cambió —sin contar la búsqueda libre: teclear en el buscador no escribe nada—, se escribe
/// pasado <see cref="Rebote"/> sin más cambios —cada escritura deja una
/// fila de auditoría, así que una ráfaga de filtros es una sola—; si volvió a la de inicio, se
/// olvida en vez de guardar un diccionario vacío. Restaurar no escribe.
/// </para>
///
/// <para>
/// AL SALIR con una escritura pendiente: si se sale navegando a otra pantalla, el aviso de cambio
/// de dirección llega con la pieza aún viva y la escritura se hace en ese momento, sin esperar al
/// rebote. Si la pieza se retira sin ese aviso, el temporizador se cancela —no queda ninguna tarea
/// viva detrás de un componente retirado— y que hay algo pendiente pasa a la <see cref="Conexion"/>:
/// si la página sigue (la pieza estaba bajo una rama que se repinta, o bajo una pestaña), la pieza que
/// se monte después programa la escritura de la vista que la URL muestre ENTONCES —no la del momento
/// del retiro: entre uno y otro nadie escuchaba la dirección— y vuelve a esperar el rebote; si no (se
/// cerró la pestaña, se recargó, cayó el circuito), ese último cambio no se recuerda.
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
///                          ParametrosDeVista="ParametrosDeVista" ParametroDeBusqueda="q"
///                          OnAplicar="AplicarVistaGuardadaAsync" AlCambiar="StateHasChanged" /&gt;
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
    /// El parámetro de <see cref="ParametrosDeVista"/> que lleva la búsqueda libre de la página (vacío si
    /// no tiene buscador). Es de la vista para «Restablecer vista», pero no se recuerda ni se restaura:
    /// ver «LA BÚSQUEDA LIBRE NO SE RECUERDA» en el resumen de la clase.
    /// </summary>
    [Parameter, EditorRequired] public string ParametroDeBusqueda { get; set; } = string.Empty;

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

    /// <summary>
    /// La página ya puede validar una vista recordada: tiene cargado aquello contra lo que comprueba sus
    /// valores (el directorio de Gestores CAE visibles en Clientes). Mientras sea <c>false</c> no se
    /// restaura; se restaura en el primer render en que sea <c>true</c>. Una página que valida sin
    /// cargar nada, o que no monta la pieza hasta haberlo cargado, no lo pasa.
    /// </summary>
    [Parameter] public bool ListoParaRestaurar { get; set; } = true;

    private string _ruta = string.Empty;
    private bool _escuchando;
    private bool _desechado;

    /// <summary>
    /// Vida de la pieza: corta la LECTURA de la vista recordada si la pieza se retira con ella en vuelo.
    /// Las escrituras no lo llevan: la de salida se lanza justo antes de que la pieza se retire.
    /// </summary>
    private readonly CancellationTokenSource _ciclo = new();

    /// <summary>
    /// La vista que se da por recordada: con ella se compara cada cambio de dirección. <c>null</c>: no
    /// se sabe (la pieza heredó una escritura pendiente de otra que se retiró), y cualquier vista es un
    /// cambio hasta que se escriba una.
    /// </summary>
    private string? _recordada = SinVista;

    /// <summary>
    /// La vista recordada que se acaba de entregar a la página, hasta el primer cambio de dirección: si
    /// ese cambio es lo recordado con valores de menos, es la página aplicándola, no el usuario.
    /// </summary>
    private Dictionary<string, string>? _restaurada;

    /// <summary>La vista que espera al rebote para escribirse, o <c>null</c>.</summary>
    private string? _pendiente;
    private int _turno;

    /// <summary>Hubo un cambio de dirección antes de restaurar (la página no estaba lista, o se leía lo recordado): el usuario ya actuó y no se le pisa.</summary>
    private bool _huboCambios;

    /// <summary>
    /// «Restablecer vista» pulsado y la vista de inicio que se espera ver en la URL (entera: con la
    /// búsqueda, que ese gesto también quita). La página puede
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
        if (_desechado)
            return;

        if (firstRender)
        {
            Navegacion.LocationChanged += AlCambiarDireccion;
            _escuchando = true;

            if (Conexion.HayPendienteHeredado)
            {
                // Una pieza anterior de esta misma visita se retiró con un cambio sin escribir. Entre su
                // retiro y este montaje nadie escuchaba la dirección: lo que hay que escribir es la vista
                // que la URL muestra ahora, y lo que quedó recordado ya no se sabe.
                Conexion.HayPendienteHeredado = false;
                _recordada = null;
                Programar(Serializar(VistaDeLaUrl()));
            }
            else
            {
                _recordada = Serializar(VistaDeLaUrl());
            }
        }

        // Una vez por visita a la página, no una por montaje de la pieza; y no antes de que la página
        // pueda validar lo recordado: este método vuelve a pasar en el render en que ya puede.
        if (Conexion.RestauracionIntentada || !ListoParaRestaurar)
            return;

        Conexion.RestauracionIntentada = true;

        // La URL manda: con cualquier parámetro no se restaura nada ni hace falta leer lo recordado.
        // Tampoco si el usuario ya cambió la vista mientras la página se preparaba.
        if (_huboCambios || TieneConsulta(Navegacion.Uri))
            return;

        string? valoresJson;
        try
        {
            valoresJson = await Mediator.Send(new ObtenerVistaRecordadaQuery(Pantalla), _ciclo.Token);
        }
        catch (OperationCanceledException) when (_desechado)
        {
            // La pieza se retiró con la lectura en vuelo: no queda a quién restaurarle nada.
            return;
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

        // Tampoco la búsqueda libre que pueda traer una fila escrita antes de dejar de recordarla.
        var recordables = ParametrosRecordados;
        var recordada = leida.Where(p => recordables.Contains(p.Key)).ToDictionary(p => p.Key, p => p.Value);
        if (recordada.Count == 0)
            return;

        // Restaurar no es un cambio del usuario: lo que la navegación de la página traiga de vuelta
        // coincide con lo recordado, o es lo recordado sin los valores que la página descartó, y en
        // ninguno de los dos casos se escribe (ver AlCambiarDireccion).
        _recordada = Serializar(recordada);
        _restaurada = recordada;
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
        _ciclo.Cancel();
        _ciclo.Dispose();
        if (_escuchando)
            Navegacion.LocationChanged -= AlCambiarDireccion;

        // El temporizador no sobrevive a la pieza; que había algo pendiente lo hereda la que se monte
        // después, si la página sigue. Ver «AL SALIR» en el resumen de la clase.
        CancelarTemporizador();
        Conexion.HayPendienteHeredado = _pendiente is not null;
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

        if (_restableciendo is { } esperada)
        {
            if (SerializarEntera(vista) == esperada)
            {
                _ = InvokeAsync(CompletarRestablecerAsync);
                return;
            }

            // Llegó otra vista: la página no restableció, o el usuario ya cambió algo más.
            _restableciendo = null;
        }

        // Sin la búsqueda libre: un cambio que solo la toque deja la vista recordable como estaba.
        var serializada = Serializar(vista);

        // El primer cambio de dirección tras restaurar. Si es lo recordado con valores de menos, es la
        // página, que descartó lo que no pudo validar: esa vista es la línea base y no se escribe.
        if (_restaurada is { } restaurada)
        {
            _restaurada = null;
            if (EsReduccionDe(vista, restaurada))
            {
                CancelarTemporizador();
                _pendiente = null;
                _recordada = serializada;
                return;
            }
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
        var esperada = SerializarEntera(inicio);

        CancelarTemporizador();
        _pendiente = null;
        _restableciendo = esperada;

        await OnAplicar.InvokeAsync(VistaDeListado.Completa(ParametrosDeVista, inicio));

        // Sin circuito de por medio la URL ya cambió; con él, lo completa el aviso de cambio de dirección.
        if (SerializarEntera(VistaDeLaUrl()) == esperada)
            await CompletarRestablecerAsync();
    }

    /// <summary>La URL ya muestra la vista de inicio: lo recordado pasa a ser esa vista, sin esperar al rebote.</summary>
    private async Task CompletarRestablecerAsync()
    {
        if (_restableciendo is null)
            return;

        var serializada = Serializar(VistaDeLaUrl());
        _restableciendo = null;
        CancelarTemporizador();
        _pendiente = null;
        _recordada = serializada;

        await EnviarAsync(serializada);
        ToastService.Mostrar(Comunes["VistaRestablecidaToast"], TonoToast.Exito);
    }

    private void Programar(string serializada)
    {
        _pendiente = serializada;
        _ = EsperarYPersistirAsync(serializada, ++_turno, _ciclo.Token);
    }

    private async Task EsperarYPersistirAsync(string serializada, int turno, CancellationToken token)
    {
        try
        {
            await Task.Delay(Rebote, Tiempo, token);
            await InvokeAsync(() => turno != _turno || _desechado ? Task.CompletedTask : PersistirAsync(serializada));
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

    /// <summary>
    /// Deja sin efecto la escritura programada SIN cancelar su espera: avanza el turno, y la espera que
    /// venza con un turno viejo no escribe. Cancelar el <c>Task.Delay</c> pendiente reencolaba su
    /// continuación en el <c>Dispatcher</c> con un salto de hilo real en cada cambio de la URL (la misma
    /// trampa que documenta <c>CampoTexto.ManejarBlurAsync</c>), y con ella un <c>Click()</c> de bUnit
    /// devolvía el control antes de que su manejador terminase: 2 rojos en 13 500 repeticiones de un test de
    /// Visitas, 0 en 42 000 sin la cancelación. Las esperas solo se cancelan al desechar la pieza.
    /// </summary>
    private void CancelarTemporizador() => _turno++;

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

    /// <summary>
    /// Esa vista no trae nada que lo restaurado no trajera, ni con otro valor: es lo restaurado, entero
    /// o con valores de menos. La búsqueda libre no cuenta.
    /// </summary>
    private bool EsReduccionDe(IReadOnlyDictionary<string, string> vista, Dictionary<string, string> restaurada) =>
        ParametrosRecordados.All(p => !vista.TryGetValue(p, out var valor) || restaurada.GetValueOrDefault(p) == valor);

    /// <summary>La lista blanca sin la búsqueda libre: lo único que se escribe, se compara y se restaura.</summary>
    private IReadOnlyList<string> ParametrosRecordados =>
        [.. ParametrosDeVista.Where(p => !string.Equals(p, ParametroDeBusqueda, StringComparison.OrdinalIgnoreCase))];

    /// <summary>La vista tal como se recuerda: sin la búsqueda libre.</summary>
    private string Serializar(IReadOnlyDictionary<string, string> vista) => VistaDeListado.Serializar(ParametrosRecordados, vista);

    /// <summary>La vista entera de la URL, con la búsqueda: solo para saber si «Restablecer vista» ya se ve en ella.</summary>
    private string SerializarEntera(IReadOnlyDictionary<string, string> vista) => VistaDeListado.Serializar(ParametrosDeVista, vista);

    private bool EsMiRuta(string direccion) => string.Equals(RutaDe(direccion), _ruta, StringComparison.OrdinalIgnoreCase);

    private string RutaDe(string direccion) => Navegacion.ToAbsoluteUri(direccion).AbsolutePath.TrimEnd('/');

    private bool TieneConsulta(string direccion) => Navegacion.ToAbsoluteUri(direccion).Query.Length > 1;
}
