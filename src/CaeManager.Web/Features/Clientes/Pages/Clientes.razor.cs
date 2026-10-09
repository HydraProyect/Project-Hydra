using System.Text.Json;
using CaeManager.Application.Clientes.Commands.CrearCliente;
using CaeManager.Application.Clientes.Commands.EditarCliente;
using CaeManager.Application.Clientes.Commands.EliminarClientes;
using CaeManager.Application.Clientes.Commands.ReasignarEjecutivoCliente;
using CaeManager.Application.Clientes.Commands.RestaurarCliente;
using CaeManager.Application.Clientes.Queries.ObtenerCentrosDeCliente;
using CaeManager.Application.Clientes.Queries.ObtenerClientePorId;
using CaeManager.Application.Clientes.Queries.ObtenerClientes;
using CaeManager.Application.Configuracion.Commands.EliminarFiltroGuardado;
using CaeManager.Application.Configuracion.Commands.GuardarFiltro;
using CaeManager.Application.Configuracion.Queries;
using CaeManager.Application.Documentos;
using CaeManager.Domain.Documentos;
using CaeManager.Infrastructure.Identity;
using CaeManager.Application.Common;
using CaeManager.Application.Tenants.Queries.ObtenerClientesAutorizados;
using CaeManager.Web.Components.Layout;
using CaeManager.Web.Components;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Components.Workspace;
using CaeManager.Web.Features.Documentos;
using CaeManager.Web.Features.Documentos.Recursos;
using CaeManager.Infrastructure.Autorizacion;
using FluentValidation;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.QuickGrid;

namespace CaeManager.Web.Features.Clientes.Pages;

/// <param name="Avatar">Clave del avatar elegido por esa persona (<c>CatalogoAvatares</c>), o <c>null</c>: iniciales.</param>
public record GestorCaeSelectorDto(Guid Id, string NombreCompleto, string Email, string? Avatar = null);

public partial class Clientes : CaeManager.Web.Components.PaginaInteractiva, IDisposable
{
    /// <summary>
    /// Se cancela al salir de la página: la resolución de la empresa activa que siga en vuelo deja de trabajar
    /// para nadie y su respuesta tardía no repinta un componente ya retirado.
    /// </summary>
    private readonly CancellationTokenSource _ciclo = new();
    private bool _desechado;

    // ── La fila se refresca tras guardar en la vista rápida ─────────────────────────────────────
    // El panel vive en MainLayout y guarda sin pasar por esta página: avisa por
    // ContextWorkspaceService.OnEntidadGuardada. Se vuelve a pedir SOLO esa fila y se sustituye
    // en sitio (mismo criterio que Centros.RefrescarCentroAsync): filtros, orden, página,
    // selección, fila enfocada y desplazamiento no se tocan, y la fila permanece aunque el
    // cambio la saque del filtro activo, hasta la siguiente carga. Los recuentos de la franja
    // tampoco se recalculan hasta entonces.

    /// <summary>
    /// La siguiente petición de QuickGrid se sirve de <see cref="_elementosPagina"/> sin consultar:
    /// QuickGrid solo repinta sus filas cuando su proveedor le entrega una página, y una carga
    /// de verdad limpiaría la selección y la fila enfocada. El total no cambia, así que no hay
    /// segunda petición (ver RecargarAsync).
    /// </summary>
    private bool _servirPaginaEnMemoria;

    private void AlGuardarEntidad(EntidadWorkspace tipo, Guid id)
    {
        if (tipo == EntidadWorkspace.Cliente)
            _ = InvokeAsync(() => RefrescarFilaAsync(id));
    }

    private async Task RefrescarFilaAsync(Guid id)
    {
        // Con una carga en vuelo no se sustituye nada: esa carga trae la página entera.
        if (_desechado || _grid is null || _cargando || !_elementosPagina.Any(e => e.Id == id))
            return;

        var carga = _cargaVigente;
        try
        {
            var resultado = await Mediator.Send(new ObtenerClientesQuery(Busqueda: null, SoloCriticos: null, ClienteId: id), _ciclo.Token);
            var indice = _elementosPagina.FindIndex(e => e.Id == id);
            if (_desechado || _grid is null || _cargando || carga != _cargaVigente || indice < 0
                || resultado.Elementos.FirstOrDefault() is not { } actualizada)
                return;

            _elementosPagina[indice] = actualizada;
            _servirPaginaEnMemoria = true;
            await _grid.RefreshDataAsync();
        }
        catch (Exception)
        {
            // El guardado ya es firme: que falle la relectura no es un error que enseñar. La
            // fila conserva el dato anterior hasta la siguiente carga, como antes de este aviso.
        }
        finally
        {
            _servirPaginaEnMemoria = false;
        }
    }

    public void Dispose()
    {
        if (_desechado)
            return;

        _desechado = true;
        WorkspaceService.OnCambio -= AlCambiarWorkspace;
        WorkspaceService.OnEntidadGuardada -= AlGuardarEntidad;
        _ciclo.Cancel();
        _ciclo.Dispose();
    }

    /// <summary>Quien mira no alcanza nada en este Tenant (<see cref="CaeManager.Web.Features.IncorporacionCartera.Components.VacioSegunAlcance"/>):
    /// sin «+ Nuevo» en cabecera, para no duplicar lo que quizá ya existe fuera de su cartera.</summary>
    private bool _alcanceCero;

    /// <summary>Estado 4a del mockup del selector: hay que elegir una empresa de la cartera antes de ver la lista.</summary>
    private bool _sinEmpresaSeleccionada;

    /// <summary>La empresa activa aún no se ha resuelto: se pinta una carga en vez de la lista.</summary>
    private bool _resolviendoEmpresa = true;

    [Inject] private ITenantActual TenantActual { get; set; } = default!;
    [Inject] private DirectorioUsuariosTenant DirectorioUsuarios { get; set; } = default!;
    [Inject] private AuthenticationStateProvider AuthenticationStateProvider { get; set; } = default!;
    [Inject] private IValidator<CrearClienteCommand> ValidadorCrear { get; set; } = default!;

    private static readonly string[] RolesQuePuedenReasignar =
        [Roles.Administrador, Roles.DireccionCae, Roles.CoordinadorCae];

    private const int TamanoPaginaMinimo = 20;
    private readonly PaginationState _paginacion = new() { ItemsPerPage = TamanoPaginaMinimo };
    private QuickGrid<ClienteListaDto>? _grid;

    // H2 (Project-Hydra-Negocio/tecnico/docs/ux-audit/02-clientes.md): el `Paginator` de QuickGrid no está
    // localizado — `PaginadorSimple` (mismo componente que el resto de listas
    // sin QuickGrid) cubre el copy en español; sigue delegando el movimiento
    // real de página en `_paginacion.SetCurrentPageIndexAsync` para que
    // QuickGrid pida los datos.
    private int TotalPaginas => Math.Max(1, (int)Math.Ceiling(_totalElementos / (double)_paginacion.ItemsPerPage));

    private Task CambiarPaginaAsync(int pagina) => _paginacion.SetCurrentPageIndexAsync(pagina - 1);

    // H5 (Project-Hydra-Negocio/tecnico/docs/ux-audit/05-trabajadores-vehiculos.md): selector de tamaño de página, compartido por PaginadorSimple.razor.
    // Una sola petición: SetCurrentPageIndexAsync ya avisa a QuickGrid aunque la
    // página no cambie, así que refrescar además la rejilla pedía lo mismo dos
    // veces (ver RecargarAsync).
    private Task CambiarTamanoPaginaAsync(int tamano)
    {
        _paginacion.ItemsPerPage = tamano;
        return _paginacion.SetCurrentPageIndexAsync(0);
    }

    private bool _puedeReasignarEjecutivo;
    private IReadOnlyList<GestorCaeSelectorDto> _gestoresDisponibles = [];
    private string _ejecutivoUsuarioId = string.Empty;
    private string _ejecutivoUsuarioIdOriginal = string.Empty;

    private string _busqueda = string.Empty;
    private bool _soloCriticos;
    private string _ejecutivoFiltro = string.Empty;
    private string _estadoDocumentalFiltro = string.Empty;

    /// <summary>Clientes empresariales por estado para la franja, sin el filtro de estado aplicado. <c>null</c> hasta la primera carga.</summary>
    private IReadOnlyDictionary<string, int>? _recuentosPorEstado;

