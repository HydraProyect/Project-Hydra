using CaeManager.Application.Clientes.Queries.ObtenerClientesParaSelector;
using CaeManager.Application.Empresas.Commands.CrearEmpresa;
using CaeManager.Application.Empresas.Commands.EliminarEmpresa;
using CaeManager.Application.Empresas.Commands.EliminarEmpresas;
using CaeManager.Application.Empresas.Commands.RestaurarEmpresa;
using CaeManager.Application.Empresas.Queries.ObtenerClientesDeEmpresa;
using CaeManager.Application.Empresas.Queries.ObtenerEmpresas;
using CaeManager.Application.Tenants.Queries.ObtenerPerfilVocabularioActual;
using CaeManager.Domain.Tenants;
using CaeManager.Web.Components;
using CaeManager.Web.Features.Documentos;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Components.Workspace;
using FluentValidation;
using Microsoft.AspNetCore.Components;

namespace CaeManager.Web.Features.Empresas.Pages;

public partial class Empresas : ComponentBase
{
    // QuickGrid no soporta filas expandibles (Centro 360, PLAN-EJECUCION-UX.md
    // § 0.11 — migra /empresas al mismo patrón de Centros.razor § 0.1): cada
    // Empresa es una tarjeta con acordeón de Centros con actividad, así que
    // la paginación se gestiona a mano en vez de con QuickGrid+Paginator.
    private int _tamanoPagina = 20;

    private string _busqueda = string.Empty;
    private string _estadoFiltro = string.Empty;
    private bool _cargando = true;
    private bool _errorCarga;
    private int _totalElementos;
    private int _pagina = 1;

    private int TotalPaginas => Math.Max(1, (int)Math.Ceiling(_totalElementos / (double)_tamanoPagina));

    private IReadOnlyList<ClienteSelectorDto> _clientesDisponibles = [];
    private IReadOnlyList<ElementoSeleccionable> _clientesDisponiblesSelector => _clientesDisponibles
        .Select(c => new ElementoSeleccionable(c.Id, c.RazonSocial))
        .ToList();

    private bool _drawerVisible;
    private string _razonSocial = string.Empty;
    private string _cif = string.Empty;
    private string _cnae = string.Empty;
    private string _convenioAplicable = string.Empty;
    private bool _esActividadAnexoI;
    private HashSet<Guid> _clienteIdsSeleccionados = [];
    private bool _guardando;
    private string? _mensajeErrorFormulario;
    private Dictionary<string, string> _erroresCampo = new();

    private bool _confirmarEliminarVisible;
    private Guid _idAEliminar;
    private string _razonSocialAEliminar = string.Empty;
    private bool _eliminando;

    // Drawer ligero (mismo patrón que ClientePreviewDrawer): nombre de fila
    // y "Detalles" abren esto primero, no el Context Workspace directamente.
    private Guid? _previewEmpresaId;
    private bool _previewVisible;

    private void AbrirPreview(Guid id)
    {
        _previewEmpresaId = id;
        _previewVisible = true;
    }

    private Task AbrirDesdePreviewAsync((Guid Id, string Pestana) destino)
    {
        var nombre = _elementosPagina.FirstOrDefault(e => e.Id == destino.Id)?.RazonSocial ?? string.Empty;
        return WorkspaceService.AbrirAsync(EntidadWorkspace.Empresa, destino.Id, nombre, destino.Pestana);
    }

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

    /// <summary>
    /// Qué filas tienen el acordeón de "Clientes" abierto — la expansión la
    /// lleva la página, no un estado interno por fila, para que
    /// "Expandir/Colapsar todos" pueda decidirlo desde fuera (§ 0.9).
    /// </summary>
    private readonly HashSet<Guid> _expandidos = [];

    /// <summary>
    /// Carga perezosa por Empresa, al expandir: no tiene sentido pagar N
    /// consultas de "Clientes de la Empresa" (una por Empresa de la página)
    /// si la mayoría de acordeones se quedan cerrados. <c>null</c> = todavía
    /// no se ha pedido; lista vacía = ya se pidió y no tiene clientes.
    /// </summary>
    private readonly Dictionary<Guid, IReadOnlyList<ClienteDeEmpresaDto>?> _clientesPorEmpresa = new();

