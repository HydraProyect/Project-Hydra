using CaeManager.Web.Components.DesignSystem;

namespace CaeManager.Web.Components.Workspace;

/// <summary>
/// Estado del Context Workspace (servicio scoped, mismo patrón que
/// ToastService/BusquedaGlobalService). Hay una única instancia por
/// circuito y un único &lt;ContextWorkspace&gt; montado en MainLayout que la
/// consume — esa es la garantía estructural de que nunca hay dos Workspaces
/// abiertos a la vez: no existe una segunda forma de abrir uno.
///
/// La pila (<see cref="Pila"/>) es a la vez el breadcrumb y el historial de
/// "volver": <see cref="AbrirAsync"/> la reemplaza entera (punto de entrada
/// desde una tabla/búsqueda), <see cref="NavegarAAsync"/> empuja un nivel
/// (clic en una entidad relacionada dentro de una pestaña — el panel
/// cambia de contenido internamente, nunca se abre uno nuevo).
/// </summary>
public class ContextWorkspaceService
{
    private readonly List<WorkspaceFrame> _pila = [];

    public event Action? OnCambio;

    public IReadOnlyList<WorkspaceFrame> Pila => _pila;
    public bool EstaAbierto => _pila.Count > 0;
    public WorkspaceFrame? FrameActual => _pila.Count > 0 ? _pila[^1] : null;

    public Task AbrirAsync(EntidadWorkspace tipo, Guid id, string tituloVisible, string pestanaInicial) =>
        CambiarPilaAsync([new WorkspaceFrame(tipo, id, tituloVisible, pestanaInicial)]);

    /// <summary>
    /// Empuja un nivel nuevo — o, si la entidad ya está en la pila (el
    /// usuario volvió a ella desde dos sitios distintos), trunca hasta ese
    /// nivel y lo reutiliza en vez de duplicarlo, para que el breadcrumb no
    /// crezca sin límite con ciclos A→B→A→B.
    /// </summary>
    public Task NavegarAAsync(EntidadWorkspace tipo, Guid id, string tituloVisible, string pestanaInicial)
    {
        var indiceExistente = _pila.FindIndex(f => f.Tipo == tipo && f.EntidadId == id);

        return indiceExistente >= 0
            ? CambiarPilaAsync(_pila.Take(indiceExistente + 1).ToList())
            : CambiarPilaAsync([.. _pila, new WorkspaceFrame(tipo, id, tituloVisible, pestanaInicial)]);
    }

    public Task VolverAsync() =>
        _pila.Count > 1 ? CambiarPilaAsync(_pila.Take(_pila.Count - 1).ToList()) : Task.CompletedTask;

    /// <summary>Clic en un cruce intermedio del breadcrumb — trunca hasta ese nivel (inclusive).</summary>
    public Task IrABreadcrumbAsync(int indice) =>
        indice >= 0 && indice < _pila.Count - 1 ? CambiarPilaAsync(_pila.Take(indice + 1).ToList()) : Task.CompletedTask;

    /// <summary>
    /// Cambia la pestaña activa del nivel actual (tope de pila) — no afecta al breadcrumb.
    /// No pregunta por cambios sin guardar: quien la llama es <c>Pestanas</c>, que ya lo
    /// hizo con su propio ámbito antes de cambiar.
    /// </summary>
    public Task CambiarPestanaAsync(string pestana)
    {
        if (_pila.Count == 0 || _pila[^1].PestanaActiva == pestana)
            return Task.CompletedTask;

        _pila[^1] = _pila[^1] with { PestanaActiva = pestana };
        OnCambio?.Invoke();
        return Task.CompletedTask;
    }

    public Task CerrarAsync() =>
        _pila.Count == 0 ? Task.CompletedTask : CambiarPilaAsync([]);

    /// <summary>
    /// P1-E2b: la ficha visible del Context Workspace, que se desmonta sin navegar. Lo fija el
    /// único <c>ContextWorkspace</c> montado; cada <c>AvisoCambiosSinGuardar</c> del panel se
    /// registra en él (en cascada con nombre <see cref="AmbitoCambiosSinGuardar.NombreAmbitoFicha"/>).
    /// </summary>
    public void EstablecerAmbitoFicha(AmbitoCambiosSinGuardar? ambito) => _ambitoFicha = ambito;

