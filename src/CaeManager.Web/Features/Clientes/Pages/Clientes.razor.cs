using System.Text.Json;
using CaeManager.Application.Clientes.Commands.CrearCliente;
using CaeManager.Application.Clientes.Commands.EditarCliente;
using CaeManager.Application.Clientes.Commands.EliminarCliente;
using CaeManager.Application.Clientes.Commands.EliminarClientes;
using CaeManager.Application.Clientes.Commands.ReasignarEjecutivoCliente;
using CaeManager.Application.Clientes.Commands.RestaurarCliente;
using CaeManager.Application.Clientes.Queries.ObtenerClientePorId;
using CaeManager.Application.Clientes.Queries.ObtenerClientes;
using CaeManager.Application.Configuracion.Commands.EliminarFiltroGuardado;
using CaeManager.Application.Configuracion.Commands.GuardarFiltro;
using CaeManager.Application.Configuracion.Queries;
using CaeManager.Domain.Documentos;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Components;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Components.Workspace;
using CaeManager.Web.Features.Documentos;
using CaeManager.Infrastructure.Autorizacion;
using FluentValidation;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.QuickGrid;

namespace CaeManager.Web.Features.Clientes.Pages;

public record GestorCaeSelectorDto(Guid Id, string NombreCompleto, string Email);

public partial class Clientes : ComponentBase
{
    [Inject] private DirectorioUsuariosTenant DirectorioUsuarios { get; set; } = default!;
    [Inject] private AuthenticationStateProvider AuthenticationStateProvider { get; set; } = default!;
    [Inject] private IValidator<CrearClienteCommand> ValidadorCrear { get; set; } = default!;

    private static readonly string[] RolesQuePuedenReasignar =
        [Roles.Administrador, Roles.DireccionCae, Roles.CoordinadorCae];

    private readonly PaginationState _paginacion = new() { ItemsPerPage = 20 };
    private QuickGrid<ClienteListaDto>? _grid;

    // H2 (docs/ux-audit/02-clientes.md): el `Paginator` de QuickGrid no está
    // localizado — `PaginadorSimple` (mismo componente que el resto de listas
    // sin QuickGrid) cubre el copy en español; sigue delegando el movimiento
    // real de página en `_paginacion.SetCurrentPageIndexAsync` para que
    // QuickGrid pida los datos.
    private int TotalPaginas => Math.Max(1, (int)Math.Ceiling(_totalElementos / (double)_paginacion.ItemsPerPage));

    private Task CambiarPaginaAsync(int pagina) => _paginacion.SetCurrentPageIndexAsync(pagina - 1);

    // H5 (docs/ux-audit/05-trabajadores-vehiculos.md): selector de tamaño de página, compartido por PaginadorSimple.razor.
    private async Task CambiarTamanoPaginaAsync(int tamano)
    {
        _paginacion.ItemsPerPage = tamano;
        await _paginacion.SetCurrentPageIndexAsync(0);
        if (_grid is not null)
            await _grid.RefreshDataAsync();
    }

    private bool _puedeReasignarEjecutivo;
    private IReadOnlyList<GestorCaeSelectorDto> _gestoresDisponibles = [];
    private string _ejecutivoUsuarioId = string.Empty;
    private string _ejecutivoUsuarioIdOriginal = string.Empty;

    private string _busqueda = string.Empty;
    private bool _soloCriticos;
    private string _ejecutivoFiltro = string.Empty;
    private string _estadoDocumentalFiltro = string.Empty;
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

    private bool _confirmarEliminarVisible;
    private Guid _idAEliminar;
    private string _razonSocialAEliminar = string.Empty;
    private bool _eliminando;

    /// <summary>Restauraciones en vuelo, por Cliente: el «Deshacer» del aviso no manda dos veces la misma.</summary>
    private readonly HashSet<Guid> _restaurando = [];

    // Drawer ligero (Lista Clientes TALVEG.dc.html): paso intermedio antes
    // del Context Workspace completo — clic en el nombre de la fila y "Vista
    // rápida" del menú abren esto primero, no el workspace directamente.
    private Guid? _previewClienteId;
    private bool _previewVisible;

