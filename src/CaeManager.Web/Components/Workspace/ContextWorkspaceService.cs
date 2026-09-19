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

    public Task AbrirAsync(EntidadWorkspace tipo, Guid id, string tituloVisible, string pestanaInicial)
    {
        _pila.Clear();
        _pila.Add(new WorkspaceFrame(tipo, id, tituloVisible, pestanaInicial));
        OnCambio?.Invoke();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Empuja un nivel nuevo — o, si la entidad ya está en la pila (el
    /// usuario volvió a ella desde dos sitios distintos), trunca hasta ese
    /// nivel y lo reutiliza en vez de duplicarlo, para que el breadcrumb no
    /// crezca sin límite con ciclos A→B→A→B.
    /// </summary>
    public Task NavegarAAsync(EntidadWorkspace tipo, Guid id, string tituloVisible, string pestanaInicial)
    {
        var indiceExistente = _pila.FindIndex(f => f.Tipo == tipo && f.EntidadId == id);

        if (indiceExistente >= 0)
            _pila.RemoveRange(indiceExistente + 1, _pila.Count - indiceExistente - 1);
        else
            _pila.Add(new WorkspaceFrame(tipo, id, tituloVisible, pestanaInicial));

        OnCambio?.Invoke();
        return Task.CompletedTask;
    }

    public Task VolverAsync()
    {
        if (_pila.Count > 1)
        {
            _pila.RemoveAt(_pila.Count - 1);
            OnCambio?.Invoke();
        }

        return Task.CompletedTask;
    }

    /// <summary>Clic en un cruce intermedio del breadcrumb — trunca hasta ese nivel (inclusive).</summary>
    public Task IrABreadcrumbAsync(int indice)
    {
        if (indice >= 0 && indice < _pila.Count - 1)
        {
            _pila.RemoveRange(indice + 1, _pila.Count - indice - 1);
            OnCambio?.Invoke();
        }

        return Task.CompletedTask;
    }

    /// <summary>Cambia la pestaña activa del nivel actual (tope de pila) — no afecta al breadcrumb.</summary>
    public Task CambiarPestanaAsync(string pestana)
    {
        if (_pila.Count == 0 || _pila[^1].PestanaActiva == pestana)
            return Task.CompletedTask;

        _pila[^1] = _pila[^1] with { PestanaActiva = pestana };
        OnCambio?.Invoke();
        return Task.CompletedTask;
    }

    public Task CerrarAsync()
    {
        if (_pila.Count == 0)
            return Task.CompletedTask;

        _pila.Clear();
        OnCambio?.Invoke();
        return Task.CompletedTask;
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