    /// <summary>Cifra de «Todos» en la franja: aquí los recuentos por estado se solapan y no suman el total.</summary>
    private int? _totalSinFiltroDeEstado;
    private IReadOnlyList<GestorCaeSelectorDto> _ejecutivosParaFiltro = [];
    private bool _cargando = true;
    private bool _errorCarga;
    private int _totalElementos;

    /// <summary>
    /// Número de la última carga de la lista. Cada carga captura el suyo ANTES
    /// del <c>await</c> y, al volver, solo escribe estado si sigue siendo la
    /// vigente: si mientras tanto cambió un filtro, la página, el tamaño o el
    /// orden, su respuesta es de otra pregunta. QuickGrid ya descarta las FILAS
    /// de una carga superada, pero no sabe nada de <see cref="_totalElementos"/>,
    /// <see cref="_elementosPagina"/> (de la que tiran j/k/x y la selección) ni
    /// de <see cref="_errorCarga"/>: sin esto, una respuesta lenta del filtro
    /// anterior pisaba el total del nuevo.
    /// </summary>
    private int _cargaVigente;

    /// <summary>
    /// Mismo criterio para el formulario de edición: abrir «Editar» sobre A y
    /// en seguida sobre B no puede acabar con el formulario de B relleno con
    /// los datos de A porque la consulta de A llegó la última.
    /// </summary>
    private int _edicionVigente;

    private bool _drawerVisible;
    private Guid? _editandoId;
    private Guid _versionEditando;
    private string _razonSocial = string.Empty;
    private string _cif = string.Empty;
    private bool _esCritico;
    private string _notas = string.Empty;
    private bool _guardando;
    private string? _mensajeErrorFormulario;
    private Dictionary<string, string> _erroresCampo = new();

    /// <summary>Valor del select «Criticidad» que equivale a «Solo críticos».</summary>
    private const string ValorSoloCriticos = "critico";

    /// <summary>Cliente empresarial cuyo panel está abierto arriba de la pila del Context Workspace, si lo hay.</summary>
    private Guid? FichaAbierta =>
        WorkspaceService.FrameActual is { Tipo: EntidadWorkspace.Cliente } frame ? frame.EntidadId : null;

    private void AlCambiarWorkspace() => InvokeAsync(StateHasChanged);

    // El nombre de la fila, un clic en un punto sin controles de la fila (oyente delegado de
    // atajos-lista.js) o Enter sobre la fila enfocada abren la vista rápida: el panel de 520 px
    // del Context Workspace, el mismo que abren los botones 360 del resto de pantallas. A la
    // página Cliente 360 (/clientes/{id}) se va con el icono 360 de la fila.
    private Task AbrirVistaRapidaAsync(Guid id) =>
        WorkspaceService.AbrirAsync(EntidadWorkspace.Cliente, id, NombreDe(id), "informacion");

    /// <summary>
    /// Tecla «e»: la vista rápida de la fila enfocada, ya en edición (el lápiz de la cabecera
    /// del panel). Si el rol no puede escribir, el panel se abre y se queda en lectura.
    /// </summary>
    private Task AbrirVistaRapidaEnEdicionAsync(Guid id) =>
        WorkspaceService.AbrirEnEdicionAsync(EntidadWorkspace.Cliente, id, NombreDe(id));

    // El Cliente empresarial del panel puede no estar en la página (el filtro lo dejó fuera):
    // su nombre es entonces el del frame abierto.
    private string NombreDe(Guid id) =>
        _elementosPagina.FirstOrDefault(e => e.Id == id)?.RazonSocial
        ?? (WorkspaceService.FrameActual is { } frame && frame.EntidadId == id ? frame.TituloVisible : string.Empty);

    /// <summary>Cuántos centros enseña como mucho la ventana del recuento; el resto se ve en la pestaña «Centros» del panel.</summary>
    private const int MaximoCentrosEnVentana = 8;

    /// <summary>
    /// Centros ya pedidos para la ventana de contexto del recuento de una fila. La fila solo trae
    /// el número (<see cref="ClienteListaDto.Centros"/>): los nombres se piden por fila, la
    /// primera vez que el puntero o el foco llegan a su recuento, y se olvidan con cada carga de
    /// la lista para no enseñar centros de antes de un cambio.
    /// </summary>
    private readonly Dictionary<Guid, IReadOnlyList<CentroDeClienteDto>> _centrosDeFila = [];
    private readonly HashSet<Guid> _centrosDeFilaEnVuelo = [];
    private readonly HashSet<Guid> _centrosDeFilaConError = [];

    /// <summary>Sube con cada carga de la lista: una respuesta de centros pedida antes no escribe en la página nueva.</summary>
    private int _generacionCentrosDeFila;

    private async Task CargarCentrosDeFilaAsync(Guid id)
    {
        if (_desechado || _centrosDeFila.ContainsKey(id) || !_centrosDeFilaEnVuelo.Add(id))
            return;

        var generacion = _generacionCentrosDeFila;
        _centrosDeFilaConError.Remove(id);

        try
        {
            var centros = await Mediator.Send(new ObtenerCentrosDeClienteQuery(id), _ciclo.Token);
            if (generacion == _generacionCentrosDeFila)
                _centrosDeFila[id] = centros;
        }
        catch (Exception)
        {
            // Un fallo se dice en la ventana y deja reintentar al volver a pasar por el recuento.
            if (generacion == _generacionCentrosDeFila)
                _centrosDeFilaConError.Add(id);
        }
        finally
        {
            if (generacion == _generacionCentrosDeFila)
                _centrosDeFilaEnVuelo.Remove(id);
        }
    }

    private void OlvidarCentrosDeFila()
    {
        _generacionCentrosDeFila++;
        _centrosDeFila.Clear();
        _centrosDeFilaEnVuelo.Clear();
        _centrosDeFilaConError.Clear();
    }

    /// <summary>Pie de la ventana cuando no caben todos: cuántos centros quedan fuera y dónde se ven.</summary>
    private string? PieDeCentrosDeFila(Guid id) =>
        _centrosDeFila.TryGetValue(id, out var centros) && centros.Count > MaximoCentrosEnVentana
            ? Textos["CentrosDeFilaPieMas", centros.Count - MaximoCentrosEnVentana].Value
            : null;

    /// <summary>Pulsar un centro de la ventana: el panel del Cliente empresarial en su pestaña «Centros».</summary>
    private Task AbrirCentrosDeFilaAsync(Guid id) =>
        WorkspaceService.AbrirAsync(EntidadWorkspace.Cliente, id, NombreDe(id), "centros");

    private void AbrirGuardarFiltro()
    {
        _mensajeErrorFiltro = null;
        _mostrarGuardarFiltro = true;
    }

    private IReadOnlyList<OpcionEstado> OpcionesFiltrosGuardados =>
        _filtrosGuardados.Select(f => new OpcionEstado(f.Id.ToString(), f.Nombre)).ToList();

    [SupplyParameterFromQuery(Name = "q")]
    public string? TerminoBusquedaInicial { get; set; }

    /// <summary>H4 (Project-Hydra-Negocio/tecnico/docs/ux-audit/02-clientes.md): antes solo `q` viajaba en la URL, `soloCriticos` se perdía al recargar o compartir el enlace.</summary>
    [SupplyParameterFromQuery(Name = "critico")]
    public bool? SoloCriticosInicial { get; set; }

    /// <summary>Gestor CAE por el que se filtra la lista (Id de usuario). Viaja en la URL como el resto de filtros.</summary>
    [SupplyParameterFromQuery(Name = "gestor")]
    public string? GestorCaeInicial { get; set; }

    /// <summary>Estados marcados en la franja de estado: nombres de <see cref="EstadoDocumento"/> separados por coma.</summary>
    [SupplyParameterFromQuery(Name = "estado")]
    public string? EstadoDocumentalInicial { get; set; }

    [Inject] private NavigationManager NavigationManager { get; set; } = default!;

    /// <summary>
    /// Acción pedida por URL: <c>crear</c> (palette "Crear cliente" y el atajo
    /// global «n») abre el Drawer de alta; <c>guardar-filtro</c> (palette)
    /// abre el modal de guardar filtro. Ver <see cref="OnParametersSetAsync"/>.
    /// </summary>
    [SupplyParameterFromQuery] public string? Accion { get; set; }