    private void AbrirPreview(Guid id)
    {
        _previewClienteId = id;
        _previewVisible = true;
    }

    private Task AbrirDesdePreviewAsync((Guid Id, string Pestana) destino)
    {
        var nombre = _elementosPagina.FirstOrDefault(e => e.Id == destino.Id)?.RazonSocial ?? string.Empty;
        return WorkspaceService.AbrirAsync(EntidadWorkspace.Cliente, destino.Id, nombre, destino.Pestana);
    }

    [SupplyParameterFromQuery(Name = "q")]
    public string? TerminoBusquedaInicial { get; set; }

    /// <summary>H4 (docs/ux-audit/02-clientes.md): antes solo `q` viajaba en la URL, `soloCriticos` se perdía al recargar o compartir el enlace.</summary>
    [SupplyParameterFromQuery(Name = "critico")]
    public bool? SoloCriticosInicial { get; set; }

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
    /// PLAN-EJECUCION-UX.md § 0.9) — son ruido permanente para una acción
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
    private FiltroGuardadoDto? _filtroGuardadoAEliminar;
    private bool _eliminandoFiltroGuardado;

    /// <summary>
    /// Lo que se guarda de un filtro. Hasta ahora solo viajaban la búsqueda y
    /// «solo críticos»: guardar «Con vencidos» o un ejecutivo concreto
    /// devolvía, al aplicarlo, una lista sin ese filtro. Los dos campos nuevos
    /// son opcionales, así que un filtro guardado antes de este cambio se sigue
    /// leyendo (sin ejecutivo ni estado).
    ///
    /// <para>
    /// El campo nuevo se llama <c>GestorCaeId</c> y no como la columna
    /// (<c>EjecutivoUsuarioId</c>, deuda terminológica congelada por
    /// TerminologiaCanonicaTests): nombra a la persona, el Gestor CAE con
    /// Asignación de Cartera sobre el Cliente empresarial. Al ser una clave
    /// nueva del JSON guardado, no hay filtros antiguos que la lleven.
    /// </para>
    /// </summary>
    private record FiltrosClientesJson(
        string? Busqueda, bool SoloCriticos, string? GestorCaeId = null, string? EstadoDocumental = null);

    protected override async Task OnInitializedAsync()
    {
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
            .Select(u => new GestorCaeSelectorDto(u.Id, u.NombreCompleto, u.Email ?? string.Empty))
            .OrderBy(g => g.NombreCompleto)
            .ToList();
    }

    /// <summary>
    /// Se re-ejecuta en cada navegación dentro de la propia página (recargar,
    /// compartir la URL, volver atrás) — no solo en el primer render — para
    /// que el filtro de la URL sea la fuente de verdad, no solo su semilla
    /// inicial (P1-18 de docs/business/MATURITY_REVIEW.md). Permite además
    /// que el buscador global (Ctrl/Cmd+K) navegue aquí con el filtro ya
    /// cargado, p. ej. /clientes?q=Cadena+Industrial.
    ///
    /// H4 (docs/ux-audit/02-clientes.md): la navegación mejorada de Blazor
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
        var deLaUrl = TerminoBusquedaInicial ?? string.Empty;
        var soloCriticosDeLaUrl = SoloCriticosInicial ?? false;
        var cambio = deLaUrl != _busqueda || soloCriticosDeLaUrl != _soloCriticos;

        _busqueda = deLaUrl;
        _soloCriticos = soloCriticosDeLaUrl;

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
        // Todo lo que define la pregunta se lee ANTES del await.
        var carga = ++_cargaVigente;
        var (ordenarPor, descendente) = LecturaOrden.Leer(request);
        var consulta = new ObtenerClientesQuery(
            Busqueda: string.IsNullOrWhiteSpace(_busqueda) ? null : _busqueda,
            SoloCriticos: _soloCriticos ? true : null,
            EjecutivoUsuarioId: Guid.TryParse(_ejecutivoFiltro, out var ejecutivoId) ? ejecutivoId : null,
            EstadoDocumental: Enum.TryParse<EstadoDocumento>(_estadoDocumentalFiltro, out var estado) ? estado : null,
            Pagina: (request.StartIndex / _paginacion.ItemsPerPage) + 1,
            TamanoPagina: _paginacion.ItemsPerPage,
            OrdenarPor: ordenarPor,
            Descendente: descendente);

