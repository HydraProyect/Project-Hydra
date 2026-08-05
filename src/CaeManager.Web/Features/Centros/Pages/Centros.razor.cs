using CaeManager.Application.Centros.Commands.CrearCentro;
using CaeManager.Application.Centros.Commands.EditarCentro;
using CaeManager.Application.Centros.Commands.EliminarCentro;
using CaeManager.Application.Centros.Commands.EliminarCentros;
using CaeManager.Application.Centros.Queries.ObtenerCentroPorId;
using CaeManager.Application.Centros.Queries.ObtenerCentros;
using CaeManager.Application.Clientes.Queries.ObtenerClientesParaSelector;
using CaeManager.Application.Empresas.Queries.ObtenerEmpresasParaSelector;
using CaeManager.Domain.Centros;
using CaeManager.Web.Components;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Components.Workspace;
using CaeManager.Web.Features.Clientes.Components;
using CaeManager.Web.Features.Empresas.Components;
using FluentValidation;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.QuickGrid;

namespace CaeManager.Web.Features.Centros.Pages;

public partial class Centros : ComponentBase
{
    private readonly PaginationState _paginacion = new() { ItemsPerPage = 20 };
    private QuickGrid<CentroListaDto>? _grid;

    private string _busqueda = string.Empty;
    private string _estadoFiltro = string.Empty;
    private bool _cargando = true;
    private bool _errorCarga;
    private int _totalElementos;

    private IReadOnlyList<ClienteSelectorDto> _clientesDisponibles = [];
    private IReadOnlyList<EmpresaSelectorDto> _empresasDisponibles = [];

    private bool _drawerVisible;
    private Guid? _editandoId;
    // Version del registro tal como se abrio: vuelve en el Command para
    // detectar que otra persona guardo mientras el formulario estaba abierto.
    private Guid _versionEditando;
    private string _clienteId = string.Empty;
    private string _clienteNombreSoloLectura = string.Empty;
    private string _empresaId = string.Empty;
    private string _empresaNombreSoloLectura = string.Empty;
    // Cliente/Empresa llegaron ya fijados desde el encadenado de otra
    // pantalla (Fase A2) — se muestran en solo lectura hasta que el usuario
    // pulse "cambiar". Distinto de _editandoId is null: en edición ambos son
    // siempre de solo lectura (no se puede recolocar un centro existente).
    private bool _padresFijadosPorCadena;
    private string _nombre = string.Empty;
    private string _codigoCentro = string.Empty;
    private string _direccion = string.Empty;
    private string _contacto = string.Empty;
    private string _contratoVigenteHasta = string.Empty;
    private bool _guardando;
    private string? _mensajeErrorFormulario;
    private Dictionary<string, string> _erroresCampo = new();

    private bool _confirmarEliminarVisible;
    private Guid _idAEliminar;
    private string _nombreAEliminar = string.Empty;
    private bool _eliminando;

    private readonly HashSet<Guid> _seleccionados = [];
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
    [Inject] private IValidator<EditarCentroCommand> ValidadorEditar { get; set; } = default!;

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

    private GridItemsProvider<CentroListaDto>? _proveedorElementos;

