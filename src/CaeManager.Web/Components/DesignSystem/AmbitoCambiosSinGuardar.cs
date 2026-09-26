namespace CaeManager.Web.Components.DesignSystem;

/// <summary>
/// P1-E2b: una zona de la interfaz que se desmonta sin navegar: el contenido de
/// <c>Pestanas</c> (las del Context Workspace, las de /documentos y cualquier otra). El
/// componente la ofrece en cascada; cada <c>AvisoCambiosSinGuardar</c> de dentro se
/// registra, y antes de cambiar de pestaña se llama a <see cref="ConfirmarAbandonoAsync"/>,
/// que pregunta con el mismo aviso si hay cambios sin guardar.
///
/// Los ámbitos se encadenan: un aviso dentro de unas pestañas anidadas (las subpestañas de
/// Plantillas dentro de /documentos) se registra también en los ámbitos de fuera, porque
/// cambiar la pestaña de fuera también lo desmonta.
///
/// NavigationLock no basta ahí: cambiar de pestaña muta el estado antes que la URL, así que
/// cuando llega la navegación el formulario ya se ha desmontado.
/// </summary>
public sealed class AmbitoCambiosSinGuardar
{
    private readonly List<AvisoCambiosSinGuardar> _avisos = [];
    private readonly AmbitoCambiosSinGuardar? _padre;

    public AmbitoCambiosSinGuardar(AmbitoCambiosSinGuardar? padre = null) => _padre = padre;

    internal void Registrar(AvisoCambiosSinGuardar aviso)
    {
        _avisos.Add(aviso);
        _padre?.Registrar(aviso);
    }

    internal void Retirar(AvisoCambiosSinGuardar aviso)
    {
        _avisos.Remove(aviso);
        _padre?.Retirar(aviso);
    }

    /// <summary>
    /// <c>true</c> si se puede desmontar la zona: no hay cambios, o quien edita eligió
    /// descartarlos. <c>false</c> si eligió seguir editando.
    /// </summary>
    public async Task<bool> ConfirmarAbandonoAsync()
    {
        foreach (var aviso in _avisos.ToList())
        {
            if (!await aviso.ConfirmarAbandonoAsync())
                return false;
        }

        return true;
    }

    /// <summary>
    /// Quien llamó a <see cref="ConfirmarAbandonoAsync"/> ya ha hecho el cambio (y la
    /// navegación que lo acompañe): el permiso de salir sin preguntar que dejó el descarte
    /// caduca aquí. Sin esto, un aviso que sigue montado (un formulario de la página dentro
    /// de las pestañas) dejaría pasar sin preguntar la siguiente salida real.
    /// </summary>
    public void TerminarAbandono()
    {
        foreach (var aviso in _avisos.ToList())
            aviso.TerminarAbandono();
    }
}
