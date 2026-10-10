using CaeManager.Application.Common;
using CaeManager.Application.Documentos.Queries.ObtenerDocumentos;
using CaeManager.Application.Vehiculos.Commands.GuardarNotaInternaVehiculo;
using CaeManager.Application.Vehiculos.Queries.ObtenerVehiculoPorId;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Web.Components;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Components.Workspace;
using CaeManager.Web.Features.Documentos.Components;
using CaeManager.Web.Services;
using FluentValidation;
using MediatR;
using Microsoft.AspNetCore.Components;

namespace CaeManager.Web.Features.Vehiculos.Pages;

/// <summary>
/// Vehículo 360, página (<c>/vehiculos/{id}</c>): la ficha entera del Vehículo,
/// junto al panel del Context Workspace (<c>VehiculoWorkspacePanel</c>), que se
/// sigue abriendo para editar sus datos.
///
/// <para>
/// <b>Mismas consultas y mismo alcance que el panel.</b> La cabecera sale de
/// <see cref="ObtenerVehiculoPorIdQuery"/> (que devuelve null fuera del alcance:
/// no encontrado y sin acceso no se distinguen) y la lista de
/// <see cref="ObtenerDocumentosQuery"/> con el Vehículo como propietario, la
/// misma de <c>PestanaDocumentacion</c>. La página no calcula estados: el
/// anillo y el peor estado vienen en el detalle, y el estado de cada documento
/// en su fila.
/// </para>
///
/// <para>
/// La lista pinta como mucho <see cref="MaximoDocumentos"/> documentos, igual
/// que el panel; si hay más, el encabezado lo dice, y avisa también
/// si algún vencido quedó fuera de ese tope.
/// </para>
/// </summary>
public partial class VehiculoDetalle : CaeManager.Web.Components.PaginaInteractiva, IDisposable
{
    /// <summary>Tope de documentos pintados, el mismo de <c>PestanaDocumentacion</c>.</summary>
    internal const int MaximoDocumentos = 50;

    internal const string PestanaPorDefecto = "documentacion";

    private static readonly (string Id, string Clave)[] _pestanas =
    [
        ("documentacion", "EtiquetaDocumentacion"),
        ("historial", "PestanaHistorial")
    ];

    [Parameter] public Guid VehiculoId { get; set; }

    /// <summary>
    /// Pestaña activa, en la URL (<c>?pestana=historial</c>; sin parámetro,
    /// Documentación): se puede enlazar y recargar sin perder dónde se estaba.
    /// Un valor desconocido cae a Documentación.
    /// </summary>
    [Parameter, SupplyParameterFromQuery(Name = "pestana")] public string? Pestana { get; set; }

    [Inject] private IMediator Mediator { get; set; } = default!;
    [Inject] private ContextWorkspaceService WorkspaceService { get; set; } = default!;
    [Inject] private NavigationManager NavigationManager { get; set; } = default!;
    [Inject] private ToastService ToastService { get; set; } = default!;

    private VehiculoDetalleDto? _detalle;
    private bool _cargando = true;
    private bool _error;

    private IReadOnlyList<DocumentoListaDto> _documentos = [];
    private int _totalDocumentos;
    private bool _cargandoDocumentos;
    private bool _errorDocumentos;

    private DrawerGestionDocumento? _drawerGestion;

    private string _pestanaActiva = PestanaPorDefecto;
    private string? _pestanaDeLaUrl;

    /// <summary>
    /// Vehículo que esta instancia tiene cargado. Blazor reutiliza la instancia
    /// al pasar de un vehículo a otro, y cambiar de pestaña también llega por
    /// <see cref="OnParametersSetAsync"/> (viaja en la URL): sin esto, cada
    /// cambio de pestaña relanzaría la carga entera.
    /// </summary>
    private Guid? _vehiculoCargado;

    /// <summary>
    /// Generación del vehículo que se enseña y un contador por carga. Cambiar de
    /// vehículo y retirar la página los suben: cada respuesta compara el suyo
    /// antes de escribir y, si ya no es el vigente, se descarta.
    /// </summary>
    private int _generacion;
    private int _cargaCabecera;
    private int _cargaDocumentos;

    /// <summary>
    /// Los contadores impiden que una respuesta tardía escriba; esto corta la
    /// consulta misma. El token se copia al inicializar porque leer
    /// <c>Token</c> de un <see cref="CancellationTokenSource"/> ya desechado lanza.
    /// </summary>
    private readonly CancellationTokenSource _ciclo = new();
    private CancellationToken _cancelacion;

    /// <summary>Si el panel estaba abierto en el último aviso: al pasar a cerrado, la ficha se vuelve a leer.</summary>
    private bool _panelAbierto;

    private IReadOnlyList<BreadcrumbElemento> Miguero =>
        [new BreadcrumbElemento(Textos["TituloPagina"]), new BreadcrumbElemento(_detalle?.Nombre ?? "…")];

