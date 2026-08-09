using CaeManager.Application.Centros;
using CaeManager.Application.Centros.Commands.CrearCentro;
using CaeManager.Application.Centros.Commands.EliminarCentros;
using CaeManager.Application.Centros.Queries.ObtenerCentros;
using CaeManager.Application.Clientes.Queries.ObtenerClientesParaSelector;
using CaeManager.Application.Empresas.Queries.ObtenerEmpresasParaSelector;
using CaeManager.Application.Visitas.Queries.ObtenerProximaVisitaPorCentro;
using CaeManager.Domain.Centros;
using CaeManager.Web.Components;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Components.Workspace;
using CaeManager.Web.Features.Clientes.Components;
using CaeManager.Web.Features.Empresas.Components;
using FluentValidation;
using Microsoft.AspNetCore.Components;

namespace CaeManager.Web.Features.Centros.Pages;

public partial class Centros : ComponentBase
{
    // QuickGrid no soporta filas expandibles (Centro 360, PLAN-EJECUCION-UX.md
    // § 0.1): cada Centro es una tarjeta con SeccionColapsable anidada, así
    // que la paginación se gestiona a mano en vez de con QuickGrid+Paginator
    // — la Query sigue paginando en servidor, solo cambia el control visual
    // (mismo PaginadorSimple que Usuarios.razor).
    private int _tamanoPagina = 20;

    private string _busqueda = string.Empty;
    private string _estadoFiltro = string.Empty;
    private Guid? _centroIdFiltro;
    private bool _cargando = true;
    private bool _errorCarga;
    private int _totalElementos;
    private int _pagina = 1;

    private int TotalPaginas => Math.Max(1, (int)Math.Ceiling(_totalElementos / (double)_tamanoPagina));

    private IReadOnlyDictionary<Guid, IReadOnlyList<VisitaResumenDto>> _visitasPorCentro = new Dictionary<Guid, IReadOnlyList<VisitaResumenDto>>();

    private IReadOnlyList<ClienteSelectorDto> _clientesDisponibles = [];
    private IReadOnlyList<EmpresaSelectorDto> _empresasDisponibles = [];

    private bool _drawerVisible;
    private string _clienteId = string.Empty;
    private string _empresaId = string.Empty;
    // Cliente/Empresa llegaron ya fijados desde el encadenado de otra
    // pantalla (Fase A2) — se muestran en solo lectura hasta que el usuario
    // pulse "cambiar".
    private bool _padresFijadosPorCadena;
    private string _clienteNombreSoloLectura = string.Empty;
    private string _empresaNombreSoloLectura = string.Empty;
    private string _nombre = string.Empty;
    private string _codigoCentro = string.Empty;
    private string _direccion = string.Empty;
    private string _contacto = string.Empty;
    private string _contratoVigenteHasta = string.Empty;
    private bool _guardando;
    private string? _mensajeErrorFormulario;
    private Dictionary<string, string> _erroresCampo = new();

    private readonly HashSet<Guid> _seleccionados = [];

    /// <summary>
    /// Los checkboxes de fila solo se pintan con esto activo (§ 0.9) — son
    /// ruido permanente para una acción ocasional.
    /// </summary>
    private bool _seleccionMultiple;

    /// <summary>
    /// Qué filas tienen el acordeón abierto. La expansión la lleva la página
    /// y no cada <c>SeccionColapsable</c> con su estado interno, porque
    /// "Expandir/Colapsar todos" necesita poder decidirlo desde fuera.
    /// </summary>
    private readonly HashSet<Guid> _expandidos = [];
    private List<CentroListaDto> _elementosPagina = [];
    private Guid? _idEnfocado;
    private bool _eliminandoLote;
    private bool _confirmarEliminarLoteVisible;

    // Crear inline desde el propio selector (Fase A4): si el Cliente o la
    // Empresa que hace falta no existen todavía, no hay que abandonar el
    // Drawer para ir a darlos de alta.
    private bool _formularioRapidoClienteVisible;
    private string _nombreParaCrearCliente = string.Empty;
    private bool _formularioRapidoEmpresaVisible;
    private string _nombreParaCrearEmpresa = string.Empty;

