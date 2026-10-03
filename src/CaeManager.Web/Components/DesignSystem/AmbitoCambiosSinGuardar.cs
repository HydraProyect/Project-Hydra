namespace CaeManager.Web.Components.DesignSystem;

/// <summary>
/// P1-E2b: una zona de la interfaz que se desmonta sin navegar: el contenido de
/// <c>Pestanas</c> (las del Context Workspace, las de /documentos y cualquier otra) o la
/// ficha visible del Context Workspace (<see cref="NombreAmbitoFicha"/>). El
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
    /// <summary>
    /// Nombre de la cascada con la que el Context Workspace ofrece el ámbito de la ficha
    /// visible (abrir otra ficha, volver, el breadcrumb o cerrar el panel la desmontan sin
    /// navegar antes). Va con nombre, aparte del de <c>Pestanas</c>: un aviso fuera de las
    /// pestañas del panel sigue sin ámbito de pestañas, y cambiar de pestaña no le pregunta.
    /// </summary>
    public const string NombreAmbitoFicha = "AmbitoFichaWorkspace";

    private readonly List<AvisoCambiosSinGuardar> _avisos = [];
    private readonly AmbitoCambiosSinGuardar? _padre;

    public AmbitoCambiosSinGuardar(AmbitoCambiosSinGuardar? padre = null) => _padre = padre;

    private bool _abandonoEnCurso;

    /// <summary>
    /// Si este ámbito (o uno que lo contiene) está terminando un cambio ya confirmado con
    /// <see cref="ConfirmarAbandonoAsync"/>: la sincronización de la URL que lo acompaña
    /// (el ?ctx= del Context Workspace) no vuelve a preguntar a los avisos de dentro.
    /// </summary>
    internal bool EnAbandonoAutorizado => _abandonoEnCurso || (_padre?.EnAbandonoAutorizado ?? false);

    /// <summary>
    /// Ejecuta el cambio ya confirmado (cambiar de pestaña) marcando el abandono como
    /// autorizado mientras dura, con la navegación que lo acompañe.
    /// </summary>
    public async Task EjecutarAbandonoAutorizadoAsync(Func<Task> cambio)
    {
        _abandonoEnCurso = true;
        try
        {
            await cambio();
        }
        finally
        {
            _abandonoEnCurso = false;
        }
    }

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
        // Una pregunta por ámbito, no una por aviso (S12, lote 2b): pregunta el primero con cambios y, si se descarta, los demás
        // con cambios descartan lo suyo sin volver a preguntar. Antes cada aviso con cambios preguntaba por separado y un drawer
        // con kit dentro de una página con aviso propio hacía contestar dos veces.
        var conCambios = _avisos.ToList().Where(a => a.TieneCambios).ToList();
        if (conCambios.Count == 0)
            return true;

        if (!await conCambios[0].ConfirmarAbandonoAsync())
            return false;

        foreach (var otro in conCambios.Skip(1).Where(a => a.TieneCambios))
            await otro.DescartarSinPreguntarAsync();

        return true;
    }
}