    /// <summary>
    /// Última <see cref="Accion"/> ya atendida. La acción se ejecuta al CAMBIAR,
    /// no en cada pasada de parámetros: la URL la conserva mientras se trabaja
    /// (cada filtro que se escribe en la URL preserva los demás parámetros), y
    /// atenderla en cada pasada reabría el Drawer o el modal al teclear en el
    /// buscador después de cerrarlos.
    /// </summary>
    private string? _accionAtendida;

    private GridItemsProvider<ClienteListaDto>? _proveedorElementos;

    // --- P3-31: selección múltiple, atajos j/k, filtros guardados ---
    private readonly HashSet<Guid> _seleccionados = [];

    /// <summary>
    /// Los checkboxes de fila solo se pintan con esto activo (Centro 360,
    /// Project-Hydra-Negocio/tecnico/docs/ux-audit/PLAN-EJECUCION-UX.md § 0.9) — son ruido permanente para una acción
    /// ocasional. Apagarlo limpia la selección: dejar filas marcadas que ya
    /// no se ven dejaría la barra de acciones en lote apuntando a algo
    /// invisible.
    /// </summary>
    private bool _seleccionMultiple;

    private void AlternarSeleccionMultiple(bool activa)
    {
        _seleccionMultiple = activa;
        if (!activa)
            _seleccionados.Clear();
    }
    private List<ClienteListaDto> _elementosPagina = [];
    private Guid? _idEnfocado;
    private bool _eliminandoLote;
    private bool _confirmarEliminarLoteVisible;

    private IReadOnlyList<FiltroGuardadoDto> _filtrosGuardados = [];
    private bool _mostrarGuardarFiltro;
    private string _nombreFiltroNuevo = string.Empty;
    private bool _guardandoFiltro;

    /// <summary>Error del servidor al guardar el filtro: se ve en el aviso fijo del ModalFormulario (D-20), no en un toast que desaparece.</summary>
    private string? _mensajeErrorFiltro;
    private FiltroGuardadoDto? _filtroGuardadoAEliminar;
    private bool _eliminandoFiltroGuardado;

    /// <summary>
    /// Lo que se guarda de un filtro. Hasta ahora solo viajaban la búsqueda y
    /// «solo críticos»: guardar «Con vencidos» o un ejecutivo concreto
    /// devolvía, al aplicarlo, una lista sin ese filtro. Se escribe SIEMPRE con
    /// los cuatro ejes (aunque alguno vaya a null): así un filtro nuevo declara
    /// los cuatro y, al aplicarlo, los fija todos. Solo se usa para escribir;
    /// la lectura va por <see cref="LeerFiltroGuardado"/>, que distingue la
    /// clave ausente de la clave con null.
    ///
    /// <para>
    /// El campo nuevo se llama <c>GestorCaeId</c> y no como la columna
    /// (<c>EjecutivoUsuarioId</c>, deuda terminológica congelada por
    /// TerminologiaCanonicaTests): nombra a la persona, el Gestor CAE de
    /// referencia del Cliente empresarial (no concede alcance). Al ser una clave
    /// nueva del JSON guardado, no hay filtros antiguos que la lleven.
    /// </para>
    /// </summary>
    private record FiltrosClientesJson(
        string? Busqueda, bool SoloCriticos, string? GestorCaeId = null, string? EstadoDocumental = null);

    protected override async Task OnInitializedAsync()
    {
        // «e» sin fila enfocada edita la ficha abierta: la página se entera de cuál es.
        WorkspaceService.OnCambio += AlCambiarWorkspace;
        WorkspaceService.OnEntidadGuardada += AlGuardarEntidad;

        // Hasta resolver la empresa activa no se monta la lista ni sus acciones: con la consulta en
        // vuelo el render saldría con «hay empresa» y lanzaría la carga del Tenant de origen.
        try
        {
            var contexto = await ContextoEmpresaActiva.ResolverAsync(Mediator, TenantActual, _ciclo.Token);
            _sinEmpresaSeleccionada = contexto.SinSeleccion;
        }
        catch (OperationCanceledException) when (_ciclo.IsCancellationRequested)
        {
            // La página se retiró con la resolución en vuelo: no queda nadie a quien pintar.
            return;
        }
        finally
        {
            _resolviendoEmpresa = false;
        }

        // Retirada la página, una resolución que vuelva sin lanzar (contexto «Ninguno») no es 4a: no hay
        // a quién pintarle la lista, y seguir pediría los datos de una página que ya no existe.
        if (_desechado)
            return;

        // Sin empresa elegida no se piden los datos de la organización de origen.
        if (_sinEmpresaSeleccionada)
            return;

        // Delegado estable: pasar el grupo de método directamente en el
        // markup crea un delegado nuevo en cada render, QuickGrid lo trata
        // como "fuente de datos distinta" y recarga — combinado con el
        // StateHasChanged del proveedor, bucle infinito de recargas (visto
        // con PostgreSQL, donde el proveedor es asíncrono de verdad).
        _proveedorElementos = ProveerElementosAsync;

        // Reasignar el Gestor CAE dueño de un Cliente es una decisión de
        // rango superior (ver ReasignarEjecutivoClienteCommand) — el propio
        // Gestor CAE no ve ni puede tocar este campo sobre sus propios
        // clientes.
        var estadoAutenticacion = await AuthenticationStateProvider.GetAuthenticationStateAsync();
        _puedeReasignarEjecutivo = RolesQuePuedenReasignar.Any(estadoAutenticacion.User.IsInRole);

        _filtrosGuardados = await Mediator.Send(new ObtenerFiltrosGuardadosQuery(PantallasConFiltrosGuardados.Clientes));

        // Mismo directorio que ya usa el selector de reasignación del Drawer
        // (ver AbrirEditarAsync) — el filtro "Ejecutivo" del mockup ("Lista
        // Clientes TALVEG") pregunta lo mismo, así que no hace falta una
        // segunda consulta ni un query nuevo.
        var gestores = await DirectorioUsuarios.ObtenerVisiblesEnRolAsync(Roles.GestorCae);
        _ejecutivosParaFiltro = gestores
            .Select(u => new GestorCaeSelectorDto(u.Id, u.NombreCompleto, u.Email ?? string.Empty, u.Avatar))
            .OrderBy(g => g.NombreCompleto)
            .ToList();
    }

    /// <summary>
    /// Se re-ejecuta en cada navegación dentro de la propia página (recargar,
    /// compartir la URL, volver atrás) — no solo en el primer render — para
    /// que el filtro de la URL sea la fuente de verdad, no solo su semilla
    /// inicial (P1-18 de Project-Hydra-Negocio/MATURITY_REVIEW.md). Permite además
    /// que el buscador global (Ctrl/Cmd+K) navegue aquí con el filtro ya
    /// cargado, p. ej. /clientes?q=Cadena+Industrial.
    ///
    /// H4 (Project-Hydra-Negocio/tecnico/docs/ux-audit/02-clientes.md): la navegación mejorada de Blazor
    /// reutiliza esta instancia de componente entre URLs (no la recrea desde
    /// cero), así que un cambio de filtro que llega solo por la URL —abrir
    /// un enlace compartido, "atrás/adelante"— actualizaba estos campos pero
    /// nunca refrescaba el QuickGrid ya montado (que solo vuelve a pedir
    /// datos cuando algo llama a RefreshDataAsync explícitamente). `_grid`
    /// es null únicamente en el primer render, cuando QuickGrid todavía va a
    /// hacer su propia primera carga con estos campos ya al día — por eso el
    /// refresco explícito solo hace falta, y solo se dispara, en los
    /// siguientes.
    /// </summary>
    protected override Task OnParametersSetAsync()
    {
        // Retirada la página con la resolución en vuelo, ComponentBase aún invoca esto: no se procesan
        // parámetros ni acciones de URL de un componente que ya no existe.
        if (_desechado || _resolviendoEmpresa || _sinEmpresaSeleccionada)
            return Task.CompletedTask;

        var deLaUrl = TerminoBusquedaInicial ?? string.Empty;
        var soloCriticosDeLaUrl = SoloCriticosInicial ?? false;
        // Lo que no sea un estado conocido, o un Gestor CAE que el directorio visible de este
        // usuario no ofrece, es «sin filtro»: un Id en la URL no es autoridad, y la pantalla
        // no filtra por alguien a quien no puede nombrar (misma regla que el filtro guardado).
        var gestorDeLaUrl = Guid.TryParse(GestorCaeInicial, out var gestorId) && _ejecutivosParaFiltro.Any(g => g.Id == gestorId)
            ? gestorId.ToString()
            : string.Empty;
        var estadoDeLaUrl = EstadosValidos(EstadoDocumentalInicial);
        var cambio = deLaUrl != _busqueda || soloCriticosDeLaUrl != _soloCriticos
            || gestorDeLaUrl != _ejecutivoFiltro || estadoDeLaUrl != _estadoDocumentalFiltro;

        _busqueda = deLaUrl;
        _soloCriticos = soloCriticosDeLaUrl;
        _ejecutivoFiltro = gestorDeLaUrl;
        _estadoDocumentalFiltro = estadoDeLaUrl;

        // Las dos acciones por URL se atienden aquí y no en OnInitializedAsync:
        // ese solo corre al montar, y tanto el atajo global «n» como el
        // palette navegan a /clientes?accion=… estando YA en /clientes, sin
        // recrear el componente. Antes «crear» vivía en OnInitializedAsync y,
        // desde la propia lista, «n» cambiaba la URL sin abrir nada. Se
        // ejecutan después de resincronizar los filtros, así que el modal de
        // guardar filtro parte de los ya vigentes en pantalla.
        if (Accion != _accionAtendida)
        {
            _accionAtendida = Accion;
            if (Accion == "crear")
                AbrirCrear();
            else if (Accion == "guardar-filtro")
                _mostrarGuardarFiltro = true;
        }

        return cambio && _grid is not null ? RecargarAsync() : Task.CompletedTask;
    }

