using System.Text.Json;
using CaeManager.Application.Alertas;
using CaeManager.Application.Asignaciones.Commands.CrearAsignaciones;
using CaeManager.Application.Asignaciones.Queries.ObtenerDocumentosFaltantesParaAsignacion;
using CaeManager.Application.Trabajadores.Commands.CrearTrabajador;
using CaeManager.Application.Trabajadores.Commands.EliminarTrabajador;
using CaeManager.Application.Trabajadores.Commands.EliminarTrabajadores;
using CaeManager.Application.Trabajadores.Commands.RestaurarTrabajador;
using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadores;
using CaeManager.Application.Centros.Queries.ObtenerCentrosParaSelector;
using CaeManager.Application.Configuracion.Commands.EliminarFiltroGuardado;
using CaeManager.Application.Configuracion.Commands.GuardarFiltro;
using CaeManager.Application.Configuracion.Queries;
using CaeManager.Application.Empresas.Queries.ObtenerEmpresasParaSelector;
using CaeManager.Application.Subcontratas.Queries.ObtenerSubcontratasParaSelector;
using CaeManager.Application.Tenants.Queries.ObtenerPerfilVocabularioActual;
using CaeManager.Domain.Common;
using CaeManager.Domain.Tenants;
using CaeManager.Web.Components;
using CaeManager.Web.Features.Documentos;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Components.Workspace;
using FluentValidation;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.QuickGrid;

namespace CaeManager.Web.Features.Trabajadores.Pages;