    protected override void OnInitialized()
    {
        _cancelacion = _ciclo.Token;
        _panelAbierto = WorkspaceService.EstaAbierto;
        WorkspaceService.OnCambio += AlCambiarElPanel;
    }

    protected override async Task OnParametersSetAsync()
    {
        AdoptarPestanaDeLaUrl();

        if (_vehiculoCargado == VehiculoId)
            return;

        _vehiculoCargado = VehiculoId;
        ReiniciarParaNuevoVehiculo();
        await CargarTodoAsync();
    }

    /// <summary>
    /// La pestaña de la URL se adopta cuando cambia AHÍ FUERA —un deep link, el
    /// botón «atrás»—, no en cada repintado: el clic ya la puso en local antes
    /// de que la URL la confirme.
    /// </summary>
    private void AdoptarPestanaDeLaUrl()
    {
        if (string.Equals(_pestanaDeLaUrl, Pestana, StringComparison.Ordinal))
            return;

        _pestanaDeLaUrl = Pestana;
        _pestanaActiva = Normalizar(Pestana);
    }

    private static string Normalizar(string? pestana) =>
        _pestanas.Any(p => p.Id == pestana) ? pestana! : PestanaPorDefecto;

    private void CambiarPestana(string pestana)
    {
        _pestanaActiva = Normalizar(pestana);
        _pestanaDeLaUrl = _pestanaActiva == PestanaPorDefecto ? null : _pestanaActiva;
        NavigationManager.ActualizarFiltroEnUrl("pestana", _pestanaDeLaUrl);
    }

    /// <summary>Invalida cualquier carga en vuelo: su respuesta ya no escribirá nada.</summary>
    private void InvalidarCargas()
    {
        _generacion++;
        _cargaCabecera++;
        _cargaDocumentos++;
    }

    private void ReiniciarParaNuevoVehiculo()
    {
        InvalidarCargas();
        _detalle = null;
        _error = false;
        _documentos = [];
        _totalDocumentos = 0;
        _cargandoDocumentos = false;
        _errorDocumentos = false;
        _editorNotaVisible = false;
        _guardandoNota = false;
        _notaEnEdicion = string.Empty;
        _borradorNotaTrasConflicto = null;
        _mensajeErrorNota = null;
        _errorCampoNota = null;
    }

    /// <summary>
    /// Cabecera y, detrás, los documentos, en serie: las dos consultas comparten
    /// el DbContext del circuito. Cada paso pinta en cuanto llega.
    /// </summary>
    private async Task CargarTodoAsync()
    {
        var generacion = _generacion;
        await CargarCabeceraAsync();
        if (generacion != _generacion || _detalle is null) return;

        _cargandoDocumentos = true;
        StateHasChanged();
        await CargarDocumentosAsync();
    }

    /// <summary>Un doble clic en «Reintentar» no lanza una segunda cadena: la primera ya puso _cargando.</summary>
    private Task ReintentarAsync() => _cargando ? Task.CompletedTask : CargarTodoAsync();

    private async Task CargarCabeceraAsync(bool silenciosa = false)
    {
        var carga = ++_cargaCabecera;
        var id = VehiculoId;

        // Una relectura tras guardar no vuelve al esqueleto: la ficha se queda
        // pintada y cambia cuando llega la respuesta.
        if (!silenciosa)
        {
            _cargando = true;
            _error = false;
        }

        try
        {
            var detalle = await Mediator.Send(new ObtenerVehiculoPorIdQuery(id), _cancelacion);
            if (carga != _cargaCabecera) return;
            _detalle = detalle;
            _error = detalle is null;
        }
        catch (Exception)
        {
            // En una relectura silenciosa se conserva lo que había: un fallo
            // de refresco no tumba una ficha que ya se estaba viendo.
            if (carga == _cargaCabecera && !silenciosa)
                _error = true;
        }
        finally
        {
            if (carga == _cargaCabecera)
                _cargando = false;
        }
    }

    private async Task CargarDocumentosAsync()
    {
        var carga = ++_cargaDocumentos;
        var id = VehiculoId;
        _cargandoDocumentos = true;
        _errorDocumentos = false;

        try
        {
            var pagina = await Mediator.Send(new ObtenerDocumentosQuery(
                TrabajadorId: null, Ambito: AmbitoAplicacion.Vehiculo, Busqueda: null,
                TamanoPagina: MaximoDocumentos, PropietarioId: id), _cancelacion);
            if (carga != _cargaDocumentos) return;
            _documentos = pagina.Elementos;
            _totalDocumentos = pagina.TotalElementos;
        }
        catch (Exception)
        {
            if (carga == _cargaDocumentos)
                _errorDocumentos = true;
        }
        finally
        {
            if (carga == _cargaDocumentos)
            {
                _cargandoDocumentos = false;
                StateHasChanged();
            }
        }
    }