    /// <summary>
    /// Al cerrar lo que abrió una acción por URL se quita esa acción de la
    /// URL: si no, volver a pulsar «n» navegaría a la misma URL, la acción no
    /// cambiaría y no se abriría nada. Solo desde manejadores de eventos
    /// (sesión interactiva), nunca durante el prerender.
    /// </summary>
    private void QuitarAccionDeLaUrl()
    {
        if (!string.IsNullOrEmpty(Accion))
            NavigationManager.ActualizarFiltroEnUrl("accion", null);
    }

    private async ValueTask<GridItemsProviderResult<ClienteListaDto>> ProveerElementosAsync(
        GridItemsProviderRequest<ClienteListaDto> request)
    {
        if (_servirPaginaEnMemoria)
        {
            // Refresco de una fila tras guardar en la vista rápida (ver RefrescarFilaAsync).
            _servirPaginaEnMemoria = false;
            return GridItemsProviderResult.From(_elementosPagina.ToList(), _totalElementos);
        }

        // Todo lo que define la pregunta se lee ANTES del await.
        var carga = ++_cargaVigente;
        var (ordenarPor, descendente) = LecturaOrden.Leer(request);
        (_ordenExportar, _descendenteExportar) = (ordenarPor, descendente);
        var estadosFiltro = SeleccionEstados.Separar<EstadoDocumento>(_estadoDocumentalFiltro);
        var consulta = new ObtenerClientesQuery(
            Busqueda: string.IsNullOrWhiteSpace(_busqueda) ? null : _busqueda,
            SoloCriticos: _soloCriticos ? true : null,
            EjecutivoUsuarioId: Guid.TryParse(_ejecutivoFiltro, out var ejecutivoId) ? ejecutivoId : null,
            EstadoDocumental: null,
            Pagina: (request.StartIndex / _paginacion.ItemsPerPage) + 1,
            TamanoPagina: _paginacion.ItemsPerPage,
            OrdenarPor: ordenarPor,
            Descendente: descendente,
            EstadosDocumentales: estadosFiltro.Count == 0 ? null : estadosFiltro,
            ConRecuentosPorEstado: true);

        _cargando = true;
        _errorCarga = false;

        try
        {
            var resultado = await Mediator.Send(consulta, request.CancellationToken);

            if (carga != _cargaVigente)
                return GridItemsProviderResult.From(new List<ClienteListaDto>(), 0);

            _totalElementos = resultado.TotalElementos;
            _recuentosPorEstado = resultado.RecuentosPorEstado;
            _totalSinFiltroDeEstado = resultado.TotalSinFiltroDeEstado;

            var elementos = resultado.Elementos.ToList();
            _elementosPagina = elementos;
            _seleccionados.Clear();
            _idEnfocado = null;
            OlvidarCentrosDeFila();

            return GridItemsProviderResult.From(elementos, resultado.TotalElementos);
        }
        catch (Exception) when (carga != _cargaVigente)
        {
            // Una carga superada que falla (o que QuickGrid canceló) no es un
            // error de la vigente: no puede tapar su resultado.
            return GridItemsProviderResult.From(new List<ClienteListaDto>(), 0);
        }
        catch (Exception)
        {
            _errorCarga = true;
            return GridItemsProviderResult.From(new List<ClienteListaDto>(), 0);
        }
        finally
        {
            if (carga == _cargaVigente)
            {
                _cargando = false;
                StateHasChanged();
            }
        }
    }

    private async Task BuscarAsync(string valor)
    {
        _busqueda = valor;
        NavigationManager.ActualizarFiltroEnUrl("q", valor);
        await RecargarAsync();
    }

    private async Task CambiarSoloCriticosAsync(bool valor)
    {
        _soloCriticos = valor;
        NavigationManager.ActualizarFiltroEnUrl("critico", valor ? "true" : null);
        await RecargarAsync();
    }

    /// <summary>Filtros «Gestor CAE» y «Estado documental» del mockup "Lista Clientes TALVEG" — mismo patrón que SoloCriticos: estado local, URL (<c>?gestor=</c>, <c>?estado=</c>) y recarga.</summary>
    private async Task CambiarEjecutivoFiltroAsync(string valor)
    {
        _ejecutivoFiltro = valor;
        NavigationManager.ActualizarFiltroEnUrl("gestor", valor);
        await RecargarAsync();
    }

    private async Task CambiarEstadoDocumentalFiltroAsync(string? valor)
    {
        _estadoDocumentalFiltro = valor ?? string.Empty;
        NavigationManager.ActualizarFiltroEnUrl("estado", valor);
        await RecargarAsync();
    }

    // --- H4 (Project-Hydra-Negocio/tecnico/docs/ux-audit/02-clientes.md): chips de filtros activos ---

    private bool HayFiltrosActivos =>
        !string.IsNullOrWhiteSpace(_busqueda) || _soloCriticos
        || !string.IsNullOrWhiteSpace(_ejecutivoFiltro) || !string.IsNullOrWhiteSpace(_estadoDocumentalFiltro);

    private Task QuitarFiltroBusquedaAsync() => BuscarAsync(string.Empty);

    private Task QuitarFiltroCriticosAsync() => CambiarSoloCriticosAsync(false);

    private Task QuitarFiltroEjecutivoAsync() => CambiarEjecutivoFiltroAsync(string.Empty);

    private string EtiquetaFiltroBusqueda => "Búsqueda: \"" + _busqueda + "\"";

    private string EtiquetaFiltroEjecutivo =>
        "Gestor CAE: " + (_ejecutivosParaFiltro.FirstOrDefault(g => g.Id.ToString() == _ejecutivoFiltro)?.NombreCompleto ?? "—");

    /// <summary>
    /// Gestor CAE de referencia de la fila (columna «Gestor CAE», con su avatar o sus iniciales
    /// delante) — mismo directorio que ya resuelve el filtro, sin consulta nueva por fila. Null
    /// sin Gestor CAE asignado o si no está en el directorio visible: la celda pinta «—».
    /// </summary>
    private GestorCaeSelectorDto? GestorCaeDeLaFila(Guid? ejecutivoUsuarioId) =>
        ejecutivoUsuarioId is null
            ? null
            : _ejecutivosParaFiltro.FirstOrDefault(g => g.Id == ejecutivoUsuarioId);

    /// <summary>
    /// Nombre accesible del botón de la celda «Gestor CAE»: empieza por lo que se ve en ella
    /// (el Gestor CAE o «Sin asignar») y después dice qué hace y sobre qué Cliente empresarial.
    /// </summary>
    private string NombreAccesibleCambiarGestorCae(string razonSocial, GestorCaeSelectorDto? asignado) =>
        Textos["CambiarGestorCaeDe", razonSocial, asignado?.NombreCompleto ?? Textos["GestorCaeSinAsignar"].Value];