    protected override async Task OnInitializedAsync()
    {
        // Delegado estable — ver Clientes.razor.cs (bucle de recargas de QuickGrid).
        _proveedorElementos = ProveerElementosAsync;

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
    /// inicial (P1-18 de docs/business/MATURITY_REVIEW.md).
    /// </summary>
    protected override void OnParametersSet()
    {
        var deLaUrl = TerminoBusquedaInicial ?? string.Empty;
        if (deLaUrl != _busqueda)
            _busqueda = deLaUrl;

        var estadoDeLaUrl = Enum.TryParse<EstadoCentro>(EstadoInicial, out _) ? EstadoInicial! : string.Empty;
        if (estadoDeLaUrl != _estadoFiltro)
            _estadoFiltro = estadoDeLaUrl;
    }

    private async ValueTask<GridItemsProviderResult<CentroListaDto>> ProveerElementosAsync(
        GridItemsProviderRequest<CentroListaDto> request)
    {
        _cargando = true;
        _errorCarga = false;

        try
        {
            var pagina = (request.StartIndex / _paginacion.ItemsPerPage) + 1;
            var (ordenarPor, descendente) = LecturaOrden.Leer(request);

            var resultado = await Mediator.Send(new ObtenerCentrosQuery(
                Busqueda: string.IsNullOrWhiteSpace(_busqueda) ? null : _busqueda,
                ClienteId: null,
                Estado: Enum.TryParse<EstadoCentro>(_estadoFiltro, out var estado) ? estado : null,
                OrdenarPor: ordenarPor,
                Descendente: descendente,
                Pagina: pagina,
                TamanoPagina: _paginacion.ItemsPerPage));

            _totalElementos = resultado.TotalElementos;

            var elementos = resultado.Elementos.ToList();
            _elementosPagina = elementos;
            _seleccionados.Clear();
            _idEnfocado = null;

            return GridItemsProviderResult.From(elementos, resultado.TotalElementos);
        }
        catch (Exception)
        {
            _errorCarga = true;
            return GridItemsProviderResult.From(new List<CentroListaDto>(), 0);
        }
        finally
        {
            _cargando = false;
            StateHasChanged();
        }
    }

    private async Task BuscarAsync(string valor)
    {
        _busqueda = valor;
        NavigationManager.ActualizarFiltroEnUrl("q", valor);
        await RecargarAsync();
    }

    private async Task CambiarEstadoAsync(string valor)
    {
        _estadoFiltro = valor;
        NavigationManager.ActualizarFiltroEnUrl("estado", valor);
        await RecargarAsync();
    }

    private async Task RecargarAsync()
    {
        await _paginacion.SetCurrentPageIndexAsync(0);

        if (_grid is not null)
            await _grid.RefreshDataAsync();

        StateHasChanged();
    }

    private async Task AbrirCrearAsync()
    {
        _clientesDisponibles = await Mediator.Send(new ObtenerClientesParaSelectorQuery());

        _editandoId = null;
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

    private async Task AbrirEditarAsync(Guid id)
    {
        var centro = await Mediator.Send(new ObtenerCentroPorIdQuery(id));
        if (centro is null)
        {
            ToastService.Mostrar("No encontramos este centro. Puede que ya se haya eliminado.", TonoToast.Error);
            await RecargarAsync();
            return;
        }

        _editandoId = centro.Id;
        _versionEditando = centro.Version;
        _clienteId = centro.ClienteId.ToString();
        _clienteNombreSoloLectura = centro.ClienteRazonSocial;
        _empresaId = centro.EmpresaId.ToString();
        _empresaNombreSoloLectura = centro.EmpresaRazonSocial;
        _nombre = centro.Nombre;
        _codigoCentro = centro.CodigoCentro ?? string.Empty;
        _direccion = centro.Direccion ?? string.Empty;
        _contacto = centro.Contacto ?? string.Empty;
        _contratoVigenteHasta = centro.ContratoVigenteHasta?.ToString("yyyy-MM-dd") ?? string.Empty;
        _erroresCampo = new Dictionary<string, string>();
        _mensajeErrorFormulario = null;
        _drawerVisible = true;
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

            string? mensajeError;

            if (_editandoId is null)
            {
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
                mensajeError = resultado.EsFallido ? resultado.Error.Mensaje : null;
            }
            else
            {
                var resultado = await Mediator.Send(
                    new EditarCentroCommand(_editandoId.Value, _nombre, codigoCentro, direccion, contacto, contratoVigenteHasta, _versionEditando));
                mensajeError = resultado.EsFallido ? resultado.Error.Mensaje : null;
            }

            if (mensajeError is not null)
            {
                _mensajeErrorFormulario = mensajeError;
                return;
            }

            ToastService.Mostrar(
                _editandoId is null ? "Centro creado correctamente." : "Centro actualizado correctamente.",
                TonoToast.Exito);

            if (crearOtro && _editandoId is null)
            {
                _nombre = string.Empty;
                _codigoCentro = string.Empty;
                _direccion = string.Empty;
                _contacto = string.Empty;
                _contratoVigenteHasta = string.Empty;
                _erroresCampo = new Dictionary<string, string>();
                await RecargarAsync();
                return;
            }

            _drawerVisible = false;
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
    /// Validación inline al salir del campo (UX_PATTERNS.md, P1-18 de
    /// docs/business/MATURITY_REVIEW.md) — hasta ahora el error de "nombre
    /// obligatorio" solo aparecía tras el viaje de ida y vuelta al servidor
    /// en Guardar. Valida solo <see cref="CrearCentroCommand.Nombre"/>/
    /// <see cref="EditarCentroCommand.Nombre"/> con el mismo validador que
    /// ya corre al guardar — el resto del formulario puede seguir
    /// incompleto sin que este campo lo bloquee.
    /// </summary>
    private async Task ValidarNombreAsync()
    {
        const string campo = nameof(CrearCentroCommand.Nombre);

        var resultado = _editandoId is null
            ? await ValidadorCrear.ValidateAsync(
                new CrearCentroCommand(Guid.Empty, Guid.Empty, _nombre, null, null, null, null),
                opciones => opciones.IncludeProperties(campo))
            : await ValidadorEditar.ValidateAsync(
                new EditarCentroCommand(_editandoId.Value, _nombre, null, null, null, null, _versionEditando),
                opciones => opciones.IncludeProperties(campo));

        if (resultado.IsValid)
            _erroresCampo.Remove(campo);
        else
            _erroresCampo[campo] = resultado.Errors[0].ErrorMessage;
    }

    private void AbrirEliminar(Guid id, string nombre)
    {
        _idAEliminar = id;
        _nombreAEliminar = nombre;
        _confirmarEliminarVisible = true;
    }

    private async Task ConfirmarEliminarAsync()
    {
        _eliminando = true;

        try
        {
            var usuarioId = await CurrentUserService.ObtenerUsuarioActualIdAsync();
            var resultado = await Mediator.Send(new EliminarCentroCommand(_idAEliminar, usuarioId ?? Guid.Empty));

            if (resultado.EsFallido)
            {
                ToastService.Mostrar(resultado.Error.Mensaje, TonoToast.Error);
            }
            else
            {
                ToastService.Mostrar("Centro eliminado correctamente.", TonoToast.Exito);
                _confirmarEliminarVisible = false;
                await RecargarAsync();
            }
        }
        catch (Exception)
        {
            ToastService.Mostrar("No pudimos eliminar el centro. Intenta nuevamente en unos segundos.", TonoToast.Error);
        }
        finally
        {
            _eliminando = false;
        }
    }

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
            await RecargarAsync();
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

    private string ObtenerClaseFila(CentroListaDto item) => item.Id == _idEnfocado ? "fila-enfocada" : "";

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
}
