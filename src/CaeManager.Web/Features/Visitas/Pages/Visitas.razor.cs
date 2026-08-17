using CaeManager.Application.Centros.Queries.ObtenerCentrosParaSelector;
using CaeManager.Application.Comunicaciones.Queries.ObtenerSugerenciaVisitaCorreo;
using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadoresParaSelector;
using CaeManager.Application.Visitas.Commands.CrearVisita;
using CaeManager.Application.Visitas.Commands.EditarVisita;
using CaeManager.Application.Visitas.Commands.EliminarVisita;
using CaeManager.Application.Visitas.Commands.EliminarVisitas;
using CaeManager.Application.Visitas.Commands.MarcarNotificadoCliente;
using CaeManager.Application.Visitas.Queries.ObtenerDetalleVisita;
using CaeManager.Application.Visitas.Queries.ObtenerDocumentacionVisita;
using CaeManager.Application.Visitas.Queries.ObtenerVisitaPorId;
using CaeManager.Application.Visitas.Queries.ObtenerVisitas;
using CaeManager.Web.Components;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Components.Workspace;
using FluentValidation;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.QuickGrid;

namespace CaeManager.Web.Features.Visitas.Pages;

public partial class Visitas : ComponentBase
{
    private readonly PaginationState _paginacion = new() { ItemsPerPage = 20 };

    // H2 (docs/ux-audit/02-clientes.md): paginador único en español, ver Clientes.razor.cs.
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

    private QuickGrid<VisitaListaDto>? _grid;

    private string _busqueda = string.Empty;
    private bool _soloActivas = true;
    private bool _soloUrgentes;
    private string _filtroNotificado = string.Empty;
    private bool _cargando = true;
    private bool _errorCarga;
    private int _totalElementos;

    private IReadOnlyList<CentroSelectorDto> _centrosDisponibles = [];
    private IReadOnlyList<TrabajadorSelectorDto> _trabajadoresDisponibles = [];
    private IReadOnlyList<ElementoSeleccionable> _trabajadoresDisponiblesSelector => _trabajadoresDisponibles
        .Select(t => new ElementoSeleccionable(t.Id, $"{t.NombreCompleto} ({t.Dni})"))
        .ToList();

    private bool _drawerVisible;
    private Guid? _editandoId;
    // Version del registro tal como se abrio: vuelve en el Command para
    // detectar que otra persona guardo mientras el formulario estaba abierto.
    private Guid _versionEditando;
    private string _centroId = string.Empty;
    private string _centroNombreEnEdicion = string.Empty;
    private string _fechaInicio = string.Empty;
    private string _fechaFin = string.Empty;
    private string _horaEstimadaAcceso = string.Empty;
    private HashSet<Guid> _trabajadorIdsSeleccionados = [];
    private bool _notificadoCliente;
    private string _notas = string.Empty;
    private bool _guardando;
    private string? _mensajeErrorFormulario;
    private Dictionary<string, string> _erroresCampo = new();

    // Prellenado desde "Crear visita" de una sugerencia detectada por IA en
    // un correo (ver SugerenciaVisitaCorreo) — se manda de vuelta en
    // CrearVisitaCommand para que el handler marque la sugerencia resuelta.
    private Guid? _sugerenciaVisitaCorreoId;
    private string? _sugerenciaVisitaResumen;

    [SupplyParameterFromQuery(Name = "sugerenciaId")]
    public string? SugerenciaVisitaIdInicial { get; set; }

    // Overrides opcionales del Action Center de Comunicaciones
    // (docs/COMUNICACIONES.md § 12.6): cuando el gestor corrigió Centro o
    // fechas en la revisión previa a confirmar, viajan aquí y prevalecen
    // sobre lo que trae la propia SugerenciaVisitaCorreo almacenada — la
    // corrección "se manda junto con la confirmación", sin persistirse antes.
    [SupplyParameterFromQuery(Name = "centroId")]
    public string? CentroIdOverride { get; set; }

    [SupplyParameterFromQuery(Name = "fechaInicio")]
    public string? FechaInicioOverride { get; set; }