    /// <summary>Opciones de la pastilla «Gestor CAE»: el mismo directorio que la columna.</summary>
    private IReadOnlyList<OpcionEstado> OpcionesGestorCae =>
        _ejecutivosParaFiltro.Select(g => new OpcionEstado(g.Id.ToString(), g.NombreCompleto)).ToList();

    /// <summary>
    /// Botones de la franja de estado, de peor a mejor. Viajan como nombre de <see cref="EstadoDocumento"/>;
    /// «Sin incidencias» es el centinela <see cref="EstadoDocumentalFiltro.AlCorriente"/> (ninguna alerta abierta)
    /// y «Por vencer» marca Urgente y Próximo, con la cifra conjunta de <see cref="ObtenerClientesQuery.ClavePorVencer"/>.
    /// </summary>
    private static IReadOnlyList<OpcionFranjaEstado> FranjaEstadoDocumental =>
    [
        new(TextosVigenciaDocumento.Texto("FranjaVencidos"), TonoBadge.Peligro, nameof(EstadoDocumento.Vencido)),
        new(TextosVigenciaDocumento.Texto("FranjaPendientes"), TonoBadge.Peligro, nameof(EstadoDocumento.Faltante)),
        new(EstadoDocumentoUi.PorVencer, TonoBadge.Advertencia, nameof(EstadoDocumento.Urgente), nameof(EstadoDocumento.Proximo)),
        new(EstadoDocumentoUi.SinIncidencias, TonoBadge.Exito, EstadoDocumentalFiltro.AlCorriente)
    ];

    /// <summary>
    /// La selección de estados que llega de fuera (la URL, un filtro guardado) reducida a los que algún botón
    /// de la franja conoce; lo demás se descarta. Cadena vacía si no queda ninguno.
    /// </summary>
    private static string EstadosValidos(string? seleccion)
    {
        var conocidos = FranjaEstadoDocumental.SelectMany(o => o.Valores).ToHashSet(StringComparer.Ordinal);
        return SeleccionEstados.Unir(SeleccionEstados.Separar(seleccion).Where(conocidos.Contains)) ?? string.Empty;
    }

    /// <summary>Opciones de la pastilla «Criticidad» (sigue viajando en la URL como <c>critico</c>).</summary>
    private IReadOnlyList<OpcionEstado> OpcionesCriticidad => [new(ValorSoloCriticos, Textos["ListaFiltroSoloCriticos"])];

    /// <summary>
    /// Cuántos coinciden (mockup: «N clientes con el filtro actual»). Sin
    /// filtros no habla de ninguno; con filtros dice que el número es el de
    /// los que coinciden, no el de la cartera.
    /// </summary>
    private string TextoConteo
    {
        get
        {
            var sustantivo = _totalElementos == 1 ? "Cliente empresarial" : "Clientes empresariales";
            return HayFiltrosActivos
                ? $"{_totalElementos} {sustantivo} con estos filtros"
                : $"{_totalElementos} {sustantivo}";
        }
    }

    private string TextoAvisoSeleccionPagina
    {
        get
        {
            var ambito = HayFiltrosActivos ? "con estos filtros" : "en total";
            return $"Los {_elementosPagina.Count} de esta página están seleccionados. Hay {_totalElementos} {ambito}: los de otras páginas no entran en la selección.";
        }
    }

    /// <summary>
    /// Motivo bajo la pastilla: cuántas alertas hay en el peor estado («12 documentos»), sin repetir el estado,
    /// que ya lo dice la pastilla.
    /// </summary>
    private string MotivoEstadoDocumental(int cantidad) =>
        cantidad == 1 ? Textos["MotivoUnDocumento"].Value : Textos["MotivoDocumentos", cantidad].Value;

    /// <summary>
    /// Lo que de verdad cuenta el agregado de ObtenerClientesQuery: las
    /// alertas de vigencia de los trabajadores cuyo cliente principal es este,
    /// en su peor estado. El mockup dice «entre los trabajadores y centros»;
    /// los centros no entran en ese agregado, así que no se nombran.
    /// </summary>
    private static string TituloEstadoDocumental(EstadoDocumento peor) =>
        $"Peor estado entre las alertas de vigencia abiertas de sus trabajadores: {EstadoDocumentoUi.Texto(peor).ToLowerInvariant()}";

    /// <summary>
    /// Quita los cuatro filtros en una sola recarga. Encadenar los setters
    /// lanzaría cuatro consultas y las tres primeras devolverían listas que ya
    /// no se van a pintar. Lo usan «Quitar los filtros» del estado vacío y
    /// «Limpiar todo» de la tarjeta de filtros.
    ///
    /// <para>
    /// <b>Los cuatro filtros que viajan por la URL (<c>q</c>, <c>critico</c>, <c>gestor</c>, <c>estado</c>) se limpian TAMBIÉN allí.</b>
    /// Hasta ahora solo se borraba <c>q</c>: <c>critico</c> se quedaba puesto y
    /// <see cref="OnParametersSetAsync"/>, que re-sincroniza desde la URL, lo
    /// devolvía a true en la siguiente pasada de parámetros. El resultado era
    /// que pulsar "Quitar los filtros" con "solo críticos" activo dejaba la
    /// lista igual de recortada y el chip volvía a aparecer — el chip sí lo
    /// limpiaba bien (ver <see cref="CambiarSoloCriticosAsync"/>), el botón no.
    /// Lo destapó la prueba por render; el trinquete de fuente lo daba por
    /// bueno, porque solo mira que exista la rama.
    /// </para>
    ///
    /// <para>
    /// Se usa <c>ActualizarFiltrosEnUrl</c> —los dos de una vez— y no dos
    /// llamadas seguidas, por la razón que documenta el propio helper: cada
    /// <c>NavigateTo</c> lee la URL vigente y dos seguidas pueden pisarse.
    /// </para>
    /// </summary>
    private async Task LimpiarFiltrosAsync()
    {
        _busqueda = string.Empty;
        _soloCriticos = false;
        _ejecutivoFiltro = string.Empty;
        _estadoDocumentalFiltro = string.Empty;
        NavigationManager.ActualizarFiltrosEnUrl(new Dictionary<string, string?>
        {
            ["q"] = null,
            ["critico"] = null,
            ["gestor"] = null,
            ["estado"] = null,
        });
        await RecargarAsync();
    }

    /// <summary>
    /// Vuelve a la página 1 y pide la lista UNA vez.
    /// <see cref="PaginationState.SetCurrentPageIndexAsync"/> NO lleva guarda de
    /// igualdad: asigna el índice e invoca <c>CurrentPageItemsChanged</c> siempre,
    /// cambie o no la página, y QuickGrid tiene ahí suscrito su
    /// <c>RefreshDataCoreAsync</c> — avisar a la paginación ya es pedir los datos.
    /// (Su hermano <c>SetTotalItemCountAsync</c> sí compara antes de disparar; la
    /// asimetría es del componente.) Un comentario anterior aquí afirmaba lo
    /// contrario y justificaba llamar también a <c>RefreshDataAsync</c>: eso
    /// costaba dos consultas idénticas por búsqueda o filtro aunque el total no
    /// cambiara, medido en <c>ClientesListaGen2Tests</c>.
    ///
    /// <para>
    /// Se refresca por la referencia cuando ya estamos en la página 0 porque con
    /// el error a la vista la rejilla no está montada y nadie escucha el aviso de
    /// la paginación; la referencia vieja sí sigue sirviendo para volver a pedir.
    /// </para>
    /// </summary>
    private async Task RecargarAsync()
    {
        if (_grid is not null && _paginacion.CurrentPageIndex == 0)
            await _grid.RefreshDataAsync();
        else
            await _paginacion.SetCurrentPageIndexAsync(0);

        StateHasChanged();
    }

    private void AbrirCrear()
    {
        // Un «Editar» cuya consulta siga en vuelo no puede rellenar este
        // formulario de alta al volver.
        _edicionVigente++;
        _editandoId = null;
        _razonSocial = string.Empty;
        _cif = string.Empty;
        _esCritico = false;
        _notas = string.Empty;
        _ejecutivoUsuarioId = string.Empty;
        _ejecutivoUsuarioIdOriginal = string.Empty;
        _erroresCampo = new Dictionary<string, string>();
        _mensajeErrorFormulario = null;
        _drawerVisible = true;
        FijarInstantaneaFormulario();
    }