    private IReadOnlyList<OpcionBuscable> ClientesComoOpciones =>
        _clientesDisponibles.Select(c => new OpcionBuscable(c.Id.ToString(), c.RazonSocial)).ToList();

    private IReadOnlyList<OpcionBuscable> EmpresasComoOpciones =>
        _empresasDisponibles.Select(e => new OpcionBuscable(e.Id.ToString(), e.RazonSocial)).ToList();

    private Guid? ClienteIdParaFormularioRapido => Guid.TryParse(_clienteId, out var id) ? id : null;

    [SupplyParameterFromQuery(Name = "q")]
    public string? TerminoBusquedaInicial { get; set; }

    [SupplyParameterFromQuery(Name = "estado")]
    public string? EstadoInicial { get; set; }

    [Inject] private NavigationManager NavigationManager { get; set; } = default!;

    // Reutilizan las mismas reglas que ya corren en el servidor al guardar
    // (misma validación, sin duplicarla) — solo se les pide que validen un
    // único campo, no el Command completo, porque el resto del formulario
    // puede seguir a medio rellenar mientras el usuario todavía está en él.
    [Inject] private IValidator<CrearCentroCommand> ValidadorCrear { get; set; } = default!;

    /// <summary>Comando del palette "Crear centro «nombre»" (P3-31): abre el Drawer con el nombre precargado.</summary>
    [SupplyParameterFromQuery] public string? Accion { get; set; }
    [SupplyParameterFromQuery] public string? Nombre { get; set; }

    /// <summary>
    /// Encadenado desde "Continuar con el centro" en /empresas, o desde el
    /// asistente de alta guiada (Fase A2/A3): el Cliente y la Empresa llegan
    /// ya elegidos, en solo lectura, para no repetir la búsqueda.
    /// </summary>
    [SupplyParameterFromQuery] public Guid? ClienteId { get; set; }
    [SupplyParameterFromQuery] public Guid? EmpresaId { get; set; }

    /// <summary>
    /// Drill-down desde el desplegable de Centros con actividad de una
    /// Empresa (Centro 360, PLAN-EJECUCION-UX.md § 0.11) — filtro exacto por
    /// Id, no reutiliza <c>q</c> (texto libre) porque un nombre parecido
    /// entre Centros distintos haría el prefiltro ambiguo.
    /// </summary>
    [SupplyParameterFromQuery] public Guid? CentroId { get; set; }

    protected override async Task OnInitializedAsync()
    {
        _busqueda = TerminoBusquedaInicial ?? string.Empty;
        _estadoFiltro = Enum.TryParse<EstadoCentro>(EstadoInicial, out _) ? EstadoInicial! : string.Empty;
        _centroIdFiltro = CentroId;
        await CargarAsync();

        if (Accion == "crear")
        {
            await AbrirCrearAsync();
            if (!string.IsNullOrWhiteSpace(Nombre))
                _nombre = Nombre;

            if (ClienteId is not null && _clientesDisponibles.Any(c => c.Id == ClienteId))
            {
                _clienteId = ClienteId.Value.ToString();
                _clienteNombreSoloLectura = _clientesDisponibles.First(c => c.Id == ClienteId).RazonSocial;
                await CargarEmpresasDisponiblesAsync(ClienteId);

                if (EmpresaId is not null && _empresasDisponibles.Any(e => e.Id == EmpresaId))
                {
                    _empresaId = EmpresaId.Value.ToString();
                    _empresaNombreSoloLectura = _empresasDisponibles.First(e => e.Id == EmpresaId).RazonSocial;
                    _padresFijadosPorCadena = true;
                }
            }
        }
    }