    private List<EmpresaListaDto> _elementosPagina = [];
    private Guid? _idEnfocado;
    private bool _eliminandoLote;
    private bool _confirmarEliminarLoteVisible;

    // DDL-072: "Mi empresa" en perfil Cliente Directo, "Empresas" en perfil
    // Consultora — mismo mecanismo que NavMenu.razor.
    private string _tituloPagina = "Empresas";

    [SupplyParameterFromQuery(Name = "q")]
    public string? TerminoBusquedaInicial { get; set; }

    /// <summary>
    /// Filtro de estado documental (ver ICalculoEstadoDocumentalService) — esta
    /// entidad no tiene estado propio en el modelo, se deriva de sus Documentos.
    /// </summary>
    [SupplyParameterFromQuery(Name = "estado")]
    public string? EstadoInicial { get; set; }

    [Inject] private NavigationManager NavigationManager { get; set; } = default!;
    [Inject] private IValidator<CrearEmpresaCommand> ValidadorCrear { get; set; } = default!;

    /// <summary>Comando del palette "Crear empresa «nombre»" (P3-31): abre el Drawer con la razón social precargada.</summary>
    [SupplyParameterFromQuery] public string? Accion { get; set; }
    [SupplyParameterFromQuery] public string? Nombre { get; set; }

    /// <summary>
    /// Encadenado desde "Continuar con la empresa" en /clientes (Fase A2): el
    /// Cliente recién creado llega premarcado en el selector, para no tener
    /// que volver a buscarlo.
    /// </summary>
    [SupplyParameterFromQuery] public Guid? ClienteId { get; set; }

    protected override async Task OnInitializedAsync()
    {
        _busqueda = TerminoBusquedaInicial ?? string.Empty;
        _estadoFiltro = EstadoDocumentoUi.OpcionesDocumentales.Any(o => o.Valor == EstadoInicial) ? EstadoInicial! : string.Empty;

        var perfil = await Mediator.Send(new ObtenerPerfilVocabularioActualQuery());
        _tituloPagina = perfil == PerfilVocabularioTenant.ClienteDirecto ? "Mi empresa" : "Empresas";

        await CargarAsync();

        if (Accion == "crear")
        {
            await AbrirCrear();
            if (!string.IsNullOrWhiteSpace(Nombre))
                _razonSocial = Nombre;
            if (ClienteId is not null && _clientesDisponibles.Any(c => c.Id == ClienteId))
                _clienteIdsSeleccionados = [ClienteId.Value];
        }
    }