    private async Task AbrirEditarAsync(Guid id)
    {
        var edicion = ++_edicionVigente;

        var cliente = await Mediator.Send(new ObtenerClientePorIdQuery(id));
        if (edicion != _edicionVigente)
            return;

        if (cliente is null)
        {
            ToastService.Mostrar("No encontramos este Cliente empresarial. Puede que ya se haya eliminado.", TonoToast.Error);
            await RecargarAsync();
            return;
        }

        if (_puedeReasignarEjecutivo && _gestoresDisponibles.Count == 0)
        {
            // Acotado al tenant activo — ver DirectorioUsuariosTenant: sin
            // esto el selector ofrecía gestores de otras organizaciones.
            var gestores = await DirectorioUsuarios.ObtenerVisiblesEnRolAsync(Roles.GestorCae);
            _gestoresDisponibles = gestores
                .Select(u => new GestorCaeSelectorDto(u.Id, u.NombreCompleto, u.Email ?? string.Empty))
                .ToList();

            if (edicion != _edicionVigente)
                return;
        }

        _editandoId = cliente.Id;
        // La versión que se está viendo: vuelve en el Command para detectar
        // que otra persona guardó mientras el formulario estaba abierto.
        _versionEditando = cliente.Version;
        _razonSocial = cliente.RazonSocial;
        _cif = cliente.Cif;
        _esCritico = cliente.EsCritico;
        _ejecutivoUsuarioId = cliente.EjecutivoUsuarioId?.ToString() ?? string.Empty;
        _ejecutivoUsuarioIdOriginal = _ejecutivoUsuarioId;
        _notas = cliente.Notas ?? string.Empty;
        _erroresCampo = new Dictionary<string, string>();
        _mensajeErrorFormulario = null;
        _drawerVisible = true;
        FijarInstantaneaFormulario();
    }

    private readonly InstantaneaFormulario _instantanea = new();

    /// <summary>
    /// P1-E2b: único punto de verdad de «hay cambios» en el drawer de alta y edición de Cliente empresarial, comparado con
    /// cómo se abrió. Lo leen el DrawerFormulario (X, Escape, «Cancelar» y salir de la página); cerrado (también tras
    /// guardar) nunca hay nada que perder. Se lee en vivo: la navegación de «Continuar con la empresa» sale en el mismo
    /// manejador que cierra el drawer, antes de que el kit reciba el Visible nuevo.
    /// </summary>
    private bool HayCambiosCliente => _drawerVisible && _instantanea.Difiere(ValoresFormulario());

    /// <summary>El nombre ya escrito en el modal de guardar filtro: lo leen el Modal y el aviso de la página.</summary>
    private bool HayCambiosFiltro => _mostrarGuardarFiltro && !string.IsNullOrWhiteSpace(_nombreFiltroNuevo);

    private object?[] ValoresFormulario() => [_razonSocial, _cif, _esCritico, _notas, _ejecutivoUsuarioId];

    private void FijarInstantaneaFormulario() => _instantanea.Fijar(ValoresFormulario());

    private void CerrarClienteDescartando()
    {
        _drawerVisible = false;
        // Si la salida descartada vuelve a esta misma página con ?accion= (el atajo
        // «n»), esa acción tiene que volver a atenderse.
        _accionAtendida = null;
    }

    private void CerrarFiltroDescartando()
    {
        _mostrarGuardarFiltro = false;
        _nombreFiltroNuevo = string.Empty;
        _mensajeErrorFiltro = null;
    }

    private Task CerrarDrawerAsync(bool visible)
    {
        _drawerVisible = visible;
        if (!visible)
            QuitarAccionDeLaUrl();
        return Task.CompletedTask;
    }

    private Task GuardarAsync() => GuardarAsync(continuarACrearEmpresa: false);

    /// <summary>
    /// "Continuar con la empresa" (Fase A2): mismo guardado, pero al crear un
    /// Cliente nuevo con éxito navega a <c>/empresas?accion=crear</c> con el
    /// Cliente recién creado ya fijado — encadena el alta sin pasar por el
    /// asistente completo de <c>/clientes/alta-guiada</c>.
    /// </summary>
    private Task GuardarYCrearEmpresaAsync() => GuardarAsync(continuarACrearEmpresa: true);

    private async Task GuardarAsync(bool continuarACrearEmpresa)
    {
        // Guarda de doble clic: el botón se desactiva con _guardando, pero ese
        // repintado llega al navegador después; un segundo clic en ese hueco
        // crearía el Cliente dos veces. Cubre también que «Guardar» y
        // «Continuar con la empresa» se pulsen uno tras otro.
        if (_guardando)
            return;

        _guardando = true;
        _mensajeErrorFormulario = null;
        _erroresCampo = new Dictionary<string, string>();

        try
        {
            var notas = string.IsNullOrWhiteSpace(_notas) ? null : _notas;
            string? mensajeError;
            Guid? clienteCreadoId = null;

            if (_editandoId is null)
            {
                var resultado = await Mediator.Send(new CrearClienteCommand(_razonSocial, _cif, _esCritico, notas));
                mensajeError = resultado.EsFallido ? resultado.Error.Mensaje : null;
                if (resultado.EsExitoso)
                    clienteCreadoId = resultado.Valor;
            }
            else
            {
                var resultado = await Mediator.Send(
                    new EditarClienteCommand(_editandoId.Value, _razonSocial, _cif, _esCritico, notas, _versionEditando));
                mensajeError = resultado.EsFallido ? resultado.Error.Mensaje : null;
            }

            if (mensajeError is not null)
            {
                _mensajeErrorFormulario = mensajeError;
                return;
            }

            if (_editandoId is not null && _puedeReasignarEjecutivo && _ejecutivoUsuarioId != _ejecutivoUsuarioIdOriginal)
            {
                var nuevoEjecutivoId = Guid.TryParse(_ejecutivoUsuarioId, out var idGestor) ? idGestor : (Guid?)null;
                var resultadoReasignar = await Mediator.Send(new ReasignarEjecutivoClienteCommand(_editandoId.Value, nuevoEjecutivoId));
                if (resultadoReasignar.EsFallido)
                    ToastService.MostrarError(resultadoReasignar.Error);
            }

            ToastService.Mostrar(
                _editandoId is null ? "Cliente empresarial creado correctamente." : "Cliente empresarial actualizado correctamente.",
                TonoToast.Exito);

            _drawerVisible = false;

            if (continuarACrearEmpresa && clienteCreadoId is not null)
            {
                // Guardado: la navegación que sigue es la del propio formulario y no pregunta.
                NavigationManager.NavigateTo($"/empresas?accion=crear&clienteId={clienteCreadoId}");
                return;
            }

            QuitarAccionDeLaUrl();
            await RecargarAsync();
        }
        catch (ValidationException ex)
        {
            _erroresCampo = ex.Errors
                .GroupBy(e => e.PropertyName)
                .ToDictionary(g => g.Key, g => g.First().ErrorMessage);
        }
        catch (Exception)
        {
            _mensajeErrorFormulario = "No pudimos guardar los cambios. Intenta nuevamente en unos segundos.";
        }
        finally
        {
            _guardando = false;
        }
    }

    private string? ObtenerError(string campo) => _erroresCampo.GetValueOrDefault(campo);

    /// <summary>
    /// Validación inline al salir del campo (mismo patrón que Centros.razor,
    /// Project-Hydra-Negocio/tecnico/docs/archive/design/UX_PATTERNS.md, P1-18 de Project-Hydra-Negocio/MATURITY_REVIEW.md).
    /// </summary>
    private async Task ValidarRazonSocialAsync() => await ValidarCampoAsync(nameof(CrearClienteCommand.RazonSocial));

    private async Task ValidarCifAsync() => await ValidarCampoAsync(nameof(CrearClienteCommand.Cif));

    private async Task ValidarCampoAsync(string campo)
    {
        var notas = string.IsNullOrWhiteSpace(_notas) ? null : _notas;
        var resultado = await ValidadorCrear.ValidateAsync(
            new CrearClienteCommand(_razonSocial, _cif, _esCritico, notas),
            opciones => opciones.IncludeProperties(campo));

        if (resultado.IsValid)
            _erroresCampo.Remove(campo);
        else
            _erroresCampo[campo] = resultado.Errors[0].ErrorMessage;
    }