    /// <summary>
    /// Se re-ejecuta en cada navegación dentro de la propia página (recargar,
    /// compartir la URL, volver atrás) — no solo en el primer render — para
    /// que el filtro de la URL sea la fuente de verdad, no solo su semilla
    /// inicial (P1-18 de docs/business/MATURITY_REVIEW.md). El primer paso
    /// (justo después de OnInitializedAsync) siempre coincide con lo que ya
    /// se cargó ahí, así que esto no duplica la primera consulta.
    /// </summary>
    protected override async Task OnParametersSetAsync()
    {
        var deLaUrl = TerminoBusquedaInicial ?? string.Empty;
        var estadoDeLaUrl = Enum.TryParse<EstadoCentro>(EstadoInicial, out _) ? EstadoInicial! : string.Empty;

        if (deLaUrl == _busqueda && estadoDeLaUrl == _estadoFiltro && CentroId == _centroIdFiltro)
            return;

        _busqueda = deLaUrl;
        _estadoFiltro = estadoDeLaUrl;
        _centroIdFiltro = CentroId;
        await CargarAsync(resetPagina: true);
    }

    private async Task CargarAsync(bool resetPagina = false)
    {
        if (resetPagina)
            _pagina = 1;

        _cargando = true;
        _errorCarga = false;
        StateHasChanged();

        try
        {
            var resultado = await Mediator.Send(new ObtenerCentrosQuery(
                Busqueda: string.IsNullOrWhiteSpace(_busqueda) ? null : _busqueda,
                ClienteId: null,
                Estado: Enum.TryParse<EstadoCentro>(_estadoFiltro, out var estado) ? estado : null,
                OrdenarPor: _ordenarPor,
                Descendente: _ordenDescendente,
                Pagina: _pagina,
                TamanoPagina: _tamanoPagina,
                CentroId: _centroIdFiltro));

            _totalElementos = resultado.TotalElementos;
            _elementosPagina = resultado.Elementos.ToList();
            _seleccionados.Clear();
            _expandidos.Clear();
            _idEnfocado = null;

            // Batch por página (no por fila) — mismo criterio que el badge de
            // cumplimiento: evita N+1 aunque solo se pinte cuando hay visita.
            _visitasPorCentro = _elementosPagina.Count == 0
                ? new Dictionary<Guid, IReadOnlyList<VisitaResumenDto>>()
                : await Mediator.Send(new ObtenerProximaVisitaPorCentroQuery(_elementosPagina.Select(c => c.Id).ToList()));
        }
        catch (Exception)
        {
            _errorCarga = true;
        }
        finally
        {
            _cargando = false;
            StateHasChanged();
        }
    }

    private Task CambiarPaginaAsync(int pagina)
    {
        _pagina = pagina;
        return CargarAsync();
    }

    /// <summary>
    /// Tras guardar un documento in situ desde el acordeón (Gestionar, item 3
    /// del backlog Centro 360) hace falta refrescar el estado/recuentos de
    /// ESA fila — pero CargarAsync() completo colapsaría el acordeón que el
    /// usuario acaba de usar (limpia _expandidos). Un CentroId filtrado
    /// devuelve solo esa fila y se sustituye en sitio, sin tocar expansión ni
    /// selección ni paginación.
    /// </summary>
    private async Task RefrescarCentroAsync(Guid centroId)
    {
        var resultado = await Mediator.Send(new ObtenerCentrosQuery(Busqueda: null, ClienteId: null, CentroId: centroId));
        var actualizado = resultado.Elementos.FirstOrDefault();
        if (actualizado is null) return;

        var indice = _elementosPagina.FindIndex(c => c.Id == centroId);
        if (indice >= 0)
            _elementosPagina[indice] = actualizado;

        StateHasChanged();
    }

    // H5 (docs/ux-audit/05-trabajadores-vehiculos.md): selector de tamaño de página, compartido por PaginadorSimple.razor.
    private Task CambiarTamanoPaginaAsync(int tamano)
    {
        _tamanoPagina = tamano;
        return CargarAsync(resetPagina: true);
    }

    private async Task BuscarAsync(string valor)
    {
        _busqueda = valor;
        NavigationManager.ActualizarFiltroEnUrl("q", valor);
        await CargarAsync(resetPagina: true);
    }