    private AmbitoCambiosSinGuardar? _ambitoFicha;

    /// <summary>
    /// P1-E2b: la URL ya cambió (atrás/adelante, un enlace con <c>?ctx=</c>, el menú) y el
    /// aviso de la ficha ya preguntó al navegar, si tenía algo que perder: el estado solo la
    /// sigue, sin volver a preguntar. <paramref name="frame"/> nulo cierra el panel.
    /// </summary>
    internal void SincronizarConUrl(WorkspaceFrame? frame)
    {
        if (frame is null && _pila.Count == 0)
            return;

        _pila.Clear();
        if (frame is not null)
            _pila.Add(frame);
        OnCambio?.Invoke();
    }

    /// <summary>
    /// P1-E2b: abrir otra ficha, volver, el breadcrumb o cerrar cambian la pila ANTES de que
    /// cambie la URL, así que el panel se desmonta antes de que su NavigationLock vea nada.
    /// Si la ficha visible cambia, se pregunta primero a su ámbito; «Seguir editando» deja la
    /// pila como estaba. La sincronización de <c>?ctx=</c> que acompaña al cambio ya
    /// confirmado no vuelve a preguntar (<see cref="AmbitoCambiosSinGuardar.EnAbandonoAutorizado"/>).
    /// </summary>
    private async Task CambiarPilaAsync(List<WorkspaceFrame> nuevaPila)
    {
        // La misma ficha (tipo e id: la @key del panel) no se desmonta aunque cambien el título,
        // que al restaurar desde la URL es un marcador, o la pestaña.
        var fichaCambia = FrameActual is { } actual
            && (nuevaPila.Count == 0
                || nuevaPila[^1].Tipo != actual.Tipo
                || nuevaPila[^1].EntidadId != actual.EntidadId);

        if (fichaCambia && _ambitoFicha is { } ambito)
        {
            if (!await ambito.ConfirmarAbandonoAsync())
                return;

            await ambito.EjecutarAbandonoAutorizadoAsync(() =>
            {
                Reemplazar(nuevaPila);
                return Task.CompletedTask;
            });
            return;
        }

        Reemplazar(nuevaPila);
    }

    private void Reemplazar(List<WorkspaceFrame> nuevaPila)
    {
        _pila.Clear();
        _pila.AddRange(nuevaPila);
        OnCambio?.Invoke();
    }

    /// <summary>
    /// Retira del Workspace las fichas de las entidades que acaban de darse de
    /// baja. El Workspace no es modal: la baja se confirma desde la lista que
    /// queda detrás, y sin esto la ficha seguiría enseñando (y dejando editar)
    /// algo ya eliminado. Se quita esa entidad de CUALQUIER nivel de la pila, no
    /// solo del actual: un antecesor eliminado seguiría siendo alcanzable con
    /// «Volver» o desde el breadcrumb. Las demás fichas se conservan, de modo
    /// que quien está mirando otra entidad no la pierde; si no queda ninguna,
    /// el Workspace se cierra.
    ///
    /// El tipo no basta para identificar la fila: Cliente empresarial, Empresa
    /// y Subcontrata son tres fichas del MISMO agregado <c>Empresa</c> y del
    /// mismo Guid (los tres comandos de baja cargan la misma fila), así que una
    /// baja por cualquiera de ellos retira las fichas de los tres tipos
    /// (<see cref="SonLaMismaFila"/>).
    /// </summary>
    public void RetirarSiEstaAbierto(EntidadWorkspace tipo, IReadOnlyCollection<Guid> idsEliminados)
    {
        if (_pila.RemoveAll(f => SonLaMismaFila(f.Tipo, tipo) && idsEliminados.Contains(f.EntidadId)) > 0)
            OnCambio?.Invoke();
    }

    private static bool SonLaMismaFila(EntidadWorkspace a, EntidadWorkspace b) =>
        a == b || (EsFichaDeEmpresa(a) && EsFichaDeEmpresa(b));

    private static bool EsFichaDeEmpresa(EntidadWorkspace tipo) =>
        tipo is EntidadWorkspace.Cliente or EntidadWorkspace.Empresa or EntidadWorkspace.Subcontrata;
}