    // --- P3-31: selección múltiple ---

    private bool TodosSeleccionados =>
        _elementosPagina.Count > 0 && _elementosPagina.All(e => _seleccionados.Contains(e.Id));

    private void AlternarSeleccionTodos(bool marcar)
    {
        if (marcar)
            foreach (var elemento in _elementosPagina) _seleccionados.Add(elemento.Id);
        else
            _seleccionados.Clear();
    }

    private void AlternarSeleccion(Guid id, bool marcado)
    {
        if (marcado) _seleccionados.Add(id);
        else _seleccionados.Remove(id);
    }

    private bool _restaurandoLote;

    /// <summary>«Deshacer» del aviso de una eliminación en lote: un único deshacer restaura todos los que el lote sí eliminó.</summary>
    private async Task DeshacerEliminarLoteAsync(IReadOnlyList<Guid> ids)
    {
        if (_restaurandoLote) return;
        _restaurandoLote = true;

        try
        {
            var r = await RestauracionEnLote.RestaurarAsync(ids, id => Mediator.Send(new RestaurarClienteCommand(id)));

            ToastService.Mostrar(
                r.Errores.Count == 0 ? Textos["ToastLoteRestaurados", r.Restaurados].Value : Textos["ToastLoteRestauradosConErrores", r.Restaurados, r.Errores.Count, string.Join(" ", r.Errores)].Value,
                r.Errores.Count == 0 ? TonoToast.Exito : TonoToast.Advertencia);

            if (r.Restaurados > 0)
                await RecargarAsync();
        }
        catch (Exception)
        {
            ToastService.Mostrar(Textos["ErrorRestaurarLote"], TonoToast.Error);
        }
        finally
        {
            _restaurandoLote = false;
        }
    }

    private async Task ConfirmarEliminarLoteAsync()
    {
        if (_eliminandoLote)
            return;

        _eliminandoLote = true;

        try
        {
            var idsPedidos = _seleccionados.ToList();
            var resultado = await Mediator.Send(new EliminarClientesCommand(idsPedidos));
            var dto = resultado.Valor;
            IReadOnlyList<Guid> eliminados = dto.IdsEliminados ?? [];

            ToastService.Mostrar(
                dto.Errores.Count == 0
                    ? $"{dto.Eliminados} Cliente(s) empresarial(es) dado(s) de baja."
                    : $"{dto.Eliminados} dado(s) de baja. {dto.Errores.Count} no se pudieron dar de baja: {string.Join(" ", dto.Errores)}",
                dto.Errores.Count == 0 ? TonoToast.Exito : TonoToast.Advertencia,
                eliminados.Count > 0 ? Textos["ToastAccionDeshacer"].Value : null,
                eliminados.Count > 0 ? () => DeshacerEliminarLoteAsync(eliminados) : null);

            // Se retiran solo las fichas de los que cayeron (IdsEliminados); un superviviente
            // conserva la suya y su edición sin guardar.
            if (dto.Eliminados > 0)
                WorkspaceService.RetirarSiEstaAbierto(EntidadWorkspace.Cliente, dto.IdsEliminados ?? idsPedidos);

            _seleccionados.Clear();
            _confirmarEliminarLoteVisible = false;
            await RecargarAsync();
        }
        catch (Exception)
        {
            ToastService.Mostrar("No pudimos dar de baja los Clientes empresariales seleccionados. Intenta nuevamente.", TonoToast.Error);
        }
        finally
        {
            _eliminandoLote = false;
        }
    }

    // --- P3-31: atajos de teclado j/k/x/Enter ---

    /// <summary>
    /// La fila se tinta por su peor estado documental: Faltante o Vencido en rojo, Urgente en
    /// ámbar (rediseño de listados, fase 1). El foco de teclado (j/k) se suma al tinte, no lo
    /// sustituye: la fila enfocada sigue diciendo en qué estado está (list-page.css combina las
    /// dos clases).
    /// </summary>
    private string ObtenerClaseFila(ClienteListaDto item)
    {
        var tinte = item.EstadoDocumentalPeor switch
        {
            EstadoDocumento.Faltante or EstadoDocumento.Vencido => "fila-tintada-peligro",
            EstadoDocumento.Urgente => "fila-tintada-aviso",
            _ => null
        };
        var foco = item.Id == _idEnfocado ? "fila-enfocada" : null;
        // «fila-pulsable»: contrato con el oyente delegado de atajos-lista.js (QuickGrid no
        // expone el clic de la fila), que pulsa por ella el botón «nombre-abre-vista-rapida».
        return string.Join(' ', new[] { "fila-pulsable", foco, tinte }.Where(c => c is not null));
    }

    private async Task ManejarAtajoAsync(string tecla)
    {
        // «e» no depende de que haya filas: sin fila enfocada edita la ficha que esté abierta,
        // aunque el filtro haya dejado la lista vacía.
        if (tecla == "e")
        {
            // La fila enfocada siempre está en la página: cada carga de la lista la olvida.
            if ((_idEnfocado ?? FichaAbierta) is { } idEditar)
                await AbrirVistaRapidaEnEdicionAsync(idEditar);
            return;
        }

        if (_elementosPagina.Count == 0) return;

        switch (tecla)
        {
            case "j":
                {
                    var indiceActual = _idEnfocado is null ? -1 : _elementosPagina.FindIndex(e => e.Id == _idEnfocado);
                    _idEnfocado = _elementosPagina[Math.Min(indiceActual + 1, _elementosPagina.Count - 1)].Id;
                    break;
                }
            case "k":
                {
                    var indiceActual = _idEnfocado is null ? 0 : _elementosPagina.FindIndex(e => e.Id == _idEnfocado);
                    _idEnfocado = _elementosPagina[Math.Max(indiceActual - 1, 0)].Id;
                    break;
                }
            case "x":
                // Marcar enciende la selección múltiple: una fila marcada sin casilla a la
                // vista sería selección invisible justo antes de «Dar de baja seleccionados».
                if (_idEnfocado is { } idAlternar)
                {
                    _seleccionMultiple = true;
                    AlternarSeleccion(idAlternar, !_seleccionados.Contains(idAlternar));
                }
                break;
            case "Enter":
                // Enter hace lo mismo que pulsar el nombre de la fila: abrir la vista rápida.
                if (_idEnfocado is { } idAbrir)
                    await AbrirVistaRapidaAsync(idAbrir);
                break;
        }

        StateHasChanged();
    }

    // --- P3-31: filtros guardados ---

    /// <summary>
    /// Un eje de un filtro guardado tal como vino en su JSON: <c>Declarado</c>
    /// dice si la clave ESTABA (aunque fuera con null); <c>Valor</c>, lo que
    /// traía.
    /// </summary>
    private readonly record struct EjeGuardado<T>(bool Declarado, T Valor);

    private sealed record FiltroGuardadoLeido(
        EjeGuardado<string?> Busqueda,
        EjeGuardado<bool> SoloCriticos,
        EjeGuardado<string?> GestorCaeId,
        EjeGuardado<string?> EstadoDocumental);

