using CaeManager.Application.Clientes.Queries.ObtenerClientesParaSelector;
using CaeManager.Application.Empresas.Commands.CrearEmpresa;
using CaeManager.Application.Empresas.Commands.EliminarEmpresa;
using CaeManager.Application.Empresas.Commands.EliminarEmpresas;
using CaeManager.Application.Empresas.Commands.RestaurarEmpresa;
using CaeManager.Application.Empresas.Queries.ObtenerClientesDeEmpresa;
using CaeManager.Application.Empresas.Queries.ObtenerEmpresas;
using CaeManager.Application.Tenants.Queries.ObtenerPerfilVocabularioActual;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Tenants;
using CaeManager.Web.Components;
using CaeManager.Web.Features.Documentos;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Components.Workspace;
using FluentValidation;
using Microsoft.AspNetCore.Components;

namespace CaeManager.Web.Features.Empresas.Pages;

public partial class Empresas : ComponentBase, IDisposable
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

    /// <summary>
    /// Número de la última carga de la lista. Cada carga captura el suyo ANTES
    /// del <c>await</c> y, al volver, solo escribe estado si sigue siendo la
    /// vigente: si mientras tanto cambió un filtro, la página o el tamaño, su
    /// respuesta es de otra pregunta. Sin esto, una respuesta lenta del filtro
    /// anterior pisaba las filas, el total y el estado vacío del nuevo. Las
    /// cargas de «Clientes de la Empresa» de la fila desplegada comparten el
    /// mismo número: una respuesta de antes de recargar la lista ya no
    /// pertenece a lo que se ve.
    /// </summary>
    private int _cargaVigente;

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
    /// no se ha pedido; lista vacía = ya se pidió y no tiene clientes (o
    /// falló: ver <see cref="_clientesConError"/>).
    /// </summary>
    private readonly Dictionary<Guid, IReadOnlyList<ClienteDeEmpresaDto>?> _clientesPorEmpresa = new();

    /// <summary>
    /// Empresas cuya consulta de clientes falló. Van aparte para no pintar un
    /// fallo como «no tiene ningún Cliente asociado».
    /// </summary>
    private readonly HashSet<Guid> _clientesConError = [];

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

    /// <summary>
    /// Acción pedida por URL: <c>crear</c> (palette "Crear empresa «nombre»",
    /// P3-31, y el atajo global «n») abre el Drawer de alta. Ver
    /// <see cref="OnParametersSetAsync"/>.
    /// </summary>
    [SupplyParameterFromQuery] public string? Accion { get; set; }
    [SupplyParameterFromQuery] public string? Nombre { get; set; }

    /// <summary>
    /// Última <see cref="Accion"/> ya atendida. La acción se ejecuta al CAMBIAR,
    /// no en cada pasada de parámetros: la URL la conserva mientras se trabaja
    /// (cada filtro que se escribe en la URL preserva los demás parámetros), y
    /// atenderla en cada pasada reabriría el Drawer al teclear en el buscador
    /// después de cerrarlo.
    /// </summary>
    private string? _accionAtendida;

    /// <summary>
    /// Encadenado desde "Continuar con la empresa" en /clientes (Fase A2): el
    /// Cliente recién creado llega premarcado en el selector, para no tener
    /// que volver a buscarlo.
    /// </summary>
    [SupplyParameterFromQuery] public Guid? ClienteId { get; set; }

    protected override async Task OnInitializedAsync()
    {
        _busqueda = TerminoBusquedaInicial ?? string.Empty;
        _estadoFiltro = EstadoDesdeUrl();

        var perfil = await Mediator.Send(new ObtenerPerfilVocabularioActualQuery());
        _tituloPagina = perfil == PerfilVocabularioTenant.ClienteDirecto ? "Mi empresa" : "Empresas";

        await CargarAsync();
    }

    /// <summary>
    /// Se re-ejecuta en cada navegación dentro de la propia página (recargar,
    /// compartir la URL, volver atrás) — no solo en el primer render — para
    /// que el filtro de la URL sea la fuente de verdad, no solo su semilla
    /// inicial (P1-18 de docs/business/MATURITY_REVIEW.md).
    ///
    /// <para>
    /// Un cambio que inicia la propia página (escribir en el buscador, elegir
    /// un estado, quitar un filtro) fija el campo ANTES de escribir la URL, así
    /// que al llegar aquí ya coincide y no se lanza una segunda consulta.
    /// </para>
    /// </summary>
    protected override async Task OnParametersSetAsync()
    {
        var deLaUrl = TerminoBusquedaInicial ?? string.Empty;
        var estadoDeLaUrl = EstadoDesdeUrl();

        if (deLaUrl != _busqueda || estadoDeLaUrl != _estadoFiltro)
        {
            _busqueda = deLaUrl;
            _estadoFiltro = estadoDeLaUrl;
            await CargarAsync(resetPagina: true);
        }

        // Aquí y no en OnInitializedAsync: ese solo corre al montar, y el atajo
        // global «n» navega a /empresas?accion=crear estando YA en /empresas,
        // sin recrear el componente. Antes «n» cambiaba la URL sin abrir nada.
        if (Accion != _accionAtendida)
        {
            _accionAtendida = Accion;
            if (Accion == "crear")
                await AbrirCrearDesdeUrlAsync();
        }
    }

    private string EstadoDesdeUrl() =>
        EstadoDocumentoUi.OpcionesDocumentales.Any(o => o.Valor == EstadoInicial) ? EstadoInicial! : string.Empty;

    private async Task AbrirCrearDesdeUrlAsync()
    {
        await AbrirCrear();
        if (!string.IsNullOrWhiteSpace(Nombre))
            _razonSocial = Nombre;
        if (ClienteId is not null && _clientesDisponibles.Any(c => c.Id == ClienteId))
            _clienteIdsSeleccionados = [ClienteId.Value];
    }

    /// <summary>
    /// Al cerrar lo que abrió una acción por URL se quita esa acción de la
    /// URL: si no, volver a pulsar «n» navegaría a la misma URL, la acción no
    /// cambiaría y no se abriría nada.
    /// </summary>
    private void QuitarAccionDeLaUrl()
    {
        if (!string.IsNullOrEmpty(Accion))
            NavigationManager.ActualizarFiltroEnUrl("accion", null);
    }

    /// <summary>
    /// Se cancela al salir de la página: las consultas en curso dejan de
    /// trabajar para nadie y ninguna respuesta tardía repinta un componente
    /// ya retirado. Mismo patrón que DeteccionTrabajadores.
    /// </summary>
    private readonly CancellationTokenSource _ciclo = new();
    private bool _desechado;

    public void Dispose()
    {
        if (_desechado)
            return;

        _desechado = true;
        _ciclo.Cancel();
        _ciclo.Dispose();
    }

    /// <summary>La respuesta es de la pregunta vigente y la página sigue viva.</summary>
    private bool EsVigente(int carga) => !_desechado && carga == _cargaVigente;

    private async Task CargarAsync(bool resetPagina = false)
    {
        if (resetPagina)
            _pagina = 1;

        if (_desechado)
            return;

        // Todo lo que define la pregunta se lee ANTES del await.
        var carga = ++_cargaVigente;
        var consulta = new ObtenerEmpresasQuery(
            Busqueda: string.IsNullOrWhiteSpace(_busqueda) ? null : _busqueda,
            Pagina: _pagina,
            TamanoPagina: _tamanoPagina,
            EstadoDocumental: string.IsNullOrWhiteSpace(_estadoFiltro) ? null : _estadoFiltro);

        _cargando = true;
        _errorCarga = false;
        StateHasChanged();

        try
        {
            var resultado = await Mediator.Send(consulta, _ciclo.Token);
            if (!EsVigente(carga))
                return;

            _totalElementos = resultado.TotalElementos;
            _elementosPagina = resultado.Elementos.ToList();
            _seleccionados.Clear();
            _expandidos.Clear();
            _clientesPorEmpresa.Clear();
            _clientesConError.Clear();
            _idEnfocado = null;
        }
        catch (Exception) when (!EsVigente(carga))
        {
            // Una carga superada que falla no es un error de la vigente: no
            // puede tapar su resultado con el estado de error.
        }
        catch (Exception)
        {
            _errorCarga = true;
        }
        finally
        {
            if (EsVigente(carga))
            {
                _cargando = false;
                StateHasChanged();
            }
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

    private bool HayFiltrosActivos =>
        !string.IsNullOrWhiteSpace(_busqueda) || !string.IsNullOrWhiteSpace(_estadoFiltro);

    /// <summary>
    /// Rótulo del chip del filtro documental. Si el valor de la URL no está en
    /// el catálogo se cae al valor crudo en vez de romper: la coordenada viene
    /// de fuera y no es autoridad sobre lo que existe.
    /// </summary>
    private string EtiquetaFiltroEstado =>
        "Documentación: " + (EstadoDocumentoUi.OpcionesDocumentales.FirstOrDefault(o => o.Valor == _estadoFiltro)?.Texto ?? _estadoFiltro);

    private Task QuitarFiltroBusquedaAsync() => BuscarAsync(string.Empty);

    private Task QuitarFiltroEstadoAsync() => CambiarEstadoAsync(string.Empty);

    /// <summary>
    /// Quita los dos filtros en una sola recarga y una sola navegación.
    /// Encadenar <see cref="BuscarAsync"/> y <see cref="CambiarEstadoAsync"/>
    /// lanzaría dos consultas; y escribir la URL con dos llamadas seguidas
    /// dejaba, entre la primera y la segunda, una URL con <c>estado</c> todavía
    /// puesto que <see cref="OnParametersSetAsync"/> leía como un cambio venido
    /// de fuera: reponía el filtro y lanzaba otra consulta.
    /// </summary>
    private async Task LimpiarFiltrosAsync()
    {
        _busqueda = string.Empty;
        _estadoFiltro = string.Empty;
        NavigationManager.ActualizarFiltrosEnUrl(new Dictionary<string, string?> { ["q"] = null, ["estado"] = null });
        await CargarAsync(resetPagina: true);
    }

    /// <summary>
    /// «N de M empresas». M es el total que devuelve la consulta, que con
    /// filtros ya viene filtrado — por eso la frase lo dice, y no promete
    /// cuántas hay sin filtro, que esta pantalla no sabe.
    /// </summary>
    private string TextoConteo =>
        $"{_elementosPagina.Count} de {_totalElementos} {(_totalElementos == 1 ? "empresa" : "empresas")}"
        + (HayFiltrosActivos ? " con estos filtros" : string.Empty);

    private string ClaseRejilla(string claseBase) =>
        $"{claseBase} rejilla-empresas" + (_seleccionMultiple ? " rejilla-empresas-seleccion" : string.Empty);

    /// <summary>
    /// La fila cuya vista previa está abierta se marca, para no perder de
    /// vista a qué fila corresponde el panel de la derecha.
    /// </summary>
    private string ClaseTarjeta(Guid id) =>
        "tarjeta-fila-acordeon"
        + (id == _idEnfocado ? " fila-enfocada" : string.Empty)
        + (_previewVisible && id == _previewEmpresaId ? " fila-empresa-en-vista-previa" : string.Empty);

    /// <summary>
    /// Nombre accesible del anillo. Antes se interpolaba el porcentaje sin
    /// mirar si existía, y una empresa sin cumplimiento calculable se anunciaba
    /// como «% de cumplimiento…» — un número que no hay. Null significa que no
    /// tiene actividad en ningún Centro o que ninguno tiene requisitos
    /// aplicables (ver <see cref="EmpresaListaDto"/>).
    /// </summary>
    private static string EtiquetaCumplimiento(int? porcentaje) =>
        porcentaje is { } p
            ? $"{p}% de cumplimiento acumulado en los centros donde esta empresa tiene actividad"
            : "Sin actividad en ningún centro con requisitos aplicables";

    /// <summary>
    /// De dónde sale la columna Documentación: es el PEOR estado de vigencia
    /// de los documentos de la Empresa. El mockup explica cada estado con
    /// plazos fijos («7 días», «30 días»), pero los umbrales de urgente y
    /// próximo son configurables (<c>CalculadoraEstadoDocumento</c>), así que
    /// no se escribe ningún número.
    /// </summary>
    private static string TituloEstadoDocumental(EstadoDocumento? estado) =>
        estado is null
            ? "Todavía no tiene ningún documento"
            : $"Peor estado de vigencia entre sus documentos: {EstadoDocumentoUi.Texto(estado.Value)}";

    private static string TituloClientes(int cantidad) =>
        cantidad == 1 ? "Presta servicio a 1 cliente empresarial" : $"Presta servicio a {cantidad} clientes empresariales";

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
        if (!visible)
            QuitarAccionDeLaUrl();
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
        // Un segundo clic con el alta en vuelo crearía la misma Empresa dos veces.
        if (_guardando)
            return;

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
            QuitarAccionDeLaUrl();
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

    private Task ReintentarClientesAsync(Guid empresaId)
    {
        _clientesPorEmpresa.Remove(empresaId);
        _clientesConError.Remove(empresaId);
        return CargarClientesDeEmpresaAsync(empresaId);
    }

    private async Task CargarClientesDeEmpresaAsync(Guid empresaId)
    {
        if (_desechado)
            return;

        // La respuesta solo vale para la lista que se veía al pedirla: tras
        // recargar, la fila ya no está desplegada y su entrada se limpió.
        var cargaDeLaLista = _cargaVigente;

        try
        {
            var resultado = await Mediator.Send(new ObtenerClientesDeEmpresaQuery(empresaId), _ciclo.Token);
            if (!EsVigente(cargaDeLaLista))
                return;

            _clientesPorEmpresa[empresaId] = resultado;
        }
        catch (Exception) when (!EsVigente(cargaDeLaLista))
        {
            return;
        }
        catch (Exception)
        {
            _clientesPorEmpresa[empresaId] = [];
            _clientesConError.Add(empresaId);
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