    [SupplyParameterFromQuery(Name = "fechaFin")]
    public string? FechaFinOverride { get; set; }

    private bool _confirmarEliminarVisible;
    private Guid _idAEliminar;
    private string _centroAEliminar = string.Empty;
    private bool _eliminando;

    private bool _detalleVisible;
    private bool _cargandoDetalle;
    private DetalleVisitaDto? _detalle;

    private bool _cargandoDocumentacion;
    private bool _errorDocumentacion;
    private DocumentacionVisitaDto? _documentacion;

    private bool _visorVisible;
    private Guid _visorDocumentoId;
    private string _visorTitulo = string.Empty;

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
    private List<VisitaListaDto> _elementosPagina = [];
    private Guid? _idEnfocado;
    private bool _eliminandoLote;
    private bool _confirmarEliminarLoteVisible;

    [SupplyParameterFromQuery(Name = "q")]
    public string? TerminoBusquedaInicial { get; set; }

    [SupplyParameterFromQuery(Name = "notificado")]
    public string? NotificadoInicial { get; set; }

    [Inject] private NavigationManager NavigationManager { get; set; } = default!;

    private GridItemsProvider<VisitaListaDto>? _proveedorElementos;

    // Delegado estable — ver Clientes.razor.cs (bucle de recargas de QuickGrid).
    protected override void OnInitialized() => _proveedorElementos = ProveerElementosAsync;

    /// <summary>
    /// Abre el drawer prellenado si se llegó desde el botón "Crear visita" de
    /// una sugerencia de la Bandeja (?sugerenciaId=...), o desde "Programar
    /// visita" de Centro 360 (?centroId=...&centroNombre=..., sin sugerencia).
    /// </summary>
    protected override async Task OnInitializedAsync()
    {
        if (Guid.TryParse(SugerenciaVisitaIdInicial, out var sugerenciaId))
            await AbrirCrearDesdeSugerenciaAsync(sugerenciaId);
        else if (Guid.TryParse(CentroIdOverride, out var centroIdInicial))
            await AbrirCrearParaCentroAsync(centroIdInicial);
    }

    /// <summary>
    /// Se re-ejecuta en cada navegación dentro de la propia página, no solo
    /// en el primer render — la URL como fuente de verdad de los filtros
    /// (P1-18 de docs/business/MATURITY_REVIEW.md).
    /// </summary>
    protected override void OnParametersSet()
    {
        _busqueda = TerminoBusquedaInicial ?? string.Empty;
        _filtroNotificado = NotificadoInicial ?? string.Empty;
    }