    private async Task CambiarEstadoAsync(string valor)
    {
        _estadoFiltro = valor;
        NavigationManager.ActualizarFiltroEnUrl("estado", valor);
        await CargarAsync(resetPagina: true);
    }

    private async Task AbrirCrearAsync()
    {
        _clientesDisponibles = await Mediator.Send(new ObtenerClientesParaSelectorQuery());

        _clienteId = string.Empty;
        _empresaId = string.Empty;
        _padresFijadosPorCadena = false;
        _nombre = string.Empty;
        _codigoCentro = string.Empty;
        _direccion = string.Empty;
        _contacto = string.Empty;
        _contratoVigenteHasta = string.Empty;
        _erroresCampo = new Dictionary<string, string>();
        _mensajeErrorFormulario = null;

        // Sin Cliente elegido todavía no hay por qué acotar: se carga el
        // catálogo completo y CambiarClienteCreacionAsync lo recorta en
        // cuanto el usuario elige uno (Fase A3 — antes se cargaban siempre
        // todas las empresas de todos los clientes, aunque la Query ya sabía
        // filtrar por ClienteId).
        await CargarEmpresasDisponiblesAsync(null);

        _drawerVisible = true;
    }

    /// <summary>
    /// Recarga el selector de Empresa acotado al Cliente elegido — o al
    /// catálogo completo si todavía no hay Cliente. Se llama al abrir el
    /// Drawer y cada vez que el usuario cambia el Cliente en modo creación.
    /// </summary>
    private async Task CargarEmpresasDisponiblesAsync(Guid? clienteId)
    {
        _empresasDisponibles = await Mediator.Send(new ObtenerEmpresasParaSelectorQuery(clienteId));
    }

    /// <summary>
    /// Solo aplica en modo creación con los padres editables (no fijados por
    /// una cadena de otra pantalla): al cambiar el Cliente, el selector de
    /// Empresa se recorta a las suyas — si la Empresa que estaba elegida ya
    /// no pertenece al nuevo Cliente, se limpia en vez de dejar una
    /// combinación imposible.
    /// </summary>
    private async Task CambiarClienteCreacionAsync(string valor)
    {
        _clienteId = valor;

        var clienteId = Guid.TryParse(valor, out var id) ? id : (Guid?)null;
        await CargarEmpresasDisponiblesAsync(clienteId);

        if (!_empresasDisponibles.Any(e => e.Id.ToString() == _empresaId))
            _empresaId = string.Empty;
    }

    private Task CerrarDrawerAsync(bool visible)
    {
        _drawerVisible = visible;
        return Task.CompletedTask;
    }

    /// <summary>"Cambiar cliente/empresa" en un Centro llegado ya fijado por una cadena (Fase A2) — vuelve a los selectores editables con el catálogo completo.</summary>
    private async Task DesvincularPadresFijadosAsync()
    {
        _padresFijadosPorCadena = false;
        _clienteId = string.Empty;
        _empresaId = string.Empty;
        await CargarEmpresasDisponiblesAsync(null);
    }

    private void AbrirCrearClienteInline(string texto)
    {
        _nombreParaCrearCliente = texto;
        _formularioRapidoClienteVisible = true;
    }

    /// <summary>Un Cliente recién creado no tiene ninguna Empresa todavía — CambiarClienteCreacionAsync ya deja el selector de Empresa vacío y listo para su propio "+ Crear".</summary>
    private async Task ManejarClienteCreadoAsync(ClienteCreadoDto creado)
    {
        _clientesDisponibles = [.. _clientesDisponibles, new ClienteSelectorDto(creado.Id, creado.RazonSocial)];
        await CambiarClienteCreacionAsync(creado.Id.ToString());
        ToastService.Mostrar("Cliente creado correctamente.", TonoToast.Exito);
    }

    private void AbrirCrearEmpresaInline(string texto)
    {
        _nombreParaCrearEmpresa = texto;
        _formularioRapidoEmpresaVisible = true;
    }