        _cargando = true;
        _errorCarga = false;

        try
        {
            var resultado = await Mediator.Send(consulta, request.CancellationToken);

            if (carga != _cargaVigente)
                return GridItemsProviderResult.From(new List<ClienteListaDto>(), 0);

            _totalElementos = resultado.TotalElementos;

            var elementos = resultado.Elementos.ToList();
            _elementosPagina = elementos;
            _seleccionados.Clear();
            _idEnfocado = null;

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

    /// <summary>Filtros "Ejecutivo" y "Estado documental" del mockup "Lista Clientes TALVEG" — mismo patrón que SoloCriticos: estado local + recarga, sin URL propia porque no forman parte de ningún enlace compartido todavía.</summary>
    private async Task CambiarEjecutivoFiltroAsync(string valor)
    {
        _ejecutivoFiltro = valor;
        await RecargarAsync();
    }

    private async Task CambiarEstadoDocumentalFiltroAsync(string valor)
    {
        _estadoDocumentalFiltro = valor;
        await RecargarAsync();
    }

    // --- H4 (docs/ux-audit/02-clientes.md): chips de filtros activos ---

    private bool HayFiltrosActivos =>
        !string.IsNullOrWhiteSpace(_busqueda) || _soloCriticos
        || !string.IsNullOrWhiteSpace(_ejecutivoFiltro) || !string.IsNullOrWhiteSpace(_estadoDocumentalFiltro);

    private Task QuitarFiltroBusquedaAsync() => BuscarAsync(string.Empty);

    private Task QuitarFiltroCriticosAsync() => CambiarSoloCriticosAsync(false);

    private Task QuitarFiltroEjecutivoAsync() => CambiarEjecutivoFiltroAsync(string.Empty);

    private Task QuitarFiltroEstadoDocumentalAsync() => CambiarEstadoDocumentalFiltroAsync(string.Empty);

    private string EtiquetaFiltroEjecutivo =>
        "Ejecutivo: " + (_ejecutivosParaFiltro.FirstOrDefault(g => g.Id.ToString() == _ejecutivoFiltro)?.NombreCompleto ?? "—");

    /// <summary>Nombre a mostrar en la columna "Ejecutivo" de cada fila — mismo directorio que ya resuelve el filtro, sin consulta nueva por fila.</summary>
    private string ObtenerNombreEjecutivo(Guid? ejecutivoUsuarioId) =>
        ejecutivoUsuarioId is null
            ? "—"
            : _ejecutivosParaFiltro.FirstOrDefault(g => g.Id == ejecutivoUsuarioId)?.NombreCompleto ?? "—";

    private string EtiquetaFiltroEstadoDocumental =>
        "Estado: " + (Enum.TryParse<EstadoDocumento>(_estadoDocumentalFiltro, out var estado)
            ? (estado == EstadoDocumento.Vigente ? "Al corriente" : EstadoDocumentoUi.Texto(estado))
            : "—");

    /// <summary>
    /// Cuántos coinciden (mockup: «N clientes con el filtro actual»). Sin
    /// filtros no habla de ninguno; con filtros dice que el número es el de
    /// los que coinciden, no el de la cartera.
    /// </summary>
    private string TextoConteo
    {
        get
        {
            var sustantivo = _totalElementos == 1 ? "cliente" : "clientes";
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
    /// «12 vencidos», «1 vencido», «3 faltan»: el recuento de alertas en el
    /// peor estado, con el calificativo concordado. Antes se pintaba
    /// «12 vencido».
    /// </summary>
    private static string TextoEstadoDocumental(EstadoDocumento peor, int cantidad)
    {
        var uno = cantidad == 1;
        var calificativo = peor switch
        {
            EstadoDocumento.Vencido => uno ? "vencido" : "vencidos",
            EstadoDocumento.Urgente => uno ? "urgente" : "urgentes",
            EstadoDocumento.Proximo => uno ? "próximo" : "próximos",
            EstadoDocumento.Faltante => uno ? "falta" : "faltan",
            _ => EstadoDocumentoUi.Texto(peor).ToLowerInvariant()
        };
        return $"{cantidad} {calificativo}";
    }

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
    /// <b>Los dos filtros que viajan por la URL se limpian TAMBIÉN allí.</b>
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
        });
        await RecargarAsync();
    }

