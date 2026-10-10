namespace CaeManager.Web.Components.DesignSystem;

/// <summary>
/// Lo que un listado tiene en la mano de su <see cref="FiltrosGuardadosDeListado"/>: las opciones
/// que pinta <see cref="BarraFiltros"/> y los tres gestos de la barra (guardar, aplicar, borrar).
///
/// <para>
/// Existe para que la página pueda enlazar la barra con grupos de métodos desde el primer render,
/// antes de que la pieza se haya montado (un <c>@ref</c> sería nulo ahí). Hasta que la pieza se
/// conecta no hay opciones y los gestos no hacen nada.
/// </para>
///
/// <code>
/// private readonly ConexionFiltrosGuardados _filtrosGuardados = new();
///
/// &lt;BarraFiltros … FiltrosGuardados="_filtrosGuardados.Opciones" OnGuardarFiltro="_filtrosGuardados.AbrirGuardar"
///               OnAplicarFiltroGuardado="_filtrosGuardados.AplicarAsync" OnBorrarFiltroGuardado="_filtrosGuardados.PedirBorrar"&gt;
/// </code>
/// </summary>
public sealed class ConexionFiltrosGuardados
{
    private FiltrosGuardadosDeListado? _pieza;

    /// <summary>Los filtros guardados del usuario en esta pantalla y este Tenant: <c>Valor</c> es el Id, <c>Texto</c> el nombre.</summary>
    public IReadOnlyList<OpcionEstado> Opciones { get; internal set; } = [];

    /// <summary>«Guardar filtro» de la barra: abre el modal que pide el nombre.</summary>
    public void AbrirGuardar() => _pieza?.AbrirGuardar();

    /// <summary>Un filtro guardado elegido en la barra, por su Id.</summary>
    public Task AplicarAsync(string id) => _pieza?.AplicarAsync(id) ?? Task.CompletedTask;

    /// <summary>La ✕ de un filtro guardado: pide confirmación antes de borrarlo.</summary>
    public void PedirBorrar(string id) => _pieza?.PedirBorrar(id);

    internal void Conectar(FiltrosGuardadosDeListado pieza) => _pieza = pieza;

    /// <summary>Solo suelta la pieza si sigue siendo la conectada: al remontarse, la nueva se conecta antes de que la vieja se retire.</summary>
    internal void Desconectar(FiltrosGuardadosDeListado pieza)
    {
        if (ReferenceEquals(_pieza, pieza))
            _pieza = null;
    }
}