    private Task ManejarEmpresaCreadaAsync(EmpresaCreadaDto creada)
    {
        _empresasDisponibles = [.. _empresasDisponibles, new EmpresaSelectorDto(creada.Id, creada.RazonSocial)];
        _empresaId = creada.Id.ToString();
        ToastService.Mostrar("Empresa creada correctamente.", TonoToast.Exito);
        return Task.CompletedTask;
    }

    private Task GuardarAsync() => GuardarAsync(crearOtro: false);

    /// <summary>
    /// "Añadir otro centro" (Fase A2): igual que
    /// <see cref="GuardarAsync()"/>, pero al crear con éxito no cierra el
    /// Drawer — limpia solo los campos propios del Centro y mantiene
    /// Cliente/Empresa fijados, para dar de alta varios centros seguidos sin
    /// repetir la búsqueda.
    /// </summary>
    private Task GuardarYCrearOtroAsync() => GuardarAsync(crearOtro: true);

    private async Task GuardarAsync(bool crearOtro)
    {
        _guardando = true;
        _mensajeErrorFormulario = null;
        _erroresCampo = new Dictionary<string, string>();

        try
        {
            var codigoCentro = string.IsNullOrWhiteSpace(_codigoCentro) ? null : _codigoCentro;
            var direccion = string.IsNullOrWhiteSpace(_direccion) ? null : _direccion;
            var contacto = string.IsNullOrWhiteSpace(_contacto) ? null : _contacto;
            var contratoVigenteHasta = DateOnly.TryParse(_contratoVigenteHasta, out var fecha) ? fecha : (DateOnly?)null;

            if (!Guid.TryParse(_clienteId, out var clienteId))
            {
                _mensajeErrorFormulario = "Selecciona un cliente.";
                return;
            }

            if (!Guid.TryParse(_empresaId, out var empresaId))
            {
                _mensajeErrorFormulario = "Selecciona una empresa.";
                return;
            }

            var resultado = await Mediator.Send(
                new CrearCentroCommand(clienteId, empresaId, _nombre, codigoCentro, direccion, contacto, contratoVigenteHasta));

            if (resultado.EsFallido)
            {
                _mensajeErrorFormulario = resultado.Error.Mensaje;
                return;
            }

            ToastService.Mostrar("Centro creado correctamente.", TonoToast.Exito);

            if (crearOtro)
            {
                _nombre = string.Empty;
                _codigoCentro = string.Empty;
                _direccion = string.Empty;
                _contacto = string.Empty;
                _contratoVigenteHasta = string.Empty;
                _erroresCampo = new Dictionary<string, string>();
                await CargarAsync();
                return;
            }

            _drawerVisible = false;
            await CargarAsync();
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
    /// Validación inline al salir del campo (UX_PATTERNS.md, P1-18 de
    /// docs/business/MATURITY_REVIEW.md) — hasta ahora el error de "nombre
    /// obligatorio" solo aparecía tras el viaje de ida y vuelta al servidor
    /// en Guardar. Valida solo <see cref="CrearCentroCommand.Nombre"/> con
    /// el mismo validador que ya corre al guardar — el resto del formulario
    /// puede seguir incompleto sin que este campo lo bloquee.
    /// </summary>
    private async Task ValidarNombreAsync()
    {
        const string campo = nameof(CrearCentroCommand.Nombre);

        var resultado = await ValidadorCrear.ValidateAsync(
            new CrearCentroCommand(Guid.Empty, Guid.Empty, _nombre, null, null, null, null),
            opciones => opciones.IncludeProperties(campo));

        if (resultado.IsValid)
            _erroresCampo.Remove(campo);
        else
            _erroresCampo[campo] = resultado.Errors[0].ErrorMessage;
    }

    private bool TodosSeleccionados =>
        _elementosPagina.Count > 0 && _elementosPagina.All(e => _seleccionados.Contains(e.Id));

    /// <summary>
    /// Apagar el modo limpia la selección (PLAN-EJECUCION-UX.md § 0.9):
    /// dejar filas marcadas que ya no se ven dejaría la barra de acciones en
    /// lote apuntando a algo invisible.
    /// </summary>
    private void AlternarSeleccionMultiple(bool activa)
    {
        _seleccionMultiple = activa;
        if (!activa)
            _seleccionados.Clear();
    }

    private bool TodosExpandidos =>
        _elementosPagina.Count > 0 && _elementosPagina.All(e => _expandidos.Contains(e.Id));

    private void AlternarExpansion(Guid id)
    {
        if (!_expandidos.Add(id))
            _expandidos.Remove(id);
    }

    private void AlternarTodosExpandidos(bool expandir)
    {
        if (expandir)
            foreach (var elemento in _elementosPagina) _expandidos.Add(elemento.Id);
        else
            _expandidos.Clear();
    }

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
        _eliminandoLote = true;

        try
        {
            var usuarioId = await CurrentUserService.ObtenerUsuarioActualIdAsync();
            var resultado = await Mediator.Send(new EliminarCentrosCommand(_seleccionados.ToList(), usuarioId ?? Guid.Empty));
            var dto = resultado.Valor;

            ToastService.Mostrar(
                dto.Errores.Count == 0
                    ? $"{dto.Eliminados} centro(s) eliminado(s)."
                    : $"{dto.Eliminados} eliminado(s). {dto.Errores.Count} no se pudieron borrar: {string.Join(" ", dto.Errores)}",
                dto.Errores.Count == 0 ? TonoToast.Exito : TonoToast.Advertencia);

            _seleccionados.Clear();
            _confirmarEliminarLoteVisible = false;
            await CargarAsync();
        }
        catch (Exception)
        {
            ToastService.Mostrar("No pudimos eliminar los centros seleccionados. Intenta nuevamente.", TonoToast.Error);
        }
        finally
        {
            _eliminandoLote = false;
        }
    }

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
                {
                    var elemento = _elementosPagina.FirstOrDefault(e => e.Id == idAbrir);
                    if (elemento is not null)
                        await WorkspaceService.AbrirAsync(EntidadWorkspace.Centro, elemento.Id, elemento.Nombre, "informacion");
                }
                break;
        }

