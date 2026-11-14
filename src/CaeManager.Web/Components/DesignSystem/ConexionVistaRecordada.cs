namespace CaeManager.Web.Components.DesignSystem;

/// <summary>
/// Lo que un listado tiene en la mano de su <see cref="VistaRecordadaDeListado"/>: si la vista
/// vigente difiere de la de inicio y el gesto «Restablecer vista». La página se lo pasa a
/// <see cref="BarraFiltros"/> (<c>VistaRecordada="_vistaRecordada"</c>), que pinta el enlace.
///
/// <para>
/// Existe por lo mismo que <see cref="ConexionFiltrosGuardados"/>: la barra se pinta antes de que
/// la pieza se haya montado. Hasta que la pieza se conecta no hay nada que restablecer.
/// </para>
/// </summary>
public sealed class ConexionVistaRecordada
{
    private VistaRecordadaDeListado? _pieza;

    /// <summary>
    /// La URL lleva algún parámetro de vista que no es de contexto: hay algo que «Restablecer vista»
    /// quitaría. Solo entonces se pinta el enlace.
    /// </summary>
    public bool DifiereDeInicio { get; internal set; }

    /// <summary>
    /// Ya se intentó restaurar en esta visita a la página. La pieza puede remontarse sin que la página
    /// se vaya (vive bajo una rama que se repinta al cargar, o bajo una pestaña): se restaura una vez
    /// por visita, no una por montaje, o un «Quitar filtros» volvería a traer lo recordado.
    /// </summary>
    internal bool RestauracionIntentada { get; set; }

    /// <summary>
    /// La pieza se retiró sin salir de la página con una vista esperando al rebote. La pieza que se
    /// monte después escribe la vista que la URL muestre entonces (no la del retiro: la URL pudo cambiar
    /// sin nadie escuchando); si no se monta ninguna (se cerró la pestaña), no se escribe.
    /// </summary>
    internal bool HayPendienteHeredado { get; set; }

    /// <summary>«Restablecer vista»: vuelve a la vista de inicio y olvida la recordada.</summary>
    public Task RestablecerAsync() => _pieza?.RestablecerAsync() ?? Task.CompletedTask;

    /// <summary>
    /// La página va a reescribir la URL por su cuenta, no el usuario (p. ej. un enlace antiguo con «?pestana=» que traduce
    /// a filtros): se llama ANTES de navegar, con la dirección que hay ahora (<paramref name="origen"/>, la del enlace) y la
    /// que la página va a pedir (<paramref name="destino"/>). Con circuito, los avisos de cambio de dirección de las dos
    /// llegan DESPUÉS, de ida y vuelta con el navegador: la pieza los descarta sin escribir ni olvidar nada, y lo recordado
    /// sigue siendo lo último que se escribió de verdad. Lo que la pieza tuviera programado se cancela ya. La marca vive
    /// aquí y no en la pieza para que la vea también una pieza que se monte después; se consume con el aviso del destino,
    /// cae con cualquier otro cambio de dirección y caduca a los <see cref="VistaRecordadaDeListado.CaducidadDeLaNavegacionDeLaPagina"/>.
    /// </summary>
    public void AnunciarNavegacionDeLaPagina(string origen, string destino)
    {
        NavegacionDeLaPagina = new NavegacionAnunciada(origen, destino);
        _pieza?.AlAnunciarNavegacionDeLaPagina();
    }

    /// <summary>La navegación que la página anunció y cuyos avisos aún no han llegado, o <c>null</c>.</summary>
    internal NavegacionAnunciada? NavegacionDeLaPagina { get; set; }

    /// <param name="Origen">La dirección desde la que la página navega: su aviso puede llegar tarde y también se descarta.</param>
    /// <param name="Destino">La dirección a la que navega: su aviso consume la marca.</param>
    internal sealed record NavegacionAnunciada(string Origen, string Destino)
    {
        /// <summary>Cuándo la vio una pieza montada, para la caducidad; <c>null</c> hasta que una la vea.</summary>
        public DateTimeOffset? Desde { get; set; }
    }

    internal void Conectar(VistaRecordadaDeListado pieza) => _pieza = pieza;

    /// <summary>Solo suelta la pieza si sigue siendo la conectada: al remontarse, la nueva se conecta antes de que la vieja se retire.</summary>
    internal void Desconectar(VistaRecordadaDeListado pieza)
    {
        if (ReferenceEquals(_pieza, pieza))
        {
            _pieza = null;
            DifiereDeInicio = false;
        }
    }
}