    /// <summary>
    /// Se re-ejecuta en cada navegación dentro de la propia página (recargar,
    /// compartir la URL, volver atrás) — no solo en el primer render — para
    /// que el filtro de la URL sea la fuente de verdad, no solo su semilla
    /// inicial (P1-18 de docs/business/MATURITY_REVIEW.md).
    /// </summary>
    protected override async Task OnParametersSetAsync()
    {
        var deLaUrl = TerminoBusquedaInicial ?? string.Empty;
        var estadoDeLaUrl = EstadoDocumentoUi.OpcionesDocumentales.Any(o => o.Valor == EstadoInicial) ? EstadoInicial! : string.Empty;

        if (deLaUrl == _busqueda && estadoDeLaUrl == _estadoFiltro)
            return;

        _busqueda = deLaUrl;
        _estadoFiltro = estadoDeLaUrl;
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
            var resultado = await Mediator.Send(new ObtenerEmpresasQuery(
                Busqueda: string.IsNullOrWhiteSpace(_busqueda) ? null : _busqueda,
                Pagina: _pagina,
                TamanoPagina: _tamanoPagina,
                EstadoDocumental: string.IsNullOrWhiteSpace(_estadoFiltro) ? null : _estadoFiltro));

            _totalElementos = resultado.TotalElementos;
            _elementosPagina = resultado.Elementos.ToList();
            _seleccionados.Clear();
            _expandidos.Clear();
            _clientesPorEmpresa.Clear();
            _idEnfocado = null;
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

    // H5 (docs/ux-audit/05-trabajadores-vehiculos.md): selector de tamaño de página, compartido por PaginadorSimple.razor.
    private Task CambiarTamanoPaginaAsync(int tamano)
    {
        _tamanoPagina = tamano;
        return CargarAsync(resetPagina: true);
    }

    private Task CambiarPaginaAsync(int pagina)
    {
        _pagina = pagina;
        return CargarAsync();
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

    private async Task AbrirCrear()
    {
        _clientesDisponibles = await Mediator.Send(new ObtenerClientesParaSelectorQuery());

        _razonSocial = string.Empty;
        _cif = string.Empty;
        _cnae = string.Empty;
        _convenioAplicable = string.Empty;
        _esActividadAnexoI = false;
        _clienteIdsSeleccionados = [];
        _erroresCampo = new Dictionary<string, string>();
        _mensajeErrorFormulario = null;
        _drawerVisible = true;
    }

    private void AlternarCliente(Guid clienteId, bool seleccionado)
    {
        if (seleccionado)
            _clienteIdsSeleccionados.Add(clienteId);
        else
            _clienteIdsSeleccionados.Remove(clienteId);
    }

    private Task CerrarDrawerAsync(bool visible)
    {
        _drawerVisible = visible;
        return Task.CompletedTask;
    }

    private Task GuardarAsync() => GuardarAsync(continuarACrearCentro: false);

    /// <summary>
    /// "Continuar con el centro" (Fase A2): al crear una Empresa nueva con
    /// éxito, navega directamente a <c>/centros?accion=crear</c> con Cliente
    /// y Empresa ya fijados, en vez de solo cerrar el Drawer.
    /// </summary>
    private Task GuardarYCrearCentroAsync() => GuardarAsync(continuarACrearCentro: true);

    private async Task GuardarAsync(bool continuarACrearCentro)
    {
        _guardando = true;
        _mensajeErrorFormulario = null;
        _erroresCampo = new Dictionary<string, string>();

        try
        {
            var clienteIds = _clienteIdsSeleccionados.ToList();
            var cif = string.IsNullOrWhiteSpace(_cif) ? null : _cif;
            var cnae = string.IsNullOrWhiteSpace(_cnae) ? null : _cnae;
            var convenioAplicable = string.IsNullOrWhiteSpace(_convenioAplicable) ? null : _convenioAplicable;

            var resultado = await Mediator.Send(new CrearEmpresaCommand(_razonSocial, cif, clienteIds, cnae, convenioAplicable, _esActividadAnexoI));
            if (resultado.EsFallido)
            {
                _mensajeErrorFormulario = resultado.Error.Mensaje;
                return;
            }

            var empresaCreadaId = resultado.Valor;
            ToastService.Mostrar("Empresa creada correctamente.", TonoToast.Exito);

            if (continuarACrearCentro)
            {
                // Prioridad: el Cliente que trajo la cadena (si sigue
                // marcado) · si no, el único Cliente marcado en el selector ·
                // si hay varios o ninguno, no hay uno solo que prefijar.
                var clienteParaCentro = ClienteId is not null && clienteIds.Contains(ClienteId.Value)
                    ? ClienteId
                    : clienteIds.Count == 1 ? clienteIds[0] : (Guid?)null;

                var destino = clienteParaCentro is null
                    ? $"/centros?accion=crear&empresaId={empresaCreadaId}"
                    : $"/centros?accion=crear&clienteId={clienteParaCentro}&empresaId={empresaCreadaId}";
                NavigationManager.NavigateTo(destino);
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
    /// Validación inline al salir del campo (mismo patrón que Centros.razor,
    /// UX_PATTERNS.md, P1-18 de docs/business/MATURITY_REVIEW.md).
    /// </summary>
    private Task ValidarRazonSocialAsync() => ValidarCampoAsync(nameof(CrearEmpresaCommand.RazonSocial));

    private Task ValidarCifAsync() => ValidarCampoAsync(nameof(CrearEmpresaCommand.Cif));

    private Task ValidarCnaeAsync() => ValidarCampoAsync(nameof(CrearEmpresaCommand.Cnae));

    private Task ValidarConvenioAplicableAsync() => ValidarCampoAsync(nameof(CrearEmpresaCommand.ConvenioAplicable));

    private async Task ValidarCampoAsync(string campo)
    {
        var cif = string.IsNullOrWhiteSpace(_cif) ? null : _cif;
        var cnae = string.IsNullOrWhiteSpace(_cnae) ? null : _cnae;
        var convenioAplicable = string.IsNullOrWhiteSpace(_convenioAplicable) ? null : _convenioAplicable;

        var resultado = await ValidadorCrear.ValidateAsync(
            new CrearEmpresaCommand(_razonSocial, cif, _clienteIdsSeleccionados.ToList(), cnae, convenioAplicable, _esActividadAnexoI),
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
        _eliminando = true;

        try
        {
            var resultado = await Mediator.Send(new EliminarEmpresaCommand(_idAEliminar));

            if (resultado.EsFallido)
            {
                ToastService.Mostrar(resultado.Error.Mensaje, TonoToast.Error);
            }
            else
            {
                var idEliminado = _idAEliminar;
                ToastService.Mostrar("Empresa eliminada correctamente.", TonoToast.Exito, "Deshacer", () => DeshacerEliminarAsync(idEliminado));
                _confirmarEliminarVisible = false;
                await CargarAsync();
            }
        }
        catch (Exception)
        {
            ToastService.Mostrar("No pudimos eliminar la empresa. Intenta nuevamente en unos segundos.", TonoToast.Error);
        }
        finally
        {
            _eliminando = false;
        }
    }

    /// <summary>Fase D ("Deshacer al eliminar") — acción del toast tras eliminar, ver RestaurarEmpresaCommand.</summary>
    private async Task DeshacerEliminarAsync(Guid id)
    {
        var resultado = await Mediator.Send(new RestaurarEmpresaCommand(id));

        ToastService.Mostrar(
            resultado.EsExitoso ? "Empresa restaurada." : resultado.Error.Mensaje,
            resultado.EsExitoso ? TonoToast.Exito : TonoToast.Error);

        if (resultado.EsExitoso)
            await CargarAsync();
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

    private bool TodosExpandidos =>
        _elementosPagina.Count > 0 && _elementosPagina.All(e => _expandidos.Contains(e.Id));

    private async Task AlternarExpansionAsync(Guid empresaId)
    {
        if (!_expandidos.Add(empresaId))
        {
            _expandidos.Remove(empresaId);
            return;
        }

        if (!_clientesPorEmpresa.ContainsKey(empresaId))
            await CargarClientesDeEmpresaAsync(empresaId);
    }

    private async Task AlternarTodosExpandidosAsync(bool expandir)
    {
        if (!expandir)
        {
            _expandidos.Clear();
            return;
        }

        foreach (var elemento in _elementosPagina) _expandidos.Add(elemento.Id);

        var pendientes = _elementosPagina
            .Select(e => e.Id)
            .Where(id => !_clientesPorEmpresa.ContainsKey(id))
            .Select(CargarClientesDeEmpresaAsync);

        await Task.WhenAll(pendientes);
    }

    private void IrAlCliente(Guid clienteId, string razonSocial) =>
        WorkspaceService.AbrirAsync(EntidadWorkspace.Cliente, clienteId, razonSocial, "informacion");

    private async Task CargarClientesDeEmpresaAsync(Guid empresaId)
    {
        try
        {
            var resultado = await Mediator.Send(new ObtenerClientesDeEmpresaQuery(empresaId));
            _clientesPorEmpresa[empresaId] = resultado;
        }
        catch (Exception)
        {
            _clientesPorEmpresa[empresaId] = [];
        }

        StateHasChanged();
    }

    private async Task ConfirmarEliminarLoteAsync()
    {
        _eliminandoLote = true;

        try
        {
            var resultado = await Mediator.Send(new EliminarEmpresasCommand(_seleccionados.ToList()));
            var dto = resultado.Valor;

            ToastService.Mostrar(
                dto.Errores.Count == 0
                    ? $"{dto.Eliminados} empresa(s) eliminada(s)."
                    : $"{dto.Eliminados} eliminada(s). {dto.Errores.Count} no se pudieron borrar: {string.Join(" ", dto.Errores)}",
                dto.Errores.Count == 0 ? TonoToast.Exito : TonoToast.Advertencia);

            _seleccionados.Clear();
            _confirmarEliminarLoteVisible = false;
            await CargarAsync();
        }
        catch (Exception)
        {
            ToastService.Mostrar("No pudimos eliminar las empresas seleccionadas. Intenta nuevamente.", TonoToast.Error);
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
                    AbrirPreview(idAbrir);
                break;
        }

        StateHasChanged();
    }
}