    /// <summary>
    /// Tras subir un documento o cerrar el panel: anillo y lista se
    /// vuelven a leer, sin pasar por el esqueleto.
    /// </summary>
    private async Task RecargarTrasCambioAsync()
    {
        var generacion = _generacion;
        await CargarCabeceraAsync(silenciosa: true);
        if (generacion != _generacion) return;

        // Si el Vehículo ya no existe o salió del alcance, la ficha pasa a su
        // estado de error: nadie más repinta en este camino.
        if (_detalle is null)
        {
            StateHasChanged();
            return;
        }

        await CargarDocumentosAsync();
    }

    /// <summary>
    /// El panel avisa de cada cambio de su pila. Lo que interesa es el paso de
    /// abierto a cerrado: ahí pudo haberse editado el Vehículo o renovado un
    /// documento, y la ficha de debajo se quedaría con los datos de antes.
    /// </summary>
    private void AlCambiarElPanel()
    {
        var abierto = WorkspaceService.EstaAbierto;
        var seCerro = _panelAbierto && !abierto;
        _panelAbierto = abierto;

        if (seCerro && _detalle is not null)
            _ = InvokeAsync(RecargarTrasCambioAsync);
    }

    /// <summary>
    /// Al navegar fuera se invalida lo que estuviera en vuelo y se cancela la
    /// consulta emitida; la cancelación llega como una excepción que el catch
    /// de cada carga descarta, porque su contador dejó de ser el vigente.
    /// </summary>
    public void Dispose()
    {
        WorkspaceService.OnCambio -= AlCambiarElPanel;
        InvalidarCargas();
        _ciclo.Cancel();
        _ciclo.Dispose();
    }

    // ── Textos derivados ──────────────────────────────────────────────────

    private string Plural(int n, string claveUno, string claveVarios) =>
        n == 1 ? Textos[claveUno] : Textos[claveVarios, n];

    /// <summary>Lo que el anillo dice a un lector de pantalla: la fracción, o que no hay documentos.</summary>
    private string EtiquetaAnillo =>
        _detalle is { DocumentosAlDia: { Requeridos: > 0 } fraccion }
            ? Textos["FichaAnilloEtiqueta", fraccion.AlDia, fraccion.Requeridos]
            : Textos["FichaAnilloSinDocumentos"];

    /// <summary>
    /// El empleador solo enlaza cuando es una Empresa: es la única que hoy tiene
    /// ficha 360 con ruta propia (<c>/empresas/{id}</c>).
    /// </summary>
    private string? RutaEmpleador =>
        _detalle?.EmpresaId is { } empresaId ? $"/empresas/{empresaId}" : null;

    /// <summary>
    /// El peor estado dice Vencido pero ningún documento pintado lo está: el
    /// vencido quedó fuera de los <see cref="MaximoDocumentos"/> de la lista.
    /// El encabezado de la lista lo dice sin nombrarlo, en vez de callar una alerta.
    /// </summary>
    private bool VencidosFueraDeLaLista =>
        _detalle?.PeorEstadoDocumental == EstadoDocumento.Vencido
        && !_cargandoDocumentos && !_errorDocumentos
        && _totalDocumentos > _documentos.Count
        && !_documentos.Any(d => d.Estado == EstadoDocumento.Vencido);

    private IReadOnlyList<PestanaDefinicion> PestanasConRecuento =>
        _pestanas.Select(p =>
        {
            var definicion = new PestanaDefinicion(p.Id, Textos[p.Clave]);
            return p.Id == "documentacion" && !_cargandoDocumentos && !_errorDocumentos
                ? definicion with { Contador = new ContadorPestana(_totalDocumentos, Plural(_totalDocumentos, "FichaGlosaDocumentoUno", "FichaGlosaDocumentoVarios")) }
                : definicion;
        }).ToList();

    // ── Navegación y acciones ─────────────────────────────────────────────

    private void IrABreadcrumb(int indice)
    {
        if (indice == 0)
            NavigationManager.NavigateTo("/vehiculos");
    }

    /// <summary>Editar vive en el panel del Vehículo, con su aviso de cambios sin guardar.</summary>
    private Task AbrirInformacionAsync() =>
        WorkspaceService.AbrirAsync(EntidadWorkspace.Vehiculo, VehiculoId, _detalle?.Nombre ?? string.Empty, "informacion");

    // ── Nota interna ──────────────────────────────────────────────────────

    private bool _editorNotaVisible;
    private bool _guardandoNota;
    private string _notaEnEdicion = string.Empty;
    /// <summary>Lo que el usuario escribió cuando otra persona guardó antes: se recupera al reabrir el editor, nunca se reenvía solo.</summary>
    private string? _borradorNotaTrasConflicto;
    private string? _mensajeErrorNota;
    private string? _errorCampoNota;
    private readonly InstantaneaFormulario _instantaneaNota = new();