    private async Task RecargarAsync()
    {
        // SetCurrentPageIndexAsync no dispara una recarga si el índice no cambia
        // (p.ej. ya estábamos en la página 0), así que se refresca explícitamente.
        await _paginacion.SetCurrentPageIndexAsync(0);

        if (_grid is not null)
            await _grid.RefreshDataAsync();

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
    }

    private async Task AbrirEditarAsync(Guid id)
    {
        var edicion = ++_edicionVigente;

        var cliente = await Mediator.Send(new ObtenerClientePorIdQuery(id));
        if (edicion != _edicionVigente)
            return;

        if (cliente is null)
        {
            ToastService.Mostrar("No encontramos este cliente. Puede que ya se haya eliminado.", TonoToast.Error);
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
                    ToastService.Mostrar(resultadoReasignar.Error.Mensaje, TonoToast.Error);
            }

            ToastService.Mostrar(
                _editandoId is null ? "Cliente creado correctamente." : "Cliente actualizado correctamente.",
                TonoToast.Exito);

            _drawerVisible = false;

            if (continuarACrearEmpresa && clienteCreadoId is not null)
            {
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
    /// UX_PATTERNS.md, P1-18 de docs/business/MATURITY_REVIEW.md).
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

    private void AbrirEliminar(Guid id, string razonSocial)
    {
        _idAEliminar = id;
        _razonSocialAEliminar = razonSocial;
        _confirmarEliminarVisible = true;
    }

    private async Task ConfirmarEliminarAsync()
    {
        if (_eliminando)
            return;

        _eliminando = true;
        var idAEliminar = _idAEliminar;

        try
        {
            var resultado = await Mediator.Send(new EliminarClienteCommand(idAEliminar));

            if (resultado.EsFallido)
            {
                ToastService.Mostrar(resultado.Error.Mensaje, TonoToast.Error);
            }
            else
            {
                ToastService.Mostrar("Cliente eliminado correctamente.", TonoToast.Exito, "Deshacer", () => DeshacerEliminarAsync(idAEliminar));
                _confirmarEliminarVisible = false;
                await RecargarAsync();
            }
        }
        catch (Exception)
        {
            ToastService.Mostrar("No pudimos eliminar el cliente. Intenta nuevamente en unos segundos.", TonoToast.Error);
        }
        finally
        {
            _eliminando = false;
        }
    }

    /// <summary>Fase D ("Deshacer al eliminar") — acción del toast tras eliminar, ver RestaurarClienteCommand.</summary>
    private async Task DeshacerEliminarAsync(Guid id)
    {
        if (!_restaurando.Add(id))
            return;

        try
        {
            var resultado = await Mediator.Send(new RestaurarClienteCommand(id));

            ToastService.Mostrar(
                resultado.EsExitoso ? "Cliente restaurado." : resultado.Error.Mensaje,
                resultado.EsExitoso ? TonoToast.Exito : TonoToast.Error);

            if (resultado.EsExitoso)
                await RecargarAsync();
        }
        finally
        {
            _restaurando.Remove(id);
        }
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

    private async Task ConfirmarEliminarLoteAsync()
    {
        if (_eliminandoLote)
            return;

        _eliminandoLote = true;

        try
        {
            var resultado = await Mediator.Send(new EliminarClientesCommand(_seleccionados.ToList()));
            var dto = resultado.Valor;

            ToastService.Mostrar(
                dto.Errores.Count == 0
                    ? $"{dto.Eliminados} cliente(s) eliminado(s)."
                    : $"{dto.Eliminados} eliminado(s). {dto.Errores.Count} no se pudieron borrar: {string.Join(" ", dto.Errores)}",
                dto.Errores.Count == 0 ? TonoToast.Exito : TonoToast.Advertencia);

            _seleccionados.Clear();
            _confirmarEliminarLoteVisible = false;
            await RecargarAsync();
        }
        catch (Exception)
        {
            ToastService.Mostrar("No pudimos eliminar los clientes seleccionados. Intenta nuevamente.", TonoToast.Error);
        }
        finally
        {
            _eliminandoLote = false;
        }
    }

    // --- P3-31: atajos de teclado j/k/x/Enter ---

    private string ObtenerClaseFila(ClienteListaDto item) => item.Id == _idEnfocado ? "fila-enfocada" : "";

    private async Task ManejarAtajoAsync(string tecla)
    {
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
                if (_idEnfocado is { } idAlternar)
                    AlternarSeleccion(idAlternar, !_seleccionados.Contains(idAlternar));
                break;
            case "Enter":
                if (_idEnfocado is { } idAbrir)
                    AbrirPreview(idAbrir);
                break;
        }

        StateHasChanged();
    }

    // --- P3-31: filtros guardados ---

    /// <summary>
    /// Aplicar un filtro guardado sustituye los cuatro filtros por los suyos y
    /// escribe en la URL los dos que viajan por ella. Antes solo cambiaba los
    /// campos en memoria: la URL seguía con el <c>q</c>/<c>critico</c> anterior
    /// y la siguiente pasada de <see cref="OnParametersSetAsync"/> (cualquier
    /// otro filtro que escribiera en la URL) devolvía la búsqueda vieja.
    /// </summary>
    private async Task AplicarFiltroGuardadoAsync(string idTexto)
    {
        if (!Guid.TryParse(idTexto, out var id)) return;

        var filtro = _filtrosGuardados.FirstOrDefault(f => f.Id == id);
        if (filtro is null) return;

        var valores = JsonSerializer.Deserialize<FiltrosClientesJson>(filtro.ValoresJson);
        if (valores is null) return;

        _busqueda = valores.Busqueda ?? string.Empty;
        _soloCriticos = valores.SoloCriticos;
        // Un ejecutivo que ya no está en el directorio visible no se repone:
        // filtraría por alguien que la pantalla no puede nombrar («Ejecutivo: —»).
        _ejecutivoFiltro = _ejecutivosParaFiltro.Any(g => g.Id.ToString() == valores.GestorCaeId)
            ? valores.GestorCaeId!
            : string.Empty;
        _estadoDocumentalFiltro = Enum.TryParse<EstadoDocumento>(valores.EstadoDocumental, out _)
            ? valores.EstadoDocumental!
            : string.Empty;

        NavigationManager.ActualizarFiltrosEnUrl(new Dictionary<string, string?>
        {
            ["q"] = _busqueda,
            ["critico"] = _soloCriticos ? "true" : null,
        });
        await RecargarAsync();
    }

    private void CerrarModalGuardarFiltro(bool visible)
    {
        _mostrarGuardarFiltro = visible;
        if (!visible)
            QuitarAccionDeLaUrl();
    }

    private async Task GuardarFiltroActualAsync()
    {
        if (_guardandoFiltro || string.IsNullOrWhiteSpace(_nombreFiltroNuevo)) return;

        _guardandoFiltro = true;

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
                ToastService.Mostrar(resultado.Error.Mensaje, TonoToast.Error);
                return;
            }

            _filtrosGuardados = await Mediator.Send(new ObtenerFiltrosGuardadosQuery(PantallasConFiltrosGuardados.Clientes));
            _nombreFiltroNuevo = string.Empty;
            CerrarModalGuardarFiltro(false);
            ToastService.Mostrar("Filtro guardado.", TonoToast.Exito);
        }
        catch (Exception)
        {
            ToastService.Mostrar("No pudimos guardar el filtro. Intenta nuevamente en unos segundos.", TonoToast.Error);
        }
        finally
        {
            _guardandoFiltro = false;
        }
    }

    private void PedirEliminarFiltroGuardado(FiltroGuardadoDto filtro) => _filtroGuardadoAEliminar = filtro;

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
                ToastService.Mostrar(resultado.Error.Mensaje, TonoToast.Error);
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
}