    /// <summary>
    /// Lee el <c>ValoresJson</c> de un filtro guardado eje por eje, distinguiendo
    /// la clave AUSENTE de la clave presente con null: es lo que separa un filtro
    /// antiguo (que no conocía Ejecutivo ni Estado) de uno nuevo que los dejó sin
    /// valor a propósito. Devuelve null si el texto no es un objeto JSON o si
    /// un eje trae un tipo que no es el suyo: <c>ValoresJson</c> vive en la
    /// tabla <c>FiltrosGuardados</c>, Application solo exige que no esté vacío,
    /// y puede llegar corrupto o escrito por otro productor.
    /// </summary>
    private static FiltroGuardadoLeido? LeerFiltroGuardado(string valoresJson)
    {
        try
        {
            using var documento = JsonDocument.Parse(valoresJson);
            var raiz = documento.RootElement;
            if (raiz.ValueKind != JsonValueKind.Object)
                return null;

            // Las claves salen del mismo record con el que se escriben: un
            // renombrado en uno no puede dejar al otro leyendo otra clave.
            if (!LeerTexto(raiz, nameof(FiltrosClientesJson.Busqueda), out var busqueda)
                || !LeerBooleano(raiz, nameof(FiltrosClientesJson.SoloCriticos), out var soloCriticos)
                || !LeerTexto(raiz, nameof(FiltrosClientesJson.GestorCaeId), out var gestorCaeId)
                || !LeerTexto(raiz, nameof(FiltrosClientesJson.EstadoDocumental), out var estadoDocumental))
                return null;

            return new FiltroGuardadoLeido(busqueda, soloCriticos, gestorCaeId, estadoDocumental);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool LeerTexto(JsonElement raiz, string clave, out EjeGuardado<string?> eje)
    {
        eje = default;
        if (!raiz.TryGetProperty(clave, out var valor))
            return true;

        switch (valor.ValueKind)
        {
            case JsonValueKind.Null:
                eje = new EjeGuardado<string?>(true, null);
                return true;
            case JsonValueKind.String:
                eje = new EjeGuardado<string?>(true, valor.GetString());
                return true;
            default:
                return false;
        }
    }

    private static bool LeerBooleano(JsonElement raiz, string clave, out EjeGuardado<bool> eje)
    {
        eje = default;
        if (!raiz.TryGetProperty(clave, out var valor))
            return true;

        switch (valor.ValueKind)
        {
            case JsonValueKind.True:
            case JsonValueKind.False:
                eje = new EjeGuardado<bool>(true, valor.GetBoolean());
                return true;
            case JsonValueKind.Null:
                eje = new EjeGuardado<bool>(true, false);
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// Aplicar un filtro guardado fija los ejes que su JSON DECLARA y deja
    /// los demás como estaban. Un filtro guardado antes de que existieran
    /// Ejecutivo y Estado (solo <c>Busqueda</c> y <c>SoloCriticos</c>) no los
    /// limpia: no dice nada de ellos. Uno nuevo declara los cuatro —aunque
    /// alguno vaya a null— y los fija todos, null incluido.
    ///
    /// <para>
    /// Escribe en la URL los dos que viajan por ella. Antes solo cambiaba los
    /// campos en memoria: la URL seguía con el <c>q</c>/<c>critico</c> anterior
    /// y la siguiente pasada de <see cref="OnParametersSetAsync"/> (cualquier
    /// otro filtro que escribiera en la URL) devolvía la búsqueda vieja.
    /// </para>
    ///
    /// <para>
    /// Un JSON que no se puede leer no tumba el circuito: los filtros se quedan
    /// como estaban y se avisa.
    /// </para>
    /// </summary>
    private async Task AplicarFiltroGuardadoAsync(string idTexto)
    {
        if (!Guid.TryParse(idTexto, out var id)) return;

        var filtro = _filtrosGuardados.FirstOrDefault(f => f.Id == id);
        if (filtro is null) return;

        if (LeerFiltroGuardado(filtro.ValoresJson) is not { } valores)
        {
            ToastService.Mostrar(
                $"No se pudo aplicar este filtro guardado («{filtro.Nombre}»): su contenido no se puede leer. Los filtros de la lista siguen como estaban.",
                TonoToast.Advertencia);
            return;
        }

        if (valores.Busqueda.Declarado)
            _busqueda = valores.Busqueda.Valor ?? string.Empty;
        if (valores.SoloCriticos.Declarado)
            _soloCriticos = valores.SoloCriticos.Valor;
        // Un ejecutivo que ya no está en el directorio visible no se repone:
        // filtraría por alguien que la pantalla no puede nombrar («Ejecutivo: —»).
        if (valores.GestorCaeId.Declarado)
            _ejecutivoFiltro = _ejecutivosParaFiltro.Any(g => g.Id.ToString() == valores.GestorCaeId.Valor)
                ? valores.GestorCaeId.Valor!
                : string.Empty;
        if (valores.EstadoDocumental.Declarado)
            _estadoDocumentalFiltro = EstadosValidos(valores.EstadoDocumental.Valor);

        NavigationManager.ActualizarFiltrosEnUrl(new Dictionary<string, string?>
        {
            ["q"] = _busqueda,
            ["critico"] = _soloCriticos ? "true" : null,
            ["gestor"] = _ejecutivoFiltro,
            ["estado"] = _estadoDocumentalFiltro,
        });
        await RecargarAsync();
    }

    private void CerrarModalGuardarFiltro(bool visible)
    {
        _mostrarGuardarFiltro = visible;
        if (!visible)
        {
            // Cancelar descarta el nombre: reabrir el modal sin tocarlo no es un cambio.
            _nombreFiltroNuevo = string.Empty;
            _mensajeErrorFiltro = null;
            QuitarAccionDeLaUrl();
        }
    }

    private async Task GuardarFiltroActualAsync()
    {
        if (_guardandoFiltro || string.IsNullOrWhiteSpace(_nombreFiltroNuevo)) return;

        _guardandoFiltro = true;
        _mensajeErrorFiltro = null;

        try
        {
            var valoresJson = JsonSerializer.Serialize(new FiltrosClientesJson(
                string.IsNullOrWhiteSpace(_busqueda) ? null : _busqueda,
                _soloCriticos,
                string.IsNullOrWhiteSpace(_ejecutivoFiltro) ? null : _ejecutivoFiltro,
                string.IsNullOrWhiteSpace(_estadoDocumentalFiltro) ? null : _estadoDocumentalFiltro));

            var resultado = await Mediator.Send(
                new GuardarFiltroCommand(PantallasConFiltrosGuardados.Clientes, _nombreFiltroNuevo, valoresJson));

            if (resultado.EsFallido)
            {
                _mensajeErrorFiltro = resultado.Error.Mensaje;
                return;
            }

            _filtrosGuardados = await Mediator.Send(new ObtenerFiltrosGuardadosQuery(PantallasConFiltrosGuardados.Clientes));
            _nombreFiltroNuevo = string.Empty;
            CerrarModalGuardarFiltro(false);
            ToastService.Mostrar("Filtro guardado.", TonoToast.Exito);
        }
        catch (Exception)
        {
            _mensajeErrorFiltro = "No pudimos guardar el filtro. Intenta nuevamente en unos segundos.";
        }
        finally
        {
            _guardandoFiltro = false;
        }
    }

    /// <summary>El ✕ de un filtro guardado dentro de «Más filtros» (con su Id): pide confirmación.</summary>
    private void PedirEliminarFiltroGuardado(string idTexto) =>
        _filtroGuardadoAEliminar = _filtrosGuardados.FirstOrDefault(f => f.Id.ToString() == idTexto);

    private async Task ConfirmarEliminarFiltroGuardadoAsync()
    {
        if (_eliminandoFiltroGuardado || _filtroGuardadoAEliminar is not { } filtro)
            return;

        _eliminandoFiltroGuardado = true;

        try
        {
            var resultado = await Mediator.Send(new EliminarFiltroGuardadoCommand(filtro.Id));
            if (resultado.EsFallido)
            {
                ToastService.MostrarError(resultado.Error);
                return;
            }

            _filtrosGuardados = await Mediator.Send(new ObtenerFiltrosGuardadosQuery(PantallasConFiltrosGuardados.Clientes));
            _filtroGuardadoAEliminar = null;
        }
        catch (Exception)
        {
            ToastService.Mostrar("No pudimos borrar el filtro guardado. Intenta nuevamente en unos segundos.", TonoToast.Error);
        }
        finally
        {
            _eliminandoFiltroGuardado = false;
        }
    }

    /// <summary>El orden de la última carga de la rejilla, para que «Exportar esta vista» salga en el mismo.</summary>
    private string? _ordenExportar;
    private bool _descendenteExportar;

    /// <summary>
    /// Los criterios de «Exportar esta vista»: los mismos que esta página pasa a la consulta del
    /// listado, con los nombres de parámetro del endpoint de exportación, que los lee igual.
    /// </summary>
    private Dictionary<string, string?> CriteriosExportar => new()
    {
        ["q"] = _busqueda,
        ["critico"] = _soloCriticos ? "true" : null,
        ["ejecutivo"] = _ejecutivoFiltro,
        ["estado"] = _estadoDocumentalFiltro,
        ["orden"] = _ordenExportar,
        ["desc"] = _descendenteExportar ? "true" : null,
    };
}