    /// <summary>
    /// La <c>Version</c> que la ficha enseñaba al abrirse el editor: es la que se manda. Con el editor abierto la ficha
    /// puede releerse (al cerrarse el panel del Vehículo) y traer una versión posterior; mandar esa pisaría, sin
    /// conflicto, una nota que quien edita no ha visto.
    /// </summary>
    private Guid _versionAlAbrirNota;

    /// <summary>Lo lee DrawerFormulario, que lleva dentro el guardián de cerrar y de navegar; cerrado no hay nada que perder.</summary>
    private bool HayCambiosEnLaNota => _editorNotaVisible && _instantaneaNota.Difiere(_notaEnEdicion);

    private void AbrirEditorNota()
    {
        if (_detalle is null) return;

        _notaEnEdicion = _borradorNotaTrasConflicto ?? _detalle.Notas ?? string.Empty;
        _borradorNotaTrasConflicto = null;
        _mensajeErrorNota = null;
        _errorCampoNota = null;
        _versionAlAbrirNota = _detalle.Version;
        _instantaneaNota.Fijar(_notaEnEdicion);
        _editorNotaVisible = true;
    }

    private void CambiarVisibilidadEditorNota(bool visible) => _editorNotaVisible = visible;

    /// <summary>
    /// Manda solo la nota y la <c>Version</c> que la ficha enseñaba al abrir el editor. Si otra persona guardó entre medias —la nota o los
    /// datos del vehículo, que comparten <c>Version</c>—, el servidor responde el conflicto: el editor se cierra, se
    /// relee la cabecera (la tarjeta enseña lo que hay ahora) y el texto propio queda como borrador para reaplicarlo
    /// al reabrir. Nunca se reenvía con la versión nueva sin pasar por ahí. Tras guardar bien también se relee, porque
    /// la <c>Version</c> cambió. Otros fallos solo muestran el motivo.
    /// </summary>
    private async Task GuardarNotaAsync()
    {
        if (_detalle is null || _guardandoNota) return;

        var generacion = _generacion;
        var detalle = _detalle;
        _guardandoNota = true;
        _mensajeErrorNota = null;
        _errorCampoNota = null;

        try
        {
            var resultado = await Mediator.Send(new GuardarNotaInternaVehiculoCommand(detalle.Id, _notaEnEdicion, _versionAlAbrirNota));

            if (resultado.EsFallido)
            {
                if (generacion != _generacion) return;
                // Un conflicto cierra el editor y relee: reenviar con la versión nueva sin mirarla pisaría en silencio
                // la nota de la otra persona.
                if (resultado.Error.Codigo == ConcurrenciaOptimista.CodigoConflicto)
                {
                    _borradorNotaTrasConflicto = _notaEnEdicion;
                    ToastService.Mostrar(resultado.Error.Mensaje, TonoToast.Error);
                    _editorNotaVisible = false;
                    await CargarCabeceraAsync(silenciosa: true);
                }
                else
                {
                    _mensajeErrorNota = resultado.Error.Mensaje;
                }
                return;
            }

            // Lo guardado se confirma aunque la ficha ya enseñe otro vehículo.
            ToastService.Mostrar(Textos["ToastNotaInternaGuardada"], TonoToast.Exito);
            if (generacion != _generacion) return;
            _editorNotaVisible = false;

            // La tarjeta enseña ya lo guardado aunque la relectura de abajo falle; la Version buena la trae la relectura.
            _detalle = detalle with { Notas = string.IsNullOrWhiteSpace(_notaEnEdicion) ? null : _notaEnEdicion.Trim() };
            await CargarCabeceraAsync(silenciosa: true);
        }
        catch (ValidationException ex)
        {
            if (generacion == _generacion)
                _errorCampoNota = ex.Errors.FirstOrDefault()?.ErrorMessage ?? Textos["ErrorGuardarNotaInterna"];
        }
        catch (Exception)
        {
            if (generacion == _generacion)
                _mensajeErrorNota = Textos["ErrorGuardarNotaInterna"];
        }
        finally
        {
            if (generacion == _generacion)
                _guardandoNota = false;
        }
    }

    /// <summary>La consulta rápida del documento: su panel, donde hoy se renueva y se confirma la vigencia.</summary>
    private Task AbrirDocumentoAsync(DocumentoListaDto documento) =>
        WorkspaceService.AbrirAsync(EntidadWorkspace.Documento, documento.Id, documento.TipoDocumentoNombre, "informacion");

    private Task SubirDocumentoAsync() =>
        _drawerGestion is { } drawer ? drawer.AbrirCrearParaVehiculoAsync(VehiculoId) : Task.CompletedTask;
}