    private async ValueTask<GridItemsProviderResult<VisitaListaDto>> ProveerElementosAsync(
        GridItemsProviderRequest<VisitaListaDto> request)
    {
        _cargando = true;
        _errorCarga = false;

        try
        {
            var pagina = (request.StartIndex / _paginacion.ItemsPerPage) + 1;

            var (ordenarPor, descendente) = LecturaOrden.Leer(request);

            var resultado = await Mediator.Send(new ObtenerVisitasQuery(
                Busqueda: string.IsNullOrWhiteSpace(_busqueda) ? null : _busqueda,
                SoloActivas: _soloActivas,
                NotificadoCliente: _filtroNotificado switch { "si" => true, "no" => false, _ => null },
                SoloUrgentes: _soloUrgentes,
                Pagina: pagina,
                TamanoPagina: _paginacion.ItemsPerPage,
                OrdenarPor: ordenarPor,
                Descendente: descendente));

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
            return GridItemsProviderResult.From(new List<VisitaListaDto>(), 0);
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

    private async Task FiltrarPorNotificadoAsync(string valor)
    {
        _filtroNotificado = valor;
        NavigationManager.ActualizarFiltroEnUrl("notificado", valor);
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
        _centrosDisponibles = await Mediator.Send(new ObtenerCentrosParaSelectorQuery());
        _trabajadoresDisponibles = await Mediator.Send(new ObtenerTrabajadoresParaSelectorQuery());

        _editandoId = null;
        _centroId = string.Empty;
        _centroNombreEnEdicion = string.Empty;
        _fechaInicio = DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd");
        _fechaFin = DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd");
        _horaEstimadaAcceso = string.Empty;
        _trabajadorIdsSeleccionados = [];
        _notificadoCliente = false;
        _notas = string.Empty;
        _erroresCampo = new Dictionary<string, string>();
        _mensajeErrorFormulario = null;
        _sugerenciaVisitaCorreoId = null;
        _sugerenciaVisitaResumen = null;
        _drawerVisible = true;
    }

    /// <summary>Variante de AbrirCrearAsync que prellena Centro/fechas/notas con lo que detectó la IA en un correo — el Gestor sigue teniendo que elegir los trabajadores y confirmar el resto a mano.</summary>
    private async Task AbrirCrearDesdeSugerenciaAsync(Guid sugerenciaId)
    {
        var sugerencia = await Mediator.Send(new ObtenerSugerenciaVisitaCorreoQuery(sugerenciaId));
        if (sugerencia is null)
        {
            ToastService.Mostrar("No encontramos esta sugerencia. Puede que ya se haya resuelto.", TonoToast.Error);
            return;
        }

        await AbrirCrearAsync();

        _sugerenciaVisitaCorreoId = sugerencia.Id;
        _sugerenciaVisitaResumen = sugerencia.Resumen;

        _centroId = Guid.TryParse(CentroIdOverride, out var centroIdCorregido)
            ? centroIdCorregido.ToString()
            : sugerencia.CentroId?.ToString() ?? string.Empty;

        if (DateOnly.TryParse(FechaInicioOverride, out var fechaInicioCorregida))
            _fechaInicio = fechaInicioCorregida.ToString("yyyy-MM-dd");
        else if (sugerencia.FechaInicio is not null)
            _fechaInicio = sugerencia.FechaInicio.Value.ToString("yyyy-MM-dd");

        if (DateOnly.TryParse(FechaFinOverride, out var fechaFinCorregida))
            _fechaFin = fechaFinCorregida.ToString("yyyy-MM-dd");
        else if (sugerencia.FechaFin is not null)
            _fechaFin = sugerencia.FechaFin.Value.ToString("yyyy-MM-dd");
    }

    /// <summary>Variante de AbrirCrearAsync para "Programar visita" desde Centro 360: mismo drawer, con el Centro ya elegido en el CampoSelect — el Gestor solo pone fechas y trabajadores.</summary>
    private async Task AbrirCrearParaCentroAsync(Guid centroId)
    {
        await AbrirCrearAsync();
        _centroId = centroId.ToString();
    }

    private async Task AbrirEditarAsync(Guid id)
    {
        _sugerenciaVisitaCorreoId = null;
        _sugerenciaVisitaResumen = null;
        _trabajadoresDisponibles = await Mediator.Send(new ObtenerTrabajadoresParaSelectorQuery());

        var visita = await Mediator.Send(new ObtenerVisitaPorIdQuery(id));
        if (visita is null)
        {
            ToastService.Mostrar("No encontramos esta visita. Puede que ya se haya eliminado.", TonoToast.Error);
            await RecargarAsync();
            return;
        }

        _editandoId = visita.Id;
        _versionEditando = visita.Version;
        _centroId = visita.CentroId.ToString();
        _centroNombreEnEdicion = $"{visita.CentroNombre} ({visita.ClienteRazonSocial} — {visita.EmpresaRazonSocial})";
        _fechaInicio = visita.FechaInicio.ToString("yyyy-MM-dd");
        _fechaFin = visita.FechaFin.ToString("yyyy-MM-dd");
        _horaEstimadaAcceso = visita.HoraEstimadaAcceso?.ToString("HH:mm") ?? string.Empty;
        _trabajadorIdsSeleccionados = visita.TrabajadorIds.ToHashSet();
        _notificadoCliente = visita.NotificadoCliente;
        _notas = visita.Notas ?? string.Empty;
        _erroresCampo = new Dictionary<string, string>();
        _mensajeErrorFormulario = null;
        _drawerVisible = true;
    }

    /// <summary>
    /// Vista de solo lectura — quién entra y el estado de su documentación.
    /// A diferencia de la versión anterior (PestanaDocumentacion, sin
    /// filtrar por Centro), ObtenerDocumentacionVisitaQuery solo trae lo que
    /// aplica al Centro de esta visita, incluye "Faltante" y viene ordenada
    /// por severidad — ver el comentario de esa Query.
    /// </summary>
    private async Task AbrirDetalleAsync(Guid id)
    {
        _detalleVisible = true;
        _cargandoDetalle = true;
        _detalle = null;

        try
        {
            _detalle = await Mediator.Send(new ObtenerDetalleVisitaQuery(id));
            if (_detalle is null)
            {
                ToastService.Mostrar("No encontramos esta visita. Puede que ya se haya eliminado.", TonoToast.Error);
                return;
            }

            await CargarDocumentacionAsync(id);
        }
        catch (Exception)
        {
            ToastService.Mostrar("No pudimos cargar el detalle de la visita. Intenta nuevamente.", TonoToast.Error);
        }
        finally
        {
            _cargandoDetalle = false;
        }
    }

    private async Task CargarDocumentacionAsync(Guid visitaId)
    {
        _cargandoDocumentacion = true;
        _errorDocumentacion = false;
        _documentacion = null;

        try
        {
            _documentacion = await Mediator.Send(new ObtenerDocumentacionVisitaQuery(visitaId));
        }
        catch (Exception)
        {
            _errorDocumentacion = true;
        }
        finally
        {
            _cargandoDocumentacion = false;
        }
    }

    /// <summary>
    /// Un Documento existente abre el visor inline. Un hueco "Faltante" lleva
    /// directo al alta manual con el propietario y el tipo ya elegidos — ver
    /// AbrirCrearParaFaltanteAsync/AbrirCrearParaFaltanteEmpresaAsync en
    /// Documentos.razor.cs. Un Documento sin ArchivoUrl (se puede dar de alta
    /// sin adjuntar archivo, ver CrearDocumentoCommand) tampoco tiene nada
    /// que previsualizar — va directo a editar en vez de abrir un visor vacío.
    /// </summary>
    private void AbrirDocumento(DocumentoVisitaItemDto item)
    {
        if (item.DocumentoId is { } documentoId)
        {
            if (item.ArchivoUrl is null)
            {
                NavigationManager.NavigateTo($"/documentos?documentoId={documentoId}");
                return;
            }

            _visorDocumentoId = documentoId;
            _visorTitulo = item.TipoDocumentoNombre;
            _visorVisible = true;
            return;
        }

        if (item.TrabajadorId is { } trabajadorId)
        {
            NavigationManager.NavigateTo($"/documentos?trabajadorId={trabajadorId}&tipoDocumentoId={item.TipoDocumentoId}");
            return;
        }

        NavigationManager.NavigateTo($"/documentos?empresaIdFaltante={_documentacion!.EmpresaId}&tipoDocumentoId={item.TipoDocumentoId}");
    }

    private void AlternarTrabajador(Guid trabajadorId, bool seleccionado)
    {
        if (seleccionado)
            _trabajadorIdsSeleccionados.Add(trabajadorId);
        else
            _trabajadorIdsSeleccionados.Remove(trabajadorId);
    }

    private Task CerrarDrawerAsync(bool visible)
    {
        _drawerVisible = visible;
        return Task.CompletedTask;
    }

    private async Task GuardarAsync()
    {
        _guardando = true;
        _mensajeErrorFormulario = null;
        _erroresCampo = new Dictionary<string, string>();

        try
        {
            if (!DateOnly.TryParse(_fechaInicio, out var fechaInicio) || !DateOnly.TryParse(_fechaFin, out var fechaFin))
            {
                _mensajeErrorFormulario = "Las fechas no son válidas.";
                return;
            }

            var notas = string.IsNullOrWhiteSpace(_notas) ? null : _notas;
            TimeOnly? horaEstimada = TimeOnly.TryParse(_horaEstimadaAcceso, out var hora) ? hora : null;
            var trabajadorIds = _trabajadorIdsSeleccionados.ToList();
            string? mensajeError;

            if (_editandoId is null)
            {
                if (!Guid.TryParse(_centroId, out var centroId))
                {
                    _mensajeErrorFormulario = "Selecciona un centro.";
                    return;
                }

                var resultado = await Mediator.Send(new CrearVisitaCommand(centroId, fechaInicio, fechaFin, trabajadorIds, notas, _sugerenciaVisitaCorreoId, horaEstimada));
                mensajeError = resultado.EsFallido ? resultado.Error.Mensaje : null;
            }
            else
            {
                var resultado = await Mediator.Send(new EditarVisitaCommand(_editandoId.Value, fechaInicio, fechaFin, trabajadorIds, notas, _versionEditando, horaEstimada));
                mensajeError = resultado.EsFallido ? resultado.Error.Mensaje : null;
            }

            if (mensajeError is not null)
            {
                _mensajeErrorFormulario = mensajeError;
                return;
            }

            ToastService.Mostrar(
                _editandoId is null ? "Visita creada correctamente." : "Visita actualizada correctamente.",
                TonoToast.Exito);

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

    private async Task AlternarNotificadoAsync(Guid id, bool notificado)
    {
        try
        {
            var resultado = await Mediator.Send(new MarcarNotificadoClienteCommand(id, notificado));
            if (resultado.EsFallido)
            {
                ToastService.Mostrar(resultado.Error.Mensaje, TonoToast.Error);
                await RecargarAsync();
                return;
            }

            await RecargarAsync();
        }
        catch (Exception)
        {
            ToastService.Mostrar("No pudimos actualizar el estado de notificación. Intenta nuevamente.", TonoToast.Error);
        }
    }

    private void AbrirEliminar(Guid id, string centroNombre)
    {
        _idAEliminar = id;
        _centroAEliminar = centroNombre;
        _confirmarEliminarVisible = true;
    }

    private async Task ConfirmarEliminarAsync()
    {
        _eliminando = true;

        try
        {
            var usuarioId = await CurrentUserService.ObtenerUsuarioActualIdAsync();
            var resultado = await Mediator.Send(new EliminarVisitaCommand(_idAEliminar, usuarioId ?? Guid.Empty));

            if (resultado.EsFallido)
            {
                ToastService.Mostrar(resultado.Error.Mensaje, TonoToast.Error);
            }
            else
            {
                ToastService.Mostrar("Visita eliminada correctamente.", TonoToast.Exito);
                _confirmarEliminarVisible = false;
                await RecargarAsync();
            }
        }
        catch (Exception)
        {
            ToastService.Mostrar("No pudimos eliminar la visita. Intenta nuevamente en unos segundos.", TonoToast.Error);
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
            var resultado = await Mediator.Send(new EliminarVisitasCommand(_seleccionados.ToList(), usuarioId ?? Guid.Empty));
            var dto = resultado.Valor;

            ToastService.Mostrar(
                dto.Errores.Count == 0
                    ? $"{dto.Eliminados} visita(s) eliminada(s)."
                    : $"{dto.Eliminados} eliminada(s). {dto.Errores.Count} no se pudieron borrar: {string.Join(" ", dto.Errores)}",
                dto.Errores.Count == 0 ? TonoToast.Exito : TonoToast.Advertencia);

            _seleccionados.Clear();
            _confirmarEliminarLoteVisible = false;
            await RecargarAsync();
        }
        catch (Exception)
        {
            ToastService.Mostrar("No pudimos eliminar las visitas seleccionadas. Intenta nuevamente.", TonoToast.Error);
        }
        finally
        {
            _eliminandoLote = false;
        }
    }

    private string ObtenerClaseFila(VisitaListaDto item) => item.Id == _idEnfocado ? "fila-enfocada" : "";

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
                    await AbrirDetalleAsync(idAbrir);
                break;
        }

        StateHasChanged();
    }
}