public partial class Trabajadores : ComponentBase
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

    private QuickGrid<TrabajadorListaDto>? _grid;

    private string _busqueda = string.Empty;
    private string _estadoFiltro = string.Empty;
    private string _filtroEmpresaId = string.Empty;
    private string _filtroSubcontrataId = string.Empty;
    private bool _cargando = true;
    private bool _errorCarga;
    private int _totalElementos;

    private IReadOnlyList<EmpresaSelectorDto> _empresasDisponibles = [];
    private IReadOnlyList<SubcontrataSelectorDto> _subcontratasDisponibles = [];

    // DDL-072 (misma tabla de vocabulario que EtiquetaEmpresas de NavMenu.razor
    // y _tituloPagina de Empresas.razor.cs): "Mis trabajadores" en perfil
    // Cliente Directo, "Trabajadores" en perfil Consultora.
    private string _tituloPagina = "Trabajadores";

    private bool _drawerVisible;
    private string _tipoEmpleador = "empresa";

    // DDL-076: en perfil Cliente Directo con una única Empresa, el selector
    // de Empresa no aparece — se resuelve en silencio. Reaparece si el
    // tenant tiene más de una razón social (excepción por dato, no por
    // configuración) o si el perfil es Consultora.
    private bool _resolverEmpresaEnSilencio;

    private string _empresaId = string.Empty;
    private string _subcontrataId = string.Empty;
    private string _dni = string.Empty;
    private string _nombre = string.Empty;
    private string _apellidos = string.Empty;
    private string _fechaNacimiento = string.Empty;
    private string _email = string.Empty;
    private string _telefono = string.Empty;
    private string _observaciones = string.Empty;
    private string _alias = string.Empty;
    private string _puesto = string.Empty;
    private bool _guardando;
    private string? _mensajeErrorFormulario;
    private Dictionary<string, string> _erroresCampo = new();

    private bool _confirmarEliminarVisible;
    private Guid _idAEliminar;
    private string _nombreAEliminar = string.Empty;
    private bool _eliminando;

    // Drawer ligero (mismo patrón que ClientePreviewDrawer): nombre de fila
    // y "Detalles" abren esto primero, no el Context Workspace directamente.
    private Guid? _previewTrabajadorId;
    private bool _previewVisible;

    private void AbrirPreview(Guid id)
    {
        _previewTrabajadorId = id;
        _previewVisible = true;
    }

    [SupplyParameterFromQuery(Name = "q")]
    public string? TerminoBusquedaInicial { get; set; }

    /// <summary>
    /// Filtro de estado documental (ver ICalculoEstadoDocumentalService) — esta
    /// entidad no tiene estado propio en el modelo, se deriva de sus Documentos.
    /// </summary>
    [SupplyParameterFromQuery(Name = "estado")]
    public string? EstadoInicial { get; set; }

    [Inject] private NavigationManager NavigationManager { get; set; } = default!;
    [Inject] private IValidator<CrearTrabajadorCommand> ValidadorCrear { get; set; } = default!;

    /// <summary>Comando del palette "Crear trabajador" / "Crear trabajador «nombre»" (P3-31): abre el Drawer, con el nombre precargado si viene del palette.</summary>
    [SupplyParameterFromQuery] public string? Accion { get; set; }
    [SupplyParameterFromQuery] public string? Nombre { get; set; }

    private GridItemsProvider<TrabajadorListaDto>? _proveedorElementos;

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
    private List<TrabajadorListaDto> _elementosPagina = [];
    private Guid? _idEnfocado;
    private bool _eliminandoLote;
    private bool _confirmarEliminarLoteVisible;

    private IReadOnlyList<FiltroGuardadoDto> _filtrosGuardados = [];
    private bool _mostrarGuardarFiltro;
    private string _nombreFiltroNuevo = string.Empty;
    private bool _guardandoFiltro;

    // --- Fase B: "Asignar a centro…" en lote desde /trabajadores ---
    private bool _asignarCentroVisible;
    private IReadOnlyList<CentroSelectorDto> _centrosDisponiblesParaAsignar = [];
    private string _centroIdParaAsignar = string.Empty;
    private string _fechaAltaParaAsignar = string.Empty;
    private IReadOnlyList<DocumentoFaltanteDto> _documentosFaltantesParaAsignar = [];
    private bool _asignandoLote;

    private IReadOnlyList<OpcionBuscable> OpcionesCentrosParaAsignar => _centrosDisponiblesParaAsignar
        .Select(c => new OpcionBuscable(c.Id.ToString(), $"{c.Nombre} ({c.ClienteRazonSocial})"))
        .ToList();

    private record FiltrosTrabajadoresJson(string? Busqueda, string? EmpresaId, string? SubcontrataId);

    protected override async Task OnInitializedAsync()
    {
        // Delegado estable — ver Clientes.razor.cs (bucle de recargas de QuickGrid).
        _proveedorElementos = ProveerElementosAsync;

        _empresasDisponibles = await Mediator.Send(new ObtenerEmpresasParaSelectorQuery());
        _subcontratasDisponibles = await Mediator.Send(new ObtenerSubcontratasParaSelectorQuery());

        var perfilPagina = await Mediator.Send(new ObtenerPerfilVocabularioActualQuery());
        _tituloPagina = perfilPagina == PerfilVocabularioTenant.ClienteDirecto ? "Mis trabajadores" : "Trabajadores";

        if (Accion == "crear")
        {
            await AbrirCrearAsync();
            if (!string.IsNullOrWhiteSpace(Nombre))
                _nombre = Nombre;
        }

        _filtrosGuardados = await Mediator.Send(new ObtenerFiltrosGuardadosQuery(PantallasConFiltrosGuardados.Trabajadores));
    }

    /// <summary>
    /// Se re-ejecuta en cada navegación dentro de la propia página (recargar,
    /// compartir la URL, volver atrás) — no solo en el primer render — para
    /// que el filtro de la URL sea la fuente de verdad, no solo su semilla
    /// inicial (P1-18 de docs/business/MATURITY_REVIEW.md).
    ///
    /// <para>
    /// Si la URL trae un filtro distinto del que hay en pantalla y la rejilla
    /// ya existe, se recarga: antes solo se copiaba el valor, y volver atrás a
    /// <c>?q=Salas</c> dejaba el chip diciendo «Salas» sobre las filas de la
    /// búsqueda anterior. En el primer paso la rejilla todavía no existe y su
    /// primera carga ya lee los valores de aquí. Los cambios que hace la propia
    /// página (buscar, quitar un chip) escriben primero el campo y después la
    /// URL, así que al llegar aquí ya coinciden y no duplican la consulta.
    /// </para>
    /// </summary>
    protected override async Task OnParametersSetAsync()
    {
        var deLaUrl = TerminoBusquedaInicial ?? string.Empty;
        var estadoDeLaUrl = EstadoDocumentoUi.OpcionesDocumentales.Any(o => o.Valor == EstadoInicial)
            ? EstadoInicial!
            : string.Empty;

        var cambiaronLosFiltros = deLaUrl != _busqueda || estadoDeLaUrl != _estadoFiltro;
        _busqueda = deLaUrl;
        _estadoFiltro = estadoDeLaUrl;

        if (cambiaronLosFiltros && _grid is not null)
            await RecargarAsync();

        // A diferencia de "accion=crear" (OnInitializedAsync, solo se
        // ejecuta al montar: siempre llega desde otra página), "guardar-filtro"
        // tiene que funcionar estando YA en /trabajadores — el propio Command
        // Palette navega a la misma ruta añadiendo el query string, sin
        // recrear el componente. OnParametersSet es el único hook que se
        // re-ejecuta en ese caso, y se ejecuta después de resincronizar los
        // filtros de arriba desde la URL, así que el modal parte de los
        // filtros ya vigentes en pantalla.
        if (Accion == "guardar-filtro")
            _mostrarGuardarFiltro = true;
    }

    private async Task CambiarEstadoAsync(string valor)
    {
        _estadoFiltro = valor;
        NavigationManager.ActualizarFiltroEnUrl("estado", valor);
        await RecargarAsync();
    }

    /// <summary>
    /// Número de la última carga pedida. Cada carga captura el suyo antes del
    /// <c>await</c> y, al volver, solo escribe estado si sigue siendo la
    /// vigente: si mientras tanto cambió un filtro, la búsqueda, la página o
    /// el orden, su respuesta es de otra pregunta. QuickGrid ya descarta las
    /// FILAS de una carga superada, pero no sabe nada de
    /// <see cref="_totalElementos"/>, <see cref="_elementosPagina"/> (de la que
    /// viven los atajos j/k/x y la selección) ni <see cref="_errorCarga"/>, que
    /// son de esta página. Mismo mecanismo que Gestiones.razor.cs.
    /// </summary>
    private int _cargaVigente;

    private async ValueTask<GridItemsProviderResult<TrabajadorListaDto>> ProveerElementosAsync(
        GridItemsProviderRequest<TrabajadorListaDto> request)
    {
        // Todo lo que define la pregunta se lee ANTES del await.
        var carga = ++_cargaVigente;
        var (ordenarPor, descendente) = LecturaOrden.Leer(request);
        var consulta = new ObtenerTrabajadoresQuery(
            Busqueda: string.IsNullOrWhiteSpace(_busqueda) ? null : _busqueda,
            EmpresaId: Guid.TryParse(_filtroEmpresaId, out var empresaId) ? empresaId : null,
            SubcontrataId: Guid.TryParse(_filtroSubcontrataId, out var subcontrataId) ? subcontrataId : null,
            Pagina: (request.StartIndex / _paginacion.ItemsPerPage) + 1,
            TamanoPagina: _paginacion.ItemsPerPage,
            OrdenarPor: ordenarPor,
            Descendente: descendente,
            EstadoDocumental: string.IsNullOrWhiteSpace(_estadoFiltro) ? null : _estadoFiltro);

        _cargando = true;
        _errorCarga = false;

        try
        {
            var resultado = await Mediator.Send(consulta, request.CancellationToken);

            if (carga != _cargaVigente)
                return GridItemsProviderResult.From(new List<TrabajadorListaDto>(), 0);

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
            return GridItemsProviderResult.From(new List<TrabajadorListaDto>(), 0);
        }
        catch (Exception)
        {
            _errorCarga = true;
            return GridItemsProviderResult.From(new List<TrabajadorListaDto>(), 0);
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

    private async Task FiltrarPorEmpresaAsync(string valor)
    {
        _filtroEmpresaId = valor;
        _filtroSubcontrataId = string.Empty;
        await RecargarAsync();
    }

    private async Task FiltrarPorSubcontrataAsync(string valor)
    {
        _filtroSubcontrataId = valor;
        _filtroEmpresaId = string.Empty;
        await RecargarAsync();
    }

    private async Task BuscarAsync(string valor)
    {
        _busqueda = valor;
        NavigationManager.ActualizarFiltroEnUrl("q", valor);
        await RecargarAsync();
    }

    /// <summary>
    /// Abre Trabajador 360 desde el menú de la fila, sin pasar por la vista
    /// previa. <c>forceLoad: true</c> por el mismo motivo, ya medido, que
    /// documenta <c>TrabajadorPreviewDrawer.AbrirTrabajador360</c>: el menú vive
    /// anidado dentro de la propia página que la navegación va a reemplazar, y
    /// una navegación mejorada dejaba a veces la URL cambiada con la lista
    /// todavía en pantalla. Es una transición ocasional lista→ficha, no una
    /// ruta caliente.
    /// </summary>
    private void AbrirTrabajador360(Guid id) =>
        NavigationManager.NavigateTo($"/trabajadores/{id}", forceLoad: true);

    private bool HayFiltrosActivos =>
        !string.IsNullOrWhiteSpace(_busqueda) || !string.IsNullOrWhiteSpace(_estadoFiltro)
        || !string.IsNullOrWhiteSpace(_filtroEmpresaId) || !string.IsNullOrWhiteSpace(_filtroSubcontrataId);

    private string EtiquetaFiltroEstado =>
        EstadoDocumentoUi.OpcionesDocumentales.FirstOrDefault(o => o.Valor == _estadoFiltro)?.Texto ?? _estadoFiltro;

    /// <summary>
    /// Rótulo del chip de empresa. Si el id filtrado no está en el catálogo
    /// cargado —cartera que ya no lo alcanza, empresa dada de baja— se dice
    /// "Empresa" a secas en vez de pintar un GUID: el chip existe para que se
    /// pueda quitar el filtro, y para eso no hace falta saber su nombre.
    /// </summary>
    private string EtiquetaFiltroEmpresa =>
        _empresasDisponibles.FirstOrDefault(e => e.Id.ToString() == _filtroEmpresaId)?.RazonSocial ?? "Empresa";

    private string EtiquetaFiltroSubcontrata =>
        _subcontratasDisponibles.FirstOrDefault(s => s.Id.ToString() == _filtroSubcontrataId)?.RazonSocial ?? "Subcontrata";

    private Task QuitarFiltroBusquedaAsync() => BuscarAsync(string.Empty);

    private Task QuitarFiltroEstadoAsync() => CambiarEstadoAsync(string.Empty);

    private Task QuitarFiltroEmpresaAsync() => FiltrarPorEmpresaAsync(string.Empty);

    private Task QuitarFiltroSubcontrataAsync() => FiltrarPorSubcontrataAsync(string.Empty);

    /// <summary>
    /// Quita los cuatro filtros en una sola recarga. Encadenar los setters
    /// lanzaría cuatro consultas y las tres primeras devolverían listas que ya
    /// no se van a pintar. La búsqueda y el estado viven además en la URL y se
    /// limpian allí en UNA sola navegación (ver
    /// <see cref="NavigationManagerExtensions.ActualizarFiltrosEnUrl"/>): si no,
    /// <see cref="OnParametersSetAsync"/> los repondría desde <c>?q=</c> y
    /// <c>?estado=</c> en la siguiente pasada de parámetros.
    /// </summary>
    private async Task LimpiarFiltrosAsync()
    {
        _busqueda = string.Empty;
        _estadoFiltro = string.Empty;
        _filtroEmpresaId = string.Empty;
        _filtroSubcontrataId = string.Empty;
        NavigationManager.ActualizarFiltrosEnUrl(new Dictionary<string, string?> { ["q"] = null, ["estado"] = null });
        await RecargarAsync();
    }

    /// <summary>
    /// Vuelve a la página 1 y pide la lista UNA vez.
    /// <see cref="PaginationState.SetCurrentPageIndexAsync"/> avisa a QuickGrid
    /// siempre, cambie o no la página, y QuickGrid recarga al recibir el aviso
    /// (así funciona <see cref="CambiarPaginaAsync"/>). Llamar a los dos,
    /// aviso y <c>RefreshDataAsync</c>, lanzaba dos consultas idénticas por
    /// cada búsqueda o filtro aunque el total no cambiara (medido en
    /// <c>TrabajadoresListaGen2Tests</c>).
    ///
    /// <para>
    /// Lo que esto no evita: si la respuesta trae un total distinto del
    /// anterior, QuickGrid vuelve a pedir la misma página por su cuenta en el
    /// render siguiente (<c>QuickGrid.OnParametersSetAsync</c> →
    /// <c>RefreshDataCoreAsync</c>). Es de QuickGrid, no de esta página.
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

    private async Task AbrirCrearAsync()
    {
        _empresasDisponibles = await Mediator.Send(new ObtenerEmpresasParaSelectorQuery());
        _subcontratasDisponibles = await Mediator.Send(new ObtenerSubcontratasParaSelectorQuery());

        var perfil = await Mediator.Send(new ObtenerPerfilVocabularioActualQuery());
        _resolverEmpresaEnSilencio = perfil == PerfilVocabularioTenant.ClienteDirecto && _empresasDisponibles.Count == 1;

        // Si la lista ya está filtrada por Empresa o Subcontrata, se presupone
        // que el trabajador que se va a dar de alta es de ese mismo empleador.
        if (!string.IsNullOrWhiteSpace(_filtroSubcontrataId))
        {
            _tipoEmpleador = "subcontrata";
            _subcontrataId = _filtroSubcontrataId;
            _empresaId = string.Empty;
        }
        else if (_resolverEmpresaEnSilencio)
        {
            _tipoEmpleador = "empresa";
            _empresaId = _empresasDisponibles[0].Id.ToString();
            _subcontrataId = string.Empty;
        }
        else
        {
            _tipoEmpleador = "empresa";
            _empresaId = _filtroEmpresaId;
            _subcontrataId = string.Empty;
        }
        _dni = string.Empty;
        _nombre = string.Empty;
        _apellidos = string.Empty;
        _alias = string.Empty;
        _puesto = string.Empty;
        _fechaNacimiento = string.Empty;
        _email = string.Empty;
        _telefono = string.Empty;
        _observaciones = string.Empty;
        _erroresCampo = new Dictionary<string, string>();
        _mensajeErrorFormulario = null;
        _drawerVisible = true;
    }

    private void SeleccionarTipoEmpresa() => CambiarTipoEmpleador("empresa");

    private void SeleccionarTipoSubcontrata() => CambiarTipoEmpleador("subcontrata");

    private void CambiarTipoEmpleador(string tipo)
    {
        _tipoEmpleador = tipo;
        _empresaId = string.Empty;
        _subcontrataId = string.Empty;
    }

    private Task CerrarDrawerAsync(bool visible)
    {
        _drawerVisible = visible;
        return Task.CompletedTask;
    }

    private async Task GuardarAsync()
    {
        // Guarda de doble clic: un segundo «Guardar» antes de que vuelva el
        // primero daría de alta el mismo trabajador dos veces (o chocaría con
        // el DNI duplicado). El botón se deshabilita con Cargando, pero el
        // segundo clic puede llegar antes de ese render.
        if (_guardando) return;
        _guardando = true;
        _mensajeErrorFormulario = null;
        _erroresCampo = new Dictionary<string, string>();

        try
        {
            var email = string.IsNullOrWhiteSpace(_email) ? null : _email;
            var telefono = string.IsNullOrWhiteSpace(_telefono) ? null : _telefono;
            var observaciones = string.IsNullOrWhiteSpace(_observaciones) ? null : _observaciones;
            var alias = string.IsNullOrWhiteSpace(_alias) ? null : _alias;
            var puesto = string.IsNullOrWhiteSpace(_puesto) ? null : _puesto;
            var fechaNacimiento = DateOnly.TryParse(_fechaNacimiento, out var fecha) ? fecha : (DateOnly?)null;

            Guid? empresaId = null;
            Guid? subcontrataId = null;

            if (_tipoEmpleador == "empresa")
            {
                if (!Guid.TryParse(_empresaId, out var empresaIdValor))
                {
                    _mensajeErrorFormulario = "Selecciona una empresa.";
                    return;
                }
                empresaId = empresaIdValor;
            }
            else
            {
                if (!Guid.TryParse(_subcontrataId, out var subcontrataIdValor))
                {
                    _mensajeErrorFormulario = "Selecciona una subcontrata.";
                    return;
                }
                subcontrataId = subcontrataIdValor;
            }

            var resultado = await Mediator.Send(
                new CrearTrabajadorCommand(empresaId, subcontrataId, _nombre, _apellidos, _dni, fechaNacimiento, email, observaciones, alias, telefono, puesto));

            if (resultado.EsFallido)
            {
                _mensajeErrorFormulario = resultado.Error.Mensaje;
                return;
            }

            ToastService.Mostrar("Trabajador creado correctamente.", TonoToast.Exito);
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
    /// Validación inline al salir del campo (mismo patrón que Centros.razor,
    /// UX_PATTERNS.md, P1-18 de docs/business/MATURITY_REVIEW.md). El
    /// empleador (empresa/subcontrata) no se valida aquí — IncludeProperties
    /// restringe la validación al campo que perdió el foco, así que null
    /// para ambos no afecta el resultado de estas reglas.
    /// </summary>
    private Task ValidarDniAsync() => ValidarCampoAsync(nameof(CrearTrabajadorCommand.Dni));

    private Task ValidarNombreAsync() => ValidarCampoAsync(nameof(CrearTrabajadorCommand.Nombre));

    private Task ValidarApellidosAsync() => ValidarCampoAsync(nameof(CrearTrabajadorCommand.Apellidos));

    private Task ValidarAliasAsync() => ValidarCampoAsync(nameof(CrearTrabajadorCommand.Alias));

    private Task ValidarPuestoAsync() => ValidarCampoAsync(nameof(CrearTrabajadorCommand.Puesto));

    private Task ValidarEmailAsync() => ValidarCampoAsync(nameof(CrearTrabajadorCommand.Email));

    private Task ValidarTelefonoAsync() => ValidarCampoAsync(nameof(CrearTrabajadorCommand.Telefono));

    private async Task ValidarCampoAsync(string campo)
    {
        var email = string.IsNullOrWhiteSpace(_email) ? null : _email;
        var telefono = string.IsNullOrWhiteSpace(_telefono) ? null : _telefono;
        var observaciones = string.IsNullOrWhiteSpace(_observaciones) ? null : _observaciones;
        var alias = string.IsNullOrWhiteSpace(_alias) ? null : _alias;
        var puesto = string.IsNullOrWhiteSpace(_puesto) ? null : _puesto;
        var fechaNacimiento = DateOnly.TryParse(_fechaNacimiento, out var fecha) ? fecha : (DateOnly?)null;

        var resultado = await ValidadorCrear.ValidateAsync(
            new CrearTrabajadorCommand(null, null, _nombre, _apellidos, _dni, fechaNacimiento, email, observaciones, alias, telefono, puesto),
            opciones => opciones.IncludeProperties(campo));

        if (resultado.IsValid)
            _erroresCampo.Remove(campo);
        else
            _erroresCampo[campo] = resultado.Errors[0].ErrorMessage;
    }

    private string? PistaDni
    {
        get
        {
            if (string.IsNullOrWhiteSpace(_dni)) return null;

            var resultado = ValidadorIdentificacion.Analizar(_dni);
            return resultado.Tipo switch
            {
                TipoIdentificacion.Dni => resultado.EsValido ? "✓ DNI con formato y dígito de control correctos." : "✗ Formato de DNI, pero el dígito de control no coincide.",
                TipoIdentificacion.Nie => resultado.EsValido ? "✓ NIE con formato y dígito de control correctos." : "✗ Formato de NIE, pero el dígito de control no coincide.",
                TipoIdentificacion.NifEmpresa => resultado.EsValido ? "✓ Formato de CIF válido — comprueba que sea la persona y no la empresa." : "✗ Parece un CIF, pero el dígito de control no coincide.",
                TipoIdentificacion.TieSoporte => "✓ Número de soporte TIE reconocido.",
                _ => "ℹ Documento no español (pasaporte u otro) — se acepta sin validar dígito de control."
            };
        }
    }

    private string ToneDni
    {
        get
        {
            var resultado = ValidadorIdentificacion.Analizar(_dni);
            if (resultado.Tipo is TipoIdentificacion.TieSoporte or TipoIdentificacion.Otros) return "info";
            return resultado.EsValido ? "exito" : "error";
        }
    }

    private static string NombreCompleto(TrabajadorListaDto trabajador) => $"{trabajador.Nombre} {trabajador.Apellidos}";

    private void AbrirEliminar(Guid id, string nombre)
    {
        _idAEliminar = id;
        _nombreAEliminar = nombre;
        _confirmarEliminarVisible = true;
    }

    private async Task ConfirmarEliminarAsync()
    {
        // Guarda de doble clic sobre «Eliminar» del diálogo: mandaría el
        // comando dos veces y el segundo fallaría con un error que no es real.
        if (_eliminando) return;
        _eliminando = true;
        var idEliminado = _idAEliminar;

        try
        {
            var resultado = await Mediator.Send(new EliminarTrabajadorCommand(idEliminado));

            if (resultado.EsFallido)
            {
                ToastService.Mostrar(resultado.Error.Mensaje, TonoToast.Error);
            }
            else
            {
                ToastService.Mostrar("Trabajador eliminado correctamente.", TonoToast.Exito, "Deshacer", () => DeshacerEliminarAsync(idEliminado));
                _confirmarEliminarVisible = false;
                await RecargarAsync();
            }
        }
        catch (Exception)
        {
            ToastService.Mostrar("No pudimos eliminar el trabajador. Intenta nuevamente en unos segundos.", TonoToast.Error);
        }
        finally
        {
            _eliminando = false;
        }
    }

    /// <summary>Fase D ("Deshacer al eliminar") — acción del toast tras eliminar, ver RestaurarTrabajadorCommand.</summary>
    private async Task DeshacerEliminarAsync(Guid id)
    {
        // Guarda por trabajador: dos pulsaciones en «Deshacer» del mismo aviso
        // no mandan dos restauraciones.
        if (!_restaurando.Add(id)) return;

        try
        {
            var resultado = await Mediator.Send(new RestaurarTrabajadorCommand(id));

            ToastService.Mostrar(
                resultado.EsExitoso ? "Trabajador restaurado." : resultado.Error.Mensaje,
                resultado.EsExitoso ? TonoToast.Exito : TonoToast.Error);

            if (resultado.EsExitoso)
                await RecargarAsync();
        }
        finally
        {
            _restaurando.Remove(id);
        }
    }

    private readonly HashSet<Guid> _restaurando = [];

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
        if (_eliminandoLote) return;
        _eliminandoLote = true;

        try
        {
            var resultado = await Mediator.Send(new EliminarTrabajadoresCommand(_seleccionados.ToList()));
            var dto = resultado.Valor;

            ToastService.Mostrar(
                dto.Errores.Count == 0
                    ? $"{dto.Eliminados} trabajador(es) eliminado(s)."
                    : $"{dto.Eliminados} eliminado(s). {dto.Errores.Count} no se pudieron borrar: {string.Join(" ", dto.Errores)}",
                dto.Errores.Count == 0 ? TonoToast.Exito : TonoToast.Advertencia);

            _seleccionados.Clear();
            _confirmarEliminarLoteVisible = false;
            await RecargarAsync();
        }
        catch (Exception)
        {
            ToastService.Mostrar("No pudimos eliminar los trabajadores seleccionados. Intenta nuevamente.", TonoToast.Error);
        }
        finally
        {
            _eliminandoLote = false;
        }
    }

    // --- Fase B: "Asignar a centro…" en lote ---

    private async Task AbrirAsignarCentroAsync()
    {
        // Antes del await: una consulta de faltantes del diálogo anterior que
        // siga en vuelo ya no es de este.
        ++_faltantesVigente;
        _centrosDisponiblesParaAsignar = await Mediator.Send(new ObtenerCentrosParaSelectorQuery());
        _centroIdParaAsignar = string.Empty;
        _fechaAltaParaAsignar = DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd");
        _documentosFaltantesParaAsignar = [];
        _asignarCentroVisible = true;
    }

    /// <summary>
    /// Única salida del diálogo (Cancelar, la X, Escape, clic fuera y el
    /// cierre tras asignar): invalida la consulta de faltantes en vuelo y
    /// suelta el aviso, que era del centro elegido en este diálogo.
    /// </summary>
    private void CerrarAsignarCentro()
    {
        ++_faltantesVigente;
        _asignarCentroVisible = false;
        _centroIdParaAsignar = string.Empty;
        _documentosFaltantesParaAsignar = [];
    }

    /// <summary>
    /// Carga vigente del aviso de documentos faltantes. Elegir un centro y
    /// después otro lanza dos consultas; si la del primero vuelve la última,
    /// sin esto el aviso hablaría del centro que ya no está elegido — y el
    /// botón diría «Asignar igualmente» (o «Asignar») por el centro equivocado.
    /// Abrir y cerrar el diálogo también la invalidan: cerrar con la consulta
    /// del centro A en vuelo y reabrir no puede pintar los faltantes de A en
    /// un diálogo nuevo que todavía no tiene centro.
    /// </summary>
    private int _faltantesVigente;

    private async Task CambiarCentroParaAsignarAsync(string valor)
    {
        var carga = ++_faltantesVigente;
        _centroIdParaAsignar = valor;

        if (!Guid.TryParse(valor, out var centroId))
        {
            _documentosFaltantesParaAsignar = [];
            return;
        }

        var faltantes = await Mediator.Send(
            new ObtenerDocumentosFaltantesParaAsignacionQuery(_seleccionados.ToList(), [centroId]));

        if (carga == _faltantesVigente)
            _documentosFaltantesParaAsignar = faltantes;
    }

    private async Task ConfirmarAsignarCentroAsync()
    {
        if (_asignandoLote) return;

        if (!Guid.TryParse(_centroIdParaAsignar, out var centroId))
        {
            ToastService.Mostrar("Selecciona un centro.", TonoToast.Error);
            return;
        }

        if (!DateOnly.TryParse(_fechaAltaParaAsignar, out var fechaAlta))
        {
            ToastService.Mostrar("Introduce una fecha de alta válida.", TonoToast.Error);
            return;
        }

        _asignandoLote = true;

        try
        {
            var resultado = await Mediator.Send(new CrearAsignacionesCommand(_seleccionados.ToList(), [centroId], fechaAlta));

            if (resultado.EsFallido)
            {
                ToastService.Mostrar(resultado.Error.Mensaje, TonoToast.Error);
                return;
            }

            var dto = resultado.Valor;
            var resumen = $"{dto.Creadas} asignación(es) creada(s)" + (dto.YaActivas > 0 ? $", {dto.YaActivas} ya estaban activas." : ".");
            ToastService.Mostrar(resumen, dto.Errores.Count == 0 ? TonoToast.Exito : TonoToast.Advertencia);
            // Sin esto, un rechazo con motivo (p. ej. Asignaciones que solapan
            // un periodo ya registrado, DEC-19) solo cambiaba el tono del
            // toast de arriba a Advertencia, sin decir nunca por qué —
            // mismo patrón que DrawerAsignacionMasiva.razor.cs.
            foreach (var error in dto.Errores)
                ToastService.Mostrar(error, TonoToast.Advertencia);

            _seleccionados.Clear();
            CerrarAsignarCentro();
            await RecargarAsync();
        }
        catch (Exception)
        {
            ToastService.Mostrar("No pudimos asignar a los trabajadores seleccionados. Intenta nuevamente.", TonoToast.Error);
        }
        finally
        {
            _asignandoLote = false;
        }
    }

    // --- P3-31: atajos de teclado j/k/x/Enter ---

    private string ObtenerClaseFila(TrabajadorListaDto item) => item.Id == _idEnfocado ? "fila-enfocada" : "";

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

    private async Task AplicarFiltroGuardadoAsync(string idTexto)
    {
        if (!Guid.TryParse(idTexto, out var id)) return;

        var filtro = _filtrosGuardados.FirstOrDefault(f => f.Id == id);
        if (filtro is null) return;

        var valores = JsonSerializer.Deserialize<FiltrosTrabajadoresJson>(filtro.ValoresJson);
        if (valores is null) return;

        _busqueda = valores.Busqueda ?? string.Empty;
        _filtroEmpresaId = valores.EmpresaId ?? string.Empty;
        _filtroSubcontrataId = valores.SubcontrataId ?? string.Empty;

        // La búsqueda del filtro guardado se escribe también en ?q=. Si solo
        // se aplicara en memoria, la siguiente navegación dentro de la página
        // (p. ej. cambiar el filtro de documentación, que sí escribe la URL)
        // haría que OnParametersSetAsync la borrase leyendo un ?q= vacío.
        NavigationManager.ActualizarFiltroEnUrl("q", _busqueda);
        await RecargarAsync();
    }

    private async Task GuardarFiltroActualAsync()
    {
        if (string.IsNullOrWhiteSpace(_nombreFiltroNuevo) || _guardandoFiltro) return;

        _guardandoFiltro = true;

        try
        {
            var valoresJson = JsonSerializer.Serialize(new FiltrosTrabajadoresJson(
                string.IsNullOrWhiteSpace(_busqueda) ? null : _busqueda,
                string.IsNullOrWhiteSpace(_filtroEmpresaId) ? null : _filtroEmpresaId,
                string.IsNullOrWhiteSpace(_filtroSubcontrataId) ? null : _filtroSubcontrataId));

            var resultado = await Mediator.Send(
                new GuardarFiltroCommand(PantallasConFiltrosGuardados.Trabajadores, _nombreFiltroNuevo, valoresJson));

            if (resultado.EsFallido)
            {
                ToastService.Mostrar(resultado.Error.Mensaje, TonoToast.Error);
                return;
            }

            _filtrosGuardados = await Mediator.Send(new ObtenerFiltrosGuardadosQuery(PantallasConFiltrosGuardados.Trabajadores));
            _mostrarGuardarFiltro = false;
            _nombreFiltroNuevo = string.Empty;
            ToastService.Mostrar("Filtro guardado.", TonoToast.Exito);
        }
        finally
        {
            _guardandoFiltro = false;
        }
    }

    private async Task EliminarFiltroGuardadoAsync(Guid id)
    {
        var resultado = await Mediator.Send(new EliminarFiltroGuardadoCommand(id));
        if (resultado.EsFallido)
        {
            ToastService.Mostrar(resultado.Error.Mensaje, TonoToast.Error);
            return;
        }

        _filtrosGuardados = await Mediator.Send(new ObtenerFiltrosGuardadosQuery(PantallasConFiltrosGuardados.Trabajadores));
    }
}
