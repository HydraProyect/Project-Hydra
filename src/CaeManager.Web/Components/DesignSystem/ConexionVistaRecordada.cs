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
    /// La vista que esperaba al rebote cuando la pieza se retiró sin salir de la página. La recoge la
    /// pieza que se monte después; si no se monta ninguna (se cerró la pestaña), no se escribe.
    /// </summary>
    internal string? PendienteHeredado { get; set; }

    /// <summary>«Restablecer vista»: vuelve a la vista de inicio y olvida la recordada.</summary>
    public Task RestablecerAsync() => _pieza?.RestablecerAsync() ?? Task.CompletedTask;

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