        StateHasChanged();
    }
    /// <summary>
    /// Orden de la lista. Por defecto null: cliente y luego nombre, el orden de
    /// catalogo. El orden por cumplimiento se pide expresamente (blueprint
    /// seccion 3.1, DDL-036) y existe para atacar los peores centros sin
    /// depender de que haya una visita proxima.
    /// </summary>
    private string? _ordenarPor;
    private bool _ordenDescendente;

    private async Task CambiarOrdenAsync(string? ordenarPor)
    {
        // Volver a pedir el mismo orden invierte el sentido: es el gesto que
        // el usuario ya conoce de cualquier cabecera de tabla.
        if (string.Equals(_ordenarPor, ordenarPor, StringComparison.Ordinal))
        {
            _ordenDescendente = !_ordenDescendente;
        }
        else
        {
            _ordenarPor = ordenarPor;
            _ordenDescendente = false;
        }

        _pagina = 1;
        await CargarAsync();
    }

    /// <summary>
    /// Incidencias del ambito Empresa de un centro. Se derivan de los recuentos
    /// que la fila ya tiene: el bloque Empresa del acordeon no lanza ninguna
    /// consulta propia (OD-13, blueprint seccion 3.3).
    /// </summary>
    private static IReadOnlyList<IncidenciaCentroDto> IncidenciasDeEmpresa(CentroListaDto centro) =>
        centro.Recuentos.Vencidas.Concat(centro.Recuentos.Proximas)
            .Where(i => i.Ambito == AmbitoCausa.Empresa)
            .ToList();

    /// <summary>
    /// 3ª ranura de indicadores del Centro (contrato de fidelidad 2026-08-09
    /// § 1.6, bug C-2), cuando no hay visita que mostrar en su lugar. El
    /// Badge de Estado solo aporta algo que los recuentos de vencidos/
    /// próximos no digan ya: Vencido/Próximo/Falta documentación quedan
    /// reflejados en esas dos ranuras (mismo criterio de bucketing que
    /// <c>ObtenerCentrosQuery.Desglosar</c>), así que repetirlos aquí era
    /// justo la redundancia que hacía desbordar la columna. Bloqueado
    /// (el peor caso posible) y Urgente (que Desglosar no cuenta en ningún
    /// recuento) sí necesitan la ranura; "sin incidencias" (0 y 0) también,
    /// para que la fila no quede completamente muda.
    /// </summary>
    private static bool MostrarEstadoEnIndicadores(CentroListaDto centro) =>
        centro.Estado is EstadoCentro.Bloqueado or EstadoCentro.Urgente
        || (centro.Recuentos.TotalVencidas == 0 && centro.Recuentos.TotalProximas == 0);

    /// <summary>
    /// Nombre accesible de un badge de solo recuento. Es lo unico que oye un
    /// lector de pantalla, asi que dice el total Y el reparto por ambito: un
    /// numero desnudo deja el color como unico portador de significado, que es
    /// lo que 02 seccion 8 prohibe (DDL-033).
    /// </summary>
    private static string DescribirRecuento(IReadOnlyList<IncidenciaCentroDto> incidencias, string calificativo)
    {
        var deEmpresa = incidencias.Count(i => i.Ambito == AmbitoCausa.Empresa);
        var deTrabajadores = incidencias.Count - deEmpresa;
        var cabeza = incidencias.Count == 1
            ? $"1 documento {calificativo}"
            : $"{incidencias.Count} documentos {(calificativo.EndsWith('o') ? calificativo + "s" : calificativo)}";

        var partes = new List<string>();
        if (deEmpresa > 0) partes.Add(deEmpresa == 1 ? "1 de empresa" : $"{deEmpresa} de empresa");
        if (deTrabajadores > 0) partes.Add(deTrabajadores == 1 ? "1 de trabajadores" : $"{deTrabajadores} de trabajadores");

        return partes.Count == 0 ? cabeza : $"{cabeza}: {string.Join(" y ", partes)}";
    }

    /// <summary>
    /// Detalle visible de la ventana, agrupado por ambito. La agrupacion no es
    /// estetica: el blueprint seccion 3.2 exige que el desglose declare de
    /// quien es cada incidencia, porque el recuento agrega los dos ambitos
    /// (DDL-031, DDL-047).
    /// </summary>
    private static RenderFragment DesgloseIncidencias(IReadOnlyList<IncidenciaCentroDto> incidencias) => builder =>
    {
        // Numeros de secuencia LITERALES, no una variable incrementada
        // (ASP0006): el analizador de Blazor lo senala porque el numero debe
        // ser constante para la misma posicion del codigo fuente, no
        // depender de cuantas iteraciones se ejecutaron antes. Reutilizar el
        // mismo literal en cada vuelta del foreach es el patron que la
        // documentacion de Blazor recomienda para bucles de longitud
        // variable — el diffing usa la posicion en el arbol, no un contador
        // unico por elemento.
        foreach (var grupo in new[] { AmbitoCausa.Empresa, AmbitoCausa.Trabajador })
        {
            var deEsteAmbito = incidencias.Where(i => i.Ambito == grupo).ToList();
            if (deEsteAmbito.Count == 0) continue;

            builder.OpenElement(0, "span");
            builder.AddAttribute(1, "class", "ventana-linea ventana-grupo");
            builder.AddContent(2, grupo == AmbitoCausa.Empresa ? "Empresa" : "Trabajadores");
            builder.CloseElement();

            foreach (var incidencia in deEsteAmbito)
            {
                builder.OpenElement(3, "span");
                builder.AddAttribute(4, "class", "ventana-linea");
                builder.AddContent(5, incidencia.Descripcion);
                builder.CloseElement();
            }
        }
    };
}
