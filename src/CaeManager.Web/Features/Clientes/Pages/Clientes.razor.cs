using System.Text.Json;
using CaeManager.Application.Centros.Queries.ObtenerCentros;
using CaeManager.Application.Clientes.Commands.CrearCliente;
using CaeManager.Application.Clientes.Commands.EliminarClientes;
using CaeManager.Application.Clientes.Commands.ReasignarEjecutivoCliente;
using CaeManager.Application.Clientes.Commands.RestaurarCliente;
using CaeManager.Application.Clientes.Queries.ObtenerCentrosDeCliente;
using CaeManager.Application.Clientes.Queries.ObtenerClientes;
using CaeManager.Application.Configuracion.Commands.EliminarFiltroGuardado;
using CaeManager.Application.Configuracion.Commands.GuardarFiltro;
using CaeManager.Application.Configuracion.Queries;
using CaeManager.Application.Documentos;
using CaeManager.Domain.Centros;
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

    private void AlGuardarEntidad(EntidadWorkspace tipo, Guid id)
    {
        if (tipo == EntidadWorkspace.Cliente)
            _ = InvokeAsync(() => RefrescarFilaAsync(id));
    }

    private async Task RefrescarFilaAsync(Guid id)
    {
        // Con una carga en vuelo no se sustituye nada: la sustitución caería sobre una página que
        // está a punto de cambiar. Hueco conocido: si esa carga leyó antes de que el guardado
        // fuera firme, la fila conserva el dato anterior hasta la siguiente carga.
        if (_desechado || _cargando || !_elementosPagina.Any(e => e.Id == id))
            return;

        var carga = _cargaVigente;
        try
        {
            // Con el desglose: la fila sustituida conserva la ventana de incidencias de su motivo.
            var resultado = await Mediator.Send(
                new ObtenerClientesQuery(Busqueda: null, SoloCriticos: null, Id: id, ConDesgloseDocumental: true), _ciclo.Token);
            var indice = _elementosPagina.FindIndex(e => e.Id == id);
            if (_desechado || _cargando || carga != _cargaVigente || indice < 0
                || resultado.Elementos.FirstOrDefault() is not { } actualizada)
                return;

            // Sustitución en sitio: filtros, orden, página, selección, filas desplegadas y foco no se tocan. Los
            // Centros ya pedidos de esa fila se olvidan: su estado puede haber cambiado con el guardado.
            _elementosPagina[indice] = actualizada;
            _centrosConEstado.Remove(id);
            _centrosConEstadoConError.Remove(id);
            StateHasChanged();
            if (_expandidos.Contains(id))
                await CargarCentrosConEstadoAsync(id);
        }
        catch (Exception)
        {
            // El guardado ya es firme: que falle la relectura no es un error que enseñar. La
            // fila conserva el dato anterior hasta la siguiente carga, como antes de este aviso.
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

    private const int TamanoPaginaMinimo = 20;
    private int _pagina = 1;
    private int _tamanoPagina = TamanoPaginaMinimo;

    /// <summary>La primera carga espera a que los parámetros de la URL estén leídos (OnAfterRenderAsync).</summary>
    private bool _cargaPendiente = true;
    private bool _parametrosInicializados;
    private CancellationTokenSource? _peticionActiva;

    /// <summary>Filas desplegadas (maqueta «Lista Clientes TALVEG»): las que enseñan sus Centros con problema.</summary>
    private readonly HashSet<Guid> _expandidos = [];

    /// <summary>La primera fila abierta por defecto ya se decidió (una sola vez por visita de la página).</summary>
    private bool _primeraFilaDecidida;

    /// <summary>
    /// Centros de cada Cliente empresarial desplegado, con su estado (<c>ObtenerCentrosQuery</c> por Cliente: la misma
    /// consulta de las filas de Centro de Cliente 360). Se piden al desplegar la fila y se olvidan con cada carga.
    /// </summary>
    private readonly Dictionary<Guid, IReadOnlyList<CentroListaDto>> _centrosConEstado = [];
    private readonly HashSet<Guid> _centrosConEstadoEnVuelo = [];
    private readonly HashSet<Guid> _centrosConEstadoConError = [];

    // H2 (Project-Hydra-Negocio/tecnico/docs/ux-audit/02-clientes.md): el `Paginator` de QuickGrid no está
    // localizado — `PaginadorSimple` (mismo componente que el resto de listas
    // sin QuickGrid) cubre el copy en español; sigue delegando el movimiento
    // real de página en `_paginacion.SetCurrentPageIndexAsync` para que
    // QuickGrid pida los datos.
    private int TotalPaginas => Math.Max(1, (int)Math.Ceiling(_totalElementos / (double)_tamanoPagina));

    private Task CambiarPaginaAsync(int pagina)
    {
        _pagina = pagina;
        _expandidos.Clear();
        return CargarListaAsync();
    }

    // H5 (Project-Hydra-Negocio/tecnico/docs/ux-audit/05-trabajadores-vehiculos.md): selector de tamaño de página, compartido por PaginadorSimple.razor.
    // Una sola petición: SetCurrentPageIndexAsync ya avisa a QuickGrid aunque la
    // página no cambie, así que refrescar además la rejilla pedía lo mismo dos
    // veces (ver RecargarAsync).
    private Task CambiarTamanoPaginaAsync(int tamano)
    {
        _tamanoPagina = tamano;
        return RecargarAsync();
    }

    private string _busqueda = string.Empty;
    private bool _soloCriticos;
    private string _ejecutivoFiltro = string.Empty;
    private string _estadoDocumentalFiltro = string.Empty;

    /// <summary>Clientes empresariales por estado para la franja, sin el filtro de estado aplicado. <c>null</c> hasta la primera carga.</summary>
    private IReadOnlyDictionary<string, int>? _recuentosPorEstado;

    /// <summary>Cifra de «Todos» en la franja: aquí los recuentos por estado se solapan y no suman el total.</summary>
    private int? _totalSinFiltroDeEstado;
    private IReadOnlyList<GestorCaeSelectorDto> _ejecutivosParaFiltro = [];

    /// <summary>
    /// El directorio de arriba ya está cargado. Hasta entonces <see cref="GestorVisible"/> descarta a
    /// cualquier Gestor CAE, así que la vista recordada no se restaura antes
    /// (<c>ListoParaRestaurar</c> de <see cref="VistaRecordadaDeListado"/>): se perdería el suyo.
    /// </summary>
    private bool _directorioDeGestoresCargado;
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

    // El drawer de esta lista solo da de alta: «Editar» vive en el panel del Context Workspace (lápiz, tecla «e») y el Gestor
    // CAE de referencia, en Cliente 360 (decisión de 2026-10-18).
    private bool _drawerVisible;
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

    /// <summary>Orden de columna (<c>?orden=cliente-desc</c>). Sin él, el de fábrica: por estado documental.</summary>
    [SupplyParameterFromQuery(Name = "orden")]
    public string? OrdenInicial { get; set; }

    /// <summary>
    /// El orden de columna viaja en la URL y forma parte de la vista. La rejilla nace ordenada por
    /// estado documental. la exportación de la vista filtrada sigue leyendo el orden de la última petición de la
    /// rejilla (<see cref="_ordenExportar"/>), venga de un clic en la cabecera o de la URL.
    /// </summary>
    private readonly OrdenDeRejilla _orden = new(
    [
        ("cliente", nameof(ClienteListaDto.RazonSocial)),
        ("documentacion", nameof(ClienteListaDto.EstadoDocumentalPeor)),
    ], claveDeFabrica: "documentacion");

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

        var estadoAutenticacion = await AuthenticationStateProvider.GetAuthenticationStateAsync();
        _puedeEscribir = Roles.ConEscrituraCsv.Split(',').Any(estadoAutenticacion.User.IsInRole);

        _filtrosGuardados = await Mediator.Send(new ObtenerFiltrosGuardadosQuery(PantallasConFiltrosGuardados.Clientes));

        // Directorio de Gestores CAE del Tenant: da nombre al chip del filtro «gestor» de la URL, de los filtros guardados y
        // de la vista recordada (la lista ya no dibuja la columna ni la pastilla «Gestor CAE»).
        var gestores = await DirectorioUsuarios.ObtenerVisiblesEnRolAsync(Roles.GestorCae);
        _ejecutivosParaFiltro = gestores
            .Select(u => new GestorCaeSelectorDto(u.Id, u.NombreCompleto, u.Email ?? string.Empty, u.Avatar))
            .OrderBy(g => g.NombreCompleto)
            .ToList();
        _directorioDeGestoresCargado = true;
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
    /// nunca recargaba la lista ya montada. La primera carga la hace
    /// <see cref="OnAfterRenderAsync"/> con estos campos ya al día (mientras
    /// <c>_cargaPendiente</c>); la recarga explícita solo hace falta en los
    /// siguientes pasos.
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
        var gestorDeLaUrl = GestorVisible(GestorCaeInicial);
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

        // Una navegación externa cambia la pregunta; los cambios propios ya han escrito los campos.
        _parametrosInicializados = true;
        var cambiaElOrden = _orden.Leer(OrdenInicial);
        return (cambio || cambiaElOrden) && !_cargaPendiente
            ? RecargarAsync()
            : Task.CompletedTask;
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_cargaPendiente && _parametrosInicializados && !_resolviendoEmpresa && !_sinEmpresaSeleccionada && !_desechado)
            await CargarListaAsync();
    }

    /// <summary>
    /// Rótulo de columna que ordena («Cliente» y «Estado documental»; la consulta no ordena por recuento de Centros). El
    /// orden vive en la URL. Sin QuickGrid: el sentido lo dice la flecha de la clase.
    /// </summary>
    private async Task OrdenarAsync(string propiedad)
    {
        var clave = propiedad == nameof(ClienteListaDto.RazonSocial) ? "cliente" : "documentacion";
        var descendente = _orden.Propiedad == propiedad && _orden.Direccion != SortDirection.Descending;
        _orden.Leer(clave + (descendente ? "-desc" : string.Empty));
        NavigationManager.ActualizarFiltroEnUrl("orden", _orden.EnUrl);
        await RecargarAsync();
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

    private async Task CargarListaAsync()
    {
        if (_desechado || !_parametrosInicializados) return;
        _cargaPendiente = false;

        // Todo lo que define la pregunta se lee ANTES del await.
        var carga = ++_cargaVigente;
        // Cada carga cancela la anterior: su respuesta ya no es de la pregunta vigente.
        using var cancelacion = CancellationTokenSource.CreateLinkedTokenSource(_ciclo.Token);
        var anterior = _peticionActiva;
        _peticionActiva = cancelacion;
        anterior?.Cancel();
        // Vale para UNA carga, la que pidió la corrección: cualquier otra recarga suelta la selección.
        var conservarSeleccion = _conservarSeleccionEnLaProximaCarga;
        _conservarSeleccionEnLaProximaCarga = false;
        var ordenarPor = _orden.Propiedad;
        var descendente = _orden.Direccion == SortDirection.Descending;
        if (_orden.Anotar(ordenarPor, descendente))
            NavigationManager.ActualizarFiltroEnUrl("orden", _orden.EnUrl);
        (_ordenExportar, _descendenteExportar) = (ordenarPor, descendente);
        var estadosFiltro = SeleccionEstados.Separar<EstadoDocumento>(_estadoDocumentalFiltro);
        var consulta = new ObtenerClientesQuery(
            Busqueda: string.IsNullOrWhiteSpace(_busqueda) ? null : _busqueda,
            SoloCriticos: _soloCriticos ? true : null,
            EjecutivoUsuarioId: Guid.TryParse(_ejecutivoFiltro, out var ejecutivoId) ? ejecutivoId : null,
            EstadoDocumental: null,
            Pagina: _pagina,
            TamanoPagina: _tamanoPagina,
            OrdenarPor: ordenarPor,
            Descendente: descendente,
            EstadosDocumentales: estadosFiltro.Count == 0 ? null : estadosFiltro,
            ConRecuentosPorEstado: true,
            // Las incidencias de cada fila, para la ventana del motivo. Solo lo piden esta carga y el
            // refresco de una fila: la exportación y la API no lo pintan.
            ConDesgloseDocumental: true);

        _cargando = true;
        _errorCarga = false;
        StateHasChanged();

        try
        {
            var resultado = await Mediator.Send(consulta, cancelacion.Token);

            if (_desechado || carga != _cargaVigente)
                return;

            _totalElementos = resultado.TotalElementos;
            _recuentosPorEstado = resultado.RecuentosPorEstado;
            _totalSinFiltroDeEstado = resultado.TotalSinFiltroDeEstado;

            var elementos = resultado.Elementos.ToList();
            _elementosPagina = elementos;
            if (conservarSeleccion)
            {
                // Corregir un documento no es cambiar de lista: lo marcado y la fila enfocada siguen,
                // salvo lo que ya no esté en la página.
                _seleccionados.IntersectWith(elementos.Select(e => e.Id));
                if (_idEnfocado is { } enfocado && elementos.All(e => e.Id != enfocado))
                    _idEnfocado = null;
            }
            else
            {
                _seleccionados.Clear();
                _idEnfocado = null;
            }

            OlvidarCentrosDeFila();
            OlvidarCentrosConEstado();
            _expandidos.IntersectWith(elementos.Where(PuedeDesplegarse).Select(e => e.Id));

            // Como en la maqueta, la primera fila llega abierta: solo en la primera carga que trae filas (después, lo que
            // desplegó o recogió quien mira manda) y solo si esa fila existe y tiene Centros que enseñar.
            if (!_primeraFilaDecidida && elementos.Count > 0)
            {
                _primeraFilaDecidida = true;
                if (PuedeDesplegarse(elementos[0]))
                    _expandidos.Add(elementos[0].Id);
            }

            if (_pagina > TotalPaginas)
            {
                _pagina = TotalPaginas;
                await CargarListaAsync();
            }
        }
        catch (Exception) when (_desechado || carga != _cargaVigente)
        {
            // Una carga superada que falla (o que se canceló) no es un error de la vigente: no puede tapar su resultado.
        }
        catch (Exception)
        {
            _errorCarga = true;
        }
        finally
        {
            if (ReferenceEquals(_peticionActiva, cancelacion))
                _peticionActiva = null;
            if (!_desechado && carga == _cargaVigente)
            {
                _cargando = false;
                StateHasChanged();
            }
        }
        // Lo desplegado que sobrevive a la carga (p. ej. tras corregir un documento) vuelve a pedir sus Centros.
        if (!_desechado && carga == _cargaVigente && !_errorCarga)
            foreach (var id in _expandidos.ToList())
                await CargarCentrosConEstadoAsync(id);
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

    /// <summary>Filtro «Estado documental» de la franja — mismo patrón que SoloCriticos: estado local, URL (<c>?estado=</c>) y recarga.</summary>
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

    /// <summary>
    /// Quita el chip del Gestor CAE. La lista no ofrece elegirlo (se gestiona en Cliente 360): el filtro <c>gestor</c> solo
    /// llega por la URL, un filtro guardado o la vista recordada, y aquí solo se quita.
    /// </summary>
    private async Task QuitarFiltroEjecutivoAsync()
    {
        _ejecutivoFiltro = string.Empty;
        NavigationManager.ActualizarFiltroEnUrl("gestor", string.Empty);
        await RecargarAsync();
    }

    private string EtiquetaFiltroBusqueda => "Búsqueda: \"" + _busqueda + "\"";

    private string EtiquetaFiltroEjecutivo =>
        "Gestor CAE: " + (_ejecutivosParaFiltro.FirstOrDefault(g => g.Id.ToString() == _ejecutivoFiltro)?.NombreCompleto ?? "—");

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
            var sustantivo = _totalElementos == 1 ? "Cliente" : "Clientes";
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

    [Inject] private Microsoft.Extensions.Localization.IStringLocalizer<CaeManager.Web.Recursos.TextosComunes> Comunes { get; set; } = default!;

    /// <summary>
    /// El rol efectivo puede escribir (mismos roles que <c>SoloConEscritura</c>). Decide si las incidencias de
    /// la ventana del motivo se ofrecen como pulsables: a quien solo consulta no se le ofrece un formulario
    /// que el comando le va a denegar.
    /// </summary>
    private bool _puedeEscribir;

    private CaeManager.Web.Features.Documentos.Components.CorreccionIncidenciaDocumental _correccion = default!;

    /// <summary>La carga que pide <see cref="RefrescarTrasCorreccionAsync"/> conserva la selección y la fila enfocada.</summary>
    private bool _conservarSeleccionEnLaProximaCarga;

    /// <summary>
    /// Título de la ventana: cuántas alertas tiene el Cliente empresarial en total. No es la cifra del motivo,
    /// que cuenta solo las del peor estado; la ventana las lista todas, cada una con la pastilla de su estado.
    /// </summary>
    private string TituloDeIncidencias(int total) =>
        total == 1 ? Textos["IncidenciasTituloUna"].Value : Textos["IncidenciasTitulo", total].Value;

    /// <summary>Nombre accesible del disparador: el estado, lo que dice el motivo y lo que hay dentro.</summary>
    private string EtiquetaDeIncidencias(ClienteListaDto cliente, EstadoDocumento peor, string motivo) =>
        $"{EstadoDocumentoUi.Texto(peor)}: {motivo}. {TituloDeIncidencias(cliente.IncidenciasTotales)}";

    /// <summary>
    /// Pie de la ventana: cómo se corrige (solo a quien puede) y, si no caben todas, cuántas quedan fuera. A
    /// quien corrige se le dice que las siguientes van entrando: la lista se relee tras cada corrección y la
    /// consulta entrega primero las más graves. Ninguna pantalla lista hoy las alertas de un solo Cliente
    /// empresarial, así que el pie no remite a otra.
    /// </summary>
    private string? PieDeIncidencias(ClienteListaDto cliente)
    {
        var fuera = cliente.IncidenciasTotales - cliente.Incidencias.Count;
        if (!_puedeEscribir)
            return fuera > 0 ? Textos["IncidenciasPieMas", fuera].Value : null;

        var comoCorregir = Comunes["VentanaIncidenciasPie"].Value;
        return fuera > 0 ? $"{comoCorregir}. {Textos["IncidenciasPieMasAlCorregir", fuera].Value}" : comoCorregir;
    }

    /// <summary>«Documento — Trabajador», como las incidencias de Trabajador en la ventana de Centros.</summary>
    private static string TextoDeIncidencia(IncidenciaClienteDto incidencia) =>
        $"{incidencia.TipoDocumentoNombre} — {incidencia.TrabajadorNombre}";

    /// <summary>Lo que distingue la línea: cuándo vence el documento o, si falta, en qué Centro se pide.</summary>
    private static string? SecundarioDeIncidencia(IncidenciaClienteDto incidencia) =>
        incidencia.FechaVencimiento?.ToString("dd/MM/yyyy", System.Globalization.CultureInfo.InvariantCulture)
        ?? incidencia.CentroNombre;

    /// <summary>
    /// Con documento abre su renovación; sin él (Faltante), el alta del que falta para ese Trabajador y ese
    /// Tipo. No decide permisos: lo que se guarde pasa por el comando, con su autorización y su alcance.
    /// </summary>
    private Task CorregirIncidenciaAsync(IncidenciaClienteDto incidencia) =>
        _correccion.AbrirAsync(incidencia.DocumentoId, incidencia.TipoDocumentoId, incidencia.TrabajadorId, null);

    /// <summary>
    /// Tras corregir una incidencia: se vuelve a pedir la página tal como está (mismos filtros, orden y página).
    /// No basta con la fila pulsada: el documento que se da de alta para un Trabajador deja de faltar en todos
    /// los Centros donde se pedía, que pueden ser de otros Clientes empresariales de la página. La selección y
    /// la fila enfocada se conservan.
    /// </summary>
    private async Task RefrescarTrasCorreccionAsync()
    {
        if (_desechado)
            return;

        _conservarSeleccionEnLaProximaCarga = true;
        await CargarListaAsync();
    }

    /// <summary>
    /// Lo que de verdad cuenta el agregado de ObtenerClientesQuery: las alertas documentales abiertas de sus
    /// Trabajadores, en su peor estado. Son las de vigencia (vencido o por vencer), atribuidas al Cliente
    /// empresarial principal del Trabajador, y los documentos que faltan en un Centro suyo. Por eso no dice
    /// «de vigencia»: con lo peor en «Pendiente» el texto se contradiría. El mockup dice «entre los trabajadores
    /// y centros»; los documentos propios del Centro no entran en ese agregado, así que no se nombran.
    /// </summary>
    private static string TituloEstadoDocumental(EstadoDocumento peor) =>
        $"Peor estado entre las alertas documentales abiertas de sus trabajadores: {EstadoDocumentoUi.Texto(peor).ToLowerInvariant()}";

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
        _pagina = 1;
        _expandidos.Clear();
        await CargarListaAsync();
    }

    // ── Filas desplegadas: los Centros del Cliente empresarial con problema ───────────────────────────────

    /// <summary>
    /// Solo se despliega un Cliente empresarial que tiene alertas y Centros (maqueta: «Sin incidencias» no lleva chevron):
    /// la fila enseña sus Centros con problema, cada uno con su estado y el motivo.
    /// </summary>
    private static bool PuedeDesplegarse(ClienteListaDto cliente) =>
        cliente.Centros > 0 && cliente.EstadoDocumentalPeor is not null;

    private bool HayFilasDesplegables => _elementosPagina.Any(PuedeDesplegarse);

    private bool TodosExpandidos => HayFilasDesplegables
        && _elementosPagina.Where(PuedeDesplegarse).All(c => _expandidos.Contains(c.Id));

    private bool EstaExpandido(ClienteListaDto cliente) => PuedeDesplegarse(cliente) && _expandidos.Contains(cliente.Id);

    private Task AlternarExpansionAsync(Guid id)
    {
        if (!_expandidos.Remove(id))
        {
            _expandidos.Add(id);
            return CargarCentrosConEstadoAsync(id);
        }

        return Task.CompletedTask;
    }

    private async Task AlternarTodosExpandidosAsync(bool expandir)
    {
        _expandidos.Clear();
        if (!expandir) return;

        var ids = _elementosPagina.Where(PuedeDesplegarse).Select(c => c.Id).ToList();
        _expandidos.UnionWith(ids);
        foreach (var id in ids)
            await CargarCentrosConEstadoAsync(id);
    }

    private async Task CargarCentrosConEstadoAsync(Guid id)
    {
        if (_desechado || _centrosConEstado.ContainsKey(id) || !_centrosConEstadoEnVuelo.Add(id))
            return;

        var generacion = _generacionCentrosConEstado;
        _centrosConEstadoConError.Remove(id);
        StateHasChanged();
        try
        {
            // La misma consulta que las filas de Centro de Cliente 360: peor estado primero, con sus recuentos.
            var pagina = await Mediator.Send(new ObtenerCentrosQuery(
                Busqueda: null, ClienteId: id,
                OrdenarPor: nameof(CentroListaDto.Estado), Descendente: true,
                Pagina: 1, TamanoPagina: MaximoCentrosDesplegados), _ciclo.Token);
            if (generacion == _generacionCentrosConEstado)
                _centrosConEstado[id] = pagina.Elementos
                    .Where(c => c.Estado is not (EstadoCentro.Vigente or EstadoCentro.SinGestionCae))
                    .ToList();
        }
        catch (Exception)
        {
            // Un fallo se dice en la fila y deja reintentar al volver a desplegarla.
            if (generacion == _generacionCentrosConEstado)
                _centrosConEstadoConError.Add(id);
        }
        finally
        {
            if (generacion == _generacionCentrosConEstado)
            {
                _centrosConEstadoEnVuelo.Remove(id);
                if (!_desechado) StateHasChanged();
            }
        }
    }

    /// <summary>Tope de Centros que se piden por Cliente empresarial desplegado.</summary>
    private const int MaximoCentrosDesplegados = 50;

    private int _generacionCentrosConEstado;

    private void OlvidarCentrosConEstado()
    {
        _generacionCentrosConEstado++;
        _centrosConEstado.Clear();
        _centrosConEstadoEnVuelo.Clear();
        _centrosConEstadoConError.Clear();
    }

    /// <summary>Motivos del estado de un Centro, sin partes en cero ni confundir pendientes con vencidos (los de Centro de Cliente 360).</summary>
    private string MotivosCentro(CentroListaDto centro)
    {
        var partes = new List<string>();
        if (centro.Recuentos.VencidasPorFecha.Count > 0)
            partes.Add(Plural(centro.Recuentos.VencidasPorFecha.Count, "DetalleVencidosUno", "DetalleVencidosVarios"));
        if (centro.Recuentos.Pendientes.Count > 0)
            partes.Add(Textos[centro.Recuentos.Pendientes.Count == 1 ? "DetallePendienteUno" : "DetallePendientes", centro.Recuentos.Pendientes.Count]);
        if (centro.Recuentos.Bloqueos.Count > 0)
            partes.Add(Plural(centro.Recuentos.Bloqueos.Count, "DetalleBloqueoPlataformaUno", "DetalleBloqueosPlataformaVarios"));
        if (centro.Recuentos.TotalProximas > 0)
            partes.Add(Plural(centro.Recuentos.TotalProximas, "DetalleProximosUno", "DetalleProximosVarios"));
        return string.Join(" · ", partes);
    }

    private string Plural(int n, string claveUno, string claveVarios) =>
        n == 1 ? Textos[claveUno] : Textos[claveVarios, n];

    /// <summary>«Gestionar» un Centro del Cliente empresarial: su vista rápida, donde se trabaja lo que le falta.</summary>
    private Task GestionarCentroAsync(CentroListaDto centro) =>
        WorkspaceService.AbrirAsync(EntidadWorkspace.Centro, centro.Id, centro.Nombre, "informacion");

    private void AbrirCrear()
    {
        _razonSocial = string.Empty;
        _cif = string.Empty;
        _esCritico = false;
        _notas = string.Empty;
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

    private object?[] ValoresFormulario() => [_razonSocial, _cif, _esCritico, _notas];

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
            var resultado = await Mediator.Send(new CrearClienteCommand(_razonSocial, _cif, _esCritico, notas));
            if (resultado.EsFallido)
            {
                _mensajeErrorFormulario = resultado.Error.Mensaje;
                return;
            }

            var clienteCreadoId = resultado.Valor;
            ToastService.Mostrar("Cliente creado correctamente.", TonoToast.Exito);

            _drawerVisible = false;

            if (continuarACrearEmpresa)
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
                    ? $"{dto.Eliminados} Cliente(s) dado(s) de baja."
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
            ToastService.Mostrar("No pudimos dar de baja los Clientes seleccionados. Intenta nuevamente.", TonoToast.Error);
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
            EstadoDocumento.Urgente or EstadoDocumento.Proximo => "fila-tintada-aviso",
            _ => null
        };
        var foco = item.Id == _idEnfocado ? "fila-enfocada" : null;
        return string.Join(' ', new[] { "tarjeta-fila-acordeon", foco, tinte }.Where(c => c is not null));
    }

    /// <summary>La cabecera de columnas y cada fila comparten esta rejilla; con la selección múltiple entra una casilla delante.</summary>
    private string ClaseRejilla(string claseBase) =>
        $"{claseBase} rejilla-clientes" + (_seleccionMultiple ? " rejilla-clientes-seleccion" : string.Empty);

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

    // --- Vista recordada (pieza compartida VistaRecordadaDeListado) ---

    /// <summary>
    /// Lista blanca de los parámetros de VISTA de la URL: lo que recuerda y restaura la vista recordada
    /// (<see cref="VistaRecordadaDeListado"/>), menos la búsqueda libre (<c>q</c>), que es de la vista pero
    /// no se recuerda. Los filtros guardados de esta pantalla son anteriores a la pieza compartida y
    /// conservan su propio JSON (<see cref="LeerFiltroGuardado"/>), sin el orden de columna.
    /// </summary>
    public static readonly IReadOnlyList<string> ParametrosDeVista = ["q", "critico", "gestor", "estado", "orden"];

    private readonly ConexionVistaRecordada _vistaRecordada = new();

    /// <summary>
    /// La vista recordada (o la de inicio, al restablecer) define la vista entera: lo que no trae se quita.
    /// Cada valor pasa por la misma validación que el de la URL en <see cref="OnParametersSetAsync"/>: un
    /// estado que la franja no conoce se descarta y un Gestor CAE que el directorio visible de este usuario
    /// no ofrece es «sin filtro» (lo recordado no es autoridad). La URL se escribe en una sola navegación y
    /// se recarga aquí.
    /// </summary>
    private async Task AplicarVistaGuardadaAsync(IReadOnlyDictionary<string, string?> vista)
    {
        _busqueda = vista.GetValueOrDefault("q") ?? string.Empty;
        _soloCriticos = bool.TryParse(vista.GetValueOrDefault("critico"), out var soloCriticos) && soloCriticos;
        _ejecutivoFiltro = GestorVisible(vista.GetValueOrDefault("gestor"));
        _estadoDocumentalFiltro = EstadosValidos(vista.GetValueOrDefault("estado"));
        _orden.Leer(vista.GetValueOrDefault("orden"));
        // La vista recordada sustituye a la de la primera carga (que ya había decidido la fila abierta): la primera fila
        // se decide de nuevo sobre la vista que de verdad se queda.
        _primeraFilaDecidida = false;
        NavigationManager.ActualizarFiltrosEnUrl(new Dictionary<string, string?>
        {
            ["q"] = _busqueda,
            ["critico"] = _soloCriticos ? "true" : null,
            ["gestor"] = _ejecutivoFiltro,
            ["estado"] = _estadoDocumentalFiltro,
            ["orden"] = _orden.EnUrl,
        });

        await RecargarAsync();
    }

    /// <summary>El Id de Gestor CAE que llega de fuera, si el directorio visible de este usuario lo ofrece; si no, vacío.</summary>
    private string GestorVisible(string? valor) =>
        Guid.TryParse(valor, out var gestorId) && _ejecutivosParaFiltro.Any(g => g.Id == gestorId)
            ? gestorId.ToString()
            : string.Empty;

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

    /// <summary>El ✕ de un filtro guardado dentro de «Filtros guardados» (con su Id): pide confirmación.</summary>
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

    /// <summary>El orden de la última carga de la rejilla, para que la exportación de la vista filtrada salga en el mismo.</summary>
    private string? _ordenExportar;
    private bool _descendenteExportar;

    /// <summary>
    /// Los criterios de la exportación de la vista filtrada: los mismos que esta página pasa a la consulta del
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
