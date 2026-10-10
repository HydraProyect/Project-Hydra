using System.Globalization;
using CaeManager.Application.Asignaciones.Queries.ObtenerAsignacionesDocumentacionPorCentro;
using CaeManager.Application.Centros;
using CaeManager.Application.Clientes.Queries.ObtenerClientesParaSelector;
using CaeManager.Application.Common;
using CaeManager.Application.Empresas.Queries.ObtenerEmpresasParaSelector;
using CaeManager.Application.Subcontratas.Commands.CambiarNivelServicioSubcontrata;
using CaeManager.Application.Subcontratas.Commands.EliminarSubcontrata;
using CaeManager.Application.Subcontratas.Commands.EliminarVerificacionExterna;
using CaeManager.Application.Subcontratas.Commands.GuardarNotaInternaSubcontrata;
using CaeManager.Application.Subcontratas.Queries.ObtenerCentrosConActividadDeSubcontrata;
using CaeManager.Application.Subcontratas.Queries.ObtenerCredencialAccesoSubcontrata;
using CaeManager.Application.Subcontratas.Queries.ObtenerCumplimientoSubcontrata;
using CaeManager.Application.Subcontratas.Queries.ObtenerSubcontrataPorId;
using CaeManager.Application.Subcontratas.Queries.ObtenerSupervisionSubcontrata;
using CaeManager.Application.Subcontratas.Queries.ObtenerTrabajadoresDocumentacionPorSubcontrata;
using CaeManager.Application.Usuarios.Queries.ObtenerDobleFactorPropio;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Subcontratas;
using CaeManager.Web.Components;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Components.Workspace;
using CaeManager.Web.Features.Documentos;
using CaeManager.Web.Features.Documentos.Components;
using CaeManager.Web.Features.Subcontratas.Components;
using CaeManager.Web.Services;
using FluentValidation;
using MediatR;
using Microsoft.AspNetCore.Components;

namespace CaeManager.Web.Features.Subcontratas.Pages;

/// <summary>
/// Subcontrata 360, página (mockup «Subcontrata 360 página TALVEG», primer incremento: «con lo que ya viaja»).
/// «Subcontrata» es el papel que ocupa una Empresa en una Relación Empresarial —la proveedora—, no un tipo de
/// Empresa: la misma Empresa puede ser contratista directa en otra cadena.
///
/// <para>
/// Compone, con las MISMAS consultas que <c>SubcontrataWorkspacePanel</c> y que la fila desplegada del listado
/// (<c>AcordeonTrabajadoresSubcontrata</c>) —mismo alcance y misma RLS—, la identidad con el anillo
/// y las pestañas Trabajadores, Supervisión, Centros, Agenda e Historial. La edición de la identidad y
/// de las credenciales sigue viviendo en el panel: «Editar» lo abre.
/// </para>
///
/// <para>
/// <b>Qué no enseña todavía, y por qué</b> (cada punto es un incremento propio): la documentación de empresa de la
/// subcontrata y su pestaña «Gestión», los proyectos, los vehículos, el estado por Centro, la cadena de
/// subcontratación y quién registró cada verificación. Ningún DTO los trae hoy. El anillo, por lo mismo, solo
/// mide la documentación de sus trabajadores (<see cref="ObtenerCumplimientoSubcontrataQuery"/>).
/// </para>
///
/// <para>
/// <b>Credenciales del portal.</b> Son de un tercero y nada se pide al abrir: llegan al pulsar «Ver
/// credenciales», con las consultas del panel, que exigen rol con escritura y doble factor
/// (<c>AutorizacionSecretosDeTenantBehavior</c>) y dejan registro de cada lectura
/// (<c>IRegistroAccesoDatoSensibleService</c>). El aviso «activa la autenticación en dos pasos» sale de
/// <see cref="ObtenerDobleFactorPropioQuery"/>, que solo avisa: la denegación sigue siendo del servidor.
/// </para>
/// </summary>
public partial class SubcontrataDetalle : CaeManager.Web.Components.PaginaInteractiva, IDisposable
{
    internal const string PestanaTrabajadores = "trabajadores";
    internal const string PestanaSupervision = "supervision";
    internal const string PestanaCentros = "centros";
    internal const string PestanaAgenda = "agenda";
    internal const string PestanaHistorial = "historial";

    /// <summary>Tope de filas de la pestaña Trabajadores: si hay más, el resumen lo dice (mismo tope que el panel).</summary>
    internal const int TamanoPaginaTrabajadores = 50;

    private static readonly string[] PestanasValidas =
        [PestanaTrabajadores, PestanaSupervision, PestanaCentros, PestanaAgenda, PestanaHistorial];

    [Parameter] public Guid SubcontrataId { get; set; }

    /// <summary>Pestaña activa, en la URL (sin parámetro = Trabajadores), como en las demás fichas 360.</summary>
    [Parameter, SupplyParameterFromQuery(Name = "pestana")] public string? Pestana { get; set; }

    [Inject] private IMediator Mediator { get; set; } = default!;
    [Inject] private ContextWorkspaceService WorkspaceService { get; set; } = default!;
    [Inject] private NavigationManager NavigationManager { get; set; } = default!;
    [Inject] private ToastService ToastService { get; set; } = default!;

    private DrawerGestionDocumento? _drawerDocumento;
    private DrawerVerificacionExternaSubcontrata? _drawerVerificacion;

    private SubcontrataDetalleDto? _detalle;
    private FraccionCumplimiento? _cumplimiento;
    private bool _cargando = true;
    private bool _error;

    private IReadOnlyList<TrabajadorDocumentacionSubcontrataDto>? _trabajadores;
    private bool _cargandoTrabajadores = true;
    private bool _errorTrabajadores;
    private IReadOnlySet<string> _estadosMarcados = new HashSet<string>();
    private readonly HashSet<Guid> _trabajadoresDesplegados = [];

    private SupervisionSubcontrataDto? _supervision;
    private bool _cargandoSupervision = true;
    private bool _errorSupervision;

    private IReadOnlyList<CentroConActividadDto>? _centros;
    private bool _cargandoCentros = true;
    private bool _errorCentros;

    /// <summary>
    /// Nombres de las contrapartes de la relación, por Id. Los Ids de <see cref="SubcontrataDetalleDto"/> NO se
    /// acotan por cartera y los selectores sí pueden hacerlo: un Id sin nombre se cuenta, no se inventa.
    /// </summary>
    private IReadOnlyDictionary<Guid, string> _nombresClientes = new Dictionary<Guid, string>();
    private IReadOnlyDictionary<Guid, string> _nombresEmpresas = new Dictionary<Guid, string>();
    private bool _nombresCargados;
    private bool _errorNombres;

    /// <summary>
    /// Tarjeta «Acceso al portal». <c>null</c> = todavía no se sabe si la cuenta tiene doble factor (o no se pudo
    /// saber): la tarjeta ofrece «Ver credenciales» y el servidor decide al pedirlas.
    /// </summary>
    private bool? _dobleFactorActivo;
    private CredencialAccesoSubcontrataDto? _credencial;
    private bool _credencialConsultada;
    private bool _tieneContrasena;
    private bool _cargandoCredencial;
    private bool _errorCredencial;
    private bool _contrasenaVisible;

    private bool _cambiandoNivel;
    private bool _confirmarEliminarVisible;
    private bool _eliminando;
    private Guid _verificacionAEliminarId;
    private bool _confirmarEliminarVerificacionVisible;
    private bool _eliminandoVerificacion;

    private string _pestana = PestanaTrabajadores;
    private string? _pestanaDeLaUrl;
    private bool _urlAdoptada;

    /// <summary>
    /// Subcontrata que esta instancia tiene cargada: Blazor reutiliza la instancia al pasar de una a otra, y sin
    /// esto cada cambio de pestaña en la URL relanzaría la carga entera.
    /// </summary>
    private Guid? _subcontrataCargada;

    /// <summary>
    /// Generación de la subcontrata que se enseña y un contador por carga (patrón de EmpresaDetalle): cada respuesta
    /// compara el suyo antes de escribir y, si ya no es el vigente, se descarta.
    /// </summary>
    private int _generacion;
    private int _cargaDetalle;
    private int _cargaNombres;
    private int _cargaTrabajadores;
    private int _cargaSupervision;
    private int _cargaCentros;
    private int _cargaCredencial;
    private int _cargaDobleFactor;

    /// <summary>
    /// Los contadores impiden que una respuesta tardía escriba; el token corta la consulta misma. Las órdenes
    /// (cambiar nivel, eliminar, registrar) NO lo llevan: salir de la ficha no deshace lo que el usuario ya pidió.
    /// </summary>
    private readonly CancellationTokenSource _ciclo = new();
    private CancellationToken _cancelacion;

    private IReadOnlyList<BreadcrumbElemento> Miguero =>
        [new BreadcrumbElemento(Textos["MigaSubcontratas"]), new BreadcrumbElemento(_detalle?.RazonSocial ?? "…")];

    private NivelServicioSubcontrata NivelOpuesto =>
        _detalle?.NivelServicio == NivelServicioSubcontrata.Supervisada
            ? NivelServicioSubcontrata.Gestionada
            : NivelServicioSubcontrata.Supervisada;

    protected override void OnInitialized() => _cancelacion = _ciclo.Token;

    protected override async Task OnParametersSetAsync()
    {
        AdoptarUrl();

        if (_subcontrataCargada == SubcontrataId)
            return;

        _subcontrataCargada = SubcontrataId;
        ReiniciarParaNuevaSubcontrata();
        await CargarTodoAsync();
    }

    /// <summary>Adopta la pestaña cuando cambia en la URL ahí fuera (un enlace, el botón «atrás»).</summary>
    private void AdoptarUrl()
    {
        if (!_urlAdoptada || !string.Equals(_pestanaDeLaUrl, Pestana, StringComparison.Ordinal))
        {
            _pestanaDeLaUrl = Pestana;
            _pestana = PestanasValidas.Contains(Pestana) ? Pestana! : PestanaTrabajadores;
        }

        _urlAdoptada = true;
    }

    private void InvalidarCargas()
    {
        _generacion++;
        _cargaDetalle++;
        _cargaNombres++;
        _cargaTrabajadores++;
        _cargaSupervision++;
        _cargaCentros++;
        _cargaCredencial++;
        _cargaDobleFactor++;
    }

    /// <summary>Nada de la subcontrata anterior sobrevive al cambio, tampoco su credencial.</summary>
    private void ReiniciarParaNuevaSubcontrata()
    {
        InvalidarCargas();
        _detalle = null;
        _cumplimiento = null;
        _error = false;
        _trabajadores = null;
        _cargandoTrabajadores = true;
        _errorTrabajadores = false;
        _estadosMarcados = new HashSet<string>();
        _trabajadoresDesplegados.Clear();
        _supervision = null;
        _cargandoSupervision = true;
        _errorSupervision = false;
        _centros = null;
        _cargandoCentros = true;
        _errorCentros = false;
        _nombresClientes = new Dictionary<Guid, string>();
        _nombresEmpresas = new Dictionary<Guid, string>();
        _nombresCargados = false;
        _errorNombres = false;
        _credencial = null;
        _credencialConsultada = false;
        _cargandoCredencial = false;
        _errorCredencial = false;
        _contrasenaVisible = false;
        _cambiandoNivel = false;
        _confirmarEliminarVisible = false;
        _eliminando = false;
        _confirmarEliminarVerificacionVisible = false;
        _eliminandoVerificacion = false;
        _drawerVerificacion?.Descartar();
        _editorNotaVisible = false;
        _guardandoNota = false;
        _notaEnEdicion = string.Empty;
        _mensajeErrorNota = null;
        _errorCampoNota = null;
    }

    /// <summary>
    /// Trabajadores, supervisión y centros se piden en serie tras la cabecera, no al abrir su pestaña: de ellos salen
    /// los recuentos de la cabecera y los contadores de las pestañas. En serie porque comparten el
    /// DbContext del circuito.
    /// </summary>
    private async Task CargarTodoAsync()
    {
        var generacion = _generacion;
        await CargarDetalleAsync();
        if (generacion != _generacion || _detalle is null) return;

        await CargarNombresRelacionesAsync();
        if (generacion != _generacion) return;
        await CargarTrabajadoresAsync();
        if (generacion != _generacion) return;
        await CargarSupervisionAsync();
        if (generacion != _generacion) return;
        await CargarCentrosAsync();
        if (generacion != _generacion) return;

        // Mismo motivo que EmpresaDetalle: en el prerenderizado estático no hay circuito que vaya a usar el dato.
        if (RendererInfo.IsInteractive)
            await CargarDobleFactorAsync();
    }

    /// <summary>Un doble clic en «Reintentar» no lanza una segunda cadena: la primera ya puso <c>_cargando</c>.</summary>
    private Task ReintentarAsync() => _cargando ? Task.CompletedTask : CargarTodoAsync();

    private async Task CargarDetalleAsync()
    {
        var carga = ++_cargaDetalle;
        var id = SubcontrataId;
        _cargando = true;
        _error = false;

        try
        {
            // Fuera de alcance (o inexistente) la consulta devuelve null, y la página no distingue un caso del
            // otro: enseñar «sin acceso» revelaría que el registro existe en otra organización.
            var detalle = await Mediator.Send(new ObtenerSubcontrataPorIdQuery(id), _cancelacion);
            if (carga != _cargaDetalle) return;
            if (detalle is null)
            {
                _detalle = null;
                _error = true;
                return;
            }

            // El anillo es un dato secundario: si su consulta falla, la ficha se abre sin porcentaje en vez de
            // decir «no encontramos esta subcontrata» de una que sí se encontró.
            FraccionCumplimiento? cumplimiento = null;
            try
            {
                cumplimiento = await Mediator.Send(new ObtenerCumplimientoSubcontrataQuery(id), _cancelacion);
            }
            catch (Exception) when (!_cancelacion.IsCancellationRequested)
            {
            }

            if (carga != _cargaDetalle) return;
            _detalle = detalle;
            _cumplimiento = cumplimiento;
        }
        catch (Exception)
        {
            if (carga == _cargaDetalle)
                _error = true;
        }
        finally
        {
            if (carga == _cargaDetalle)
                _cargando = false;
        }
    }

    /// <summary>Tras una orden que cambia la cabecera (nivel de servicio): la relee sin tocar las pestañas.</summary>
    private async Task RecargarCabeceraAsync()
    {
        var carga = ++_cargaDetalle;
        var id = SubcontrataId;

        try
        {
            var detalle = await Mediator.Send(new ObtenerSubcontrataPorIdQuery(id), _cancelacion);
            if (carga != _cargaDetalle) return;
            if (detalle is null)
            {
                _detalle = null;
                _error = true;
                return;
            }

            _detalle = detalle;
        }
        catch (Exception)
        {
            if (carga == _cargaDetalle)
                ToastService.Mostrar(Textos["ErrorRecargarCabecera"], TonoToast.Error);
        }
    }

    private async Task RecargarCumplimientoAsync()
    {
        var generacion = _generacion;
        try
        {
            var cumplimiento = await Mediator.Send(new ObtenerCumplimientoSubcontrataQuery(SubcontrataId), _cancelacion);
            if (generacion == _generacion)
                _cumplimiento = cumplimiento;
        }
        catch (Exception)
        {
            // El anillo conserva la cifra anterior: la lista ya recargada dice lo que cambió.
        }
    }

    private async Task CargarNombresRelacionesAsync()
    {
        if (_detalle is null) return;

        var carga = ++_cargaNombres;
        var detalle = _detalle;
        _errorNombres = false;

        if (detalle.ClienteIds.Count == 0 && detalle.EmpresaIds.Count == 0)
        {
            _nombresCargados = true;
            return;
        }

        try
        {
            var clientes = detalle.ClienteIds.Count == 0 ? [] : await Mediator.Send(new ObtenerClientesParaSelectorQuery(), _cancelacion);
            if (carga != _cargaNombres) return;
            var empresas = detalle.EmpresaIds.Count == 0 ? [] : await Mediator.Send(new ObtenerEmpresasParaSelectorQuery(), _cancelacion);
            if (carga != _cargaNombres) return;

            _nombresClientes = clientes.GroupBy(c => c.Id).ToDictionary(g => g.Key, g => g.First().RazonSocial);
            _nombresEmpresas = empresas.GroupBy(e => e.Id).ToDictionary(g => g.Key, g => g.First().RazonSocial);
            _nombresCargados = true;
        }
        catch (Exception)
        {
            if (carga == _cargaNombres)
                _errorNombres = true;
        }
        finally
        {
            if (carga == _cargaNombres)
                StateHasChanged();
        }
    }

    private Task ReintentarTrabajadoresAsync() => _cargandoTrabajadores ? Task.CompletedTask : CargarTrabajadoresAsync();

    private async Task CargarTrabajadoresAsync()
    {
        var carga = ++_cargaTrabajadores;
        var id = SubcontrataId;
        _cargandoTrabajadores = true;
        _errorTrabajadores = false;

        try
        {
            var resultado = await Mediator.Send(new ObtenerTrabajadoresDocumentacionPorSubcontrataQuery(id), _cancelacion);
            if (carga != _cargaTrabajadores) return;
            _trabajadores = resultado;
        }
        catch (Exception)
        {
            if (carga == _cargaTrabajadores)
                _errorTrabajadores = true;
        }
        finally
        {
            if (carga == _cargaTrabajadores)
            {
                _cargandoTrabajadores = false;
                StateHasChanged();
            }
        }
    }

    private Task ReintentarSupervisionAsync() => _cargandoSupervision ? Task.CompletedTask : CargarSupervisionAsync();

    private async Task CargarSupervisionAsync()
    {
        var carga = ++_cargaSupervision;
        var id = SubcontrataId;
        _cargandoSupervision = true;
        _errorSupervision = false;

        try
        {
            var resultado = await Mediator.Send(new ObtenerSupervisionSubcontrataQuery(id), _cancelacion);
            if (carga != _cargaSupervision) return;
            _supervision = resultado ?? new SupervisionSubcontrataDto([], []);
        }
        catch (Exception)
        {
            if (carga == _cargaSupervision)
                _errorSupervision = true;
        }
        finally
        {
            if (carga == _cargaSupervision)
            {
                _cargandoSupervision = false;
                StateHasChanged();
            }
        }
    }

    private Task ReintentarCentrosAsync() => _cargandoCentros ? Task.CompletedTask : CargarCentrosAsync();

    private async Task CargarCentrosAsync()
    {
        var carga = ++_cargaCentros;
        var id = SubcontrataId;
        _cargandoCentros = true;
        _errorCentros = false;

        try
        {
            var resultado = await Mediator.Send(new ObtenerCentrosConActividadDeSubcontrataQuery(id), _cancelacion);
            if (carga != _cargaCentros) return;
            _centros = resultado;
        }
        catch (Exception)
        {
            if (carga == _cargaCentros)
                _errorCentros = true;
        }
        finally
        {
            if (carga == _cargaCentros)
            {
                _cargandoCentros = false;
                StateHasChanged();
            }
        }
    }

    private async Task CargarDobleFactorAsync()
    {
        var carga = ++_cargaDobleFactor;
        try
        {
            var activo = await Mediator.Send(new ObtenerDobleFactorPropioQuery(), _cancelacion);
            if (carga != _cargaDobleFactor) return;
            _dobleFactorActivo = activo;
            StateHasChanged();
        }
        catch (Exception)
        {
            // Sin el dato la tarjeta ofrece «Ver credenciales» y decide el servidor.
        }
    }

    // ---------- Trabajadores ----------

    /// <summary>Del peor estado al mejor, con el rango del punto único; a igual estado, por nombre.</summary>
    private IReadOnlyList<TrabajadorDocumentacionSubcontrataDto> TrabajadoresOrdenados =>
        (_trabajadores ?? [])
            .OrderBy(t => SeveridadEstadoDocumento.Rango(t.PeorEstado))
            .ThenBy(t => t.TrabajadorNombre, StringComparer.CurrentCulture)
            .ToList();

    private IReadOnlyList<TrabajadorDocumentacionSubcontrataDto> TrabajadoresFiltrados =>
        _estadosMarcados.Count == 0
            ? TrabajadoresOrdenados
            : TrabajadoresOrdenados.Where(t => _estadosMarcados.Contains(EstadoDocumentoFicha360.Clave(t.PeorEstado))).ToList();

    private IReadOnlyList<TrabajadorDocumentacionSubcontrataDto> TrabajadoresVisibles =>
        TrabajadoresFiltrados.Take(TamanoPaginaTrabajadores).ToList();

    /// <summary>Un contador por estado con alguna fila, en el orden de gravedad; el componente oculta los vacíos.</summary>
    private IReadOnlyList<OpcionEstadoRecuento> OpcionesDeEstado =>
        TrabajadoresOrdenados
            .GroupBy(t => EstadoDocumentoFicha360.Clave(t.PeorEstado))
            .Select(g => new OpcionEstadoRecuento(g.Key, EstadoDocumentoFicha360.Texto(g.First().PeorEstado), g.Count()))
            .ToList();

    private bool HayFiltrosActivos => _estadosMarcados.Count > 0;

    private void CambiarEstadosMarcados(IReadOnlySet<string> seleccion) => _estadosMarcados = seleccion;

    private string TextoResumenTrabajadores =>
        Textos["ResumenListaOrdenada", TrabajadoresVisibles.Count, _trabajadores?.Count ?? 0];

    private bool EstaDesplegado(Guid trabajadorId) => _trabajadoresDesplegados.Contains(trabajadorId);

    private void CambiarDespliegue(Guid trabajadorId, bool desplegado)
    {
        if (desplegado)
            _trabajadoresDesplegados.Add(trabajadorId);
        else
            _trabajadoresDesplegados.Remove(trabajadorId);
    }

    /// <summary>«2 de 5», o raya si no se le exige nada.</summary>
    private string TextoFraccion(FraccionCumplimiento cumplimiento) =>
        cumplimiento.Requeridos == 0 ? "—" : Textos["FraccionDe", cumplimiento.AlDia, cumplimiento.Requeridos];

    /// <summary>
    /// Por qué el trabajador está en su estado: el documento que lo causa, con su fecha. Vacío cuando está al día.
    /// </summary>
    private string MotivoDe(TrabajadorDocumentacionSubcontrataDto trabajador)
    {
        if (DocumentoQueLoCausa(trabajador) is not { } documento)
            return string.Empty;

        var fecha = documento.FechaVencimiento?.ToString("dd/MM/yyyy", CultureInfo.CurrentCulture);
        return documento.Estado switch
        {
            EstadoDocumento.Faltante => Textos["MotivoPendiente", documento.TipoDocumentoNombre],
            EstadoDocumento.Vencido or EstadoDocumento.EnTolerancia when fecha is not null => Textos["MotivoVencioEl", documento.TipoDocumentoNombre, fecha],
            EstadoDocumento.Urgente or EstadoDocumento.Proximo when fecha is not null => Textos["MotivoCaducaEl", documento.TipoDocumentoNombre, fecha],
            EstadoDocumento.SinConfirmar => Textos["MotivoSinConfirmar", documento.TipoDocumentoNombre],
            _ => Textos["MotivoGenerico", documento.TipoDocumentoNombre, EstadoDocumentoFicha360.Texto(documento.Estado)]
        };
    }

    /// <summary>El documento exigido en peor estado, si ese estado pide algo; <c>null</c> si el trabajador está al día.</summary>
    private static DocumentoRequeridoDto? DocumentoQueLoCausa(TrabajadorDocumentacionSubcontrataDto trabajador)
    {
        var peor = trabajador.Documentos
            .OrderBy(d => SeveridadEstadoDocumento.Rango(d.Estado))
            .FirstOrDefault();
        return peor is not null && EstadoDocumentoFicha360.Accion(peor.Estado) is not null ? peor : null;
    }

    private string TextoVigencia(DocumentoRequeridoDto documento) =>
        documento.FechaVencimiento is { } fecha
            ? Textos["VigenciaHasta", fecha.ToString("dd/MM/yyyy", CultureInfo.CurrentCulture)]
            : documento.DocumentoId is null
                ? Textos["VigenciaSeExigeYNoHay"]
                : documento.Estado == EstadoDocumento.SinConfirmar ? Textos["VigenciaFaltaLaFecha"] : Textos["VigenciaNoCaduca"];

    /// <summary>«Renovar», «Subir» o «Confirmar» según el estado; <c>null</c> si el documento no pide nada.</summary>
    private string? RotuloAccion(DocumentoRequeridoDto documento) => EstadoDocumentoFicha360.Accion(documento.Estado) switch
    {
        AccionDocumentoFicha360.Renovar => Textos["AccionRenovar"].Value,
        AccionDocumentoFicha360.Subir => Textos["AccionSubir"].Value,
        AccionDocumentoFicha360.Confirmar => Textos["AccionConfirmar"].Value,
        _ => null
    };

    /// <summary>
    /// Edita el documento si existe y lo crea para el que falta: lo mismo que «Gestionar» en la fila desplegada del
    /// listado. Crear y renovar son órdenes denegadas a Consulta en Application; aquí solo se ofrecen con escritura.
    /// </summary>
    private Task GestionarDocumentoAsync(Guid trabajadorId, DocumentoRequeridoDto documento)
    {
        if (_drawerDocumento is null) return Task.CompletedTask;

        return documento.DocumentoId is { } documentoId
            ? _drawerDocumento.AbrirEditarAsync(documentoId)
            : _drawerDocumento.AbrirCrearParaFaltanteAsync(trabajadorId, documento.TipoDocumentoId);
    }

    private async Task ManejarDocumentoGuardadoAsync()
    {
        await CargarTrabajadoresAsync();
        await RecargarCumplimientoAsync();
    }

    private Task AbrirPanelTrabajador(TrabajadorDocumentacionSubcontrataDto trabajador) =>
        WorkspaceService.AbrirAsync(EntidadWorkspace.Trabajador, trabajador.TrabajadorId, trabajador.TrabajadorNombre, "informacion");

    private void IrATrabajador360(Guid trabajadorId) => NavigationManager.NavigateTo($"/trabajadores/{trabajadorId}");

    private void IrAListaDeTrabajadores() => NavigationManager.NavigateTo("/trabajadores");

    // Avatar de la fila: las dos primeras palabras del nombre, como en Empresa 360. Un Trabajador no es un
    // usuario de la plataforma: AvatarUsuario no es para él.
    private static string Iniciales(string nombre) => string.Concat(nombre
        .Split(' ', StringSplitOptions.RemoveEmptyEntries)
        .Where(p => char.IsLetter(p[0]))
        .Take(2)
        .Select(p => char.ToUpperInvariant(p[0])));

    // ---------- Trabajadores con problema ----------

    /// <summary>
    /// Trabajadores con algún documento vencido o pendiente: ponen en alerta el contador de su pestaña.
    /// </summary>
    private IReadOnlyList<TrabajadorDocumentacionSubcontrataDto> TrabajadoresConProblema =>
        TrabajadoresOrdenados.Where(t => EstadoDocumentoFicha360.TonoFila(t.PeorEstado) == TonoFila.Peligro).ToList();

    // ---------- Supervisión ----------

    /// <summary>Los Centros con algún tipo que el Centro pide; los tipos que no pide no se enseñan en la página.</summary>
    private IReadOnlyList<SupervisionCentroDto> CentrosSupervisados =>
        (_supervision?.Centros ?? [])
            .Select(c => c with { Tipos = c.Tipos.Where(t => t.Exigido).OrderByDescending(t => GravedadSupervision(t.Estado)).ToList() })
            .Where(c => c.Tipos.Count > 0)
            .ToList();

    private int TiposConProblema => CentrosSupervisados.Sum(c => c.Tipos.Count(t => PideVerificar(t.Estado)));

    private bool HayCentrosDondeVerificar => _supervision is { CentrosSeleccionables.Count: > 0 };

    /// <summary>Con problema se ofrece «Verificar»; sin problema, «Ver evidencia».</summary>
    internal static bool PideVerificar(EstadoSupervision estado) =>
        estado is EstadoSupervision.SinVerificar or EstadoSupervision.NoValido or EstadoSupervision.Vencido or EstadoSupervision.Urgente;

    // Orden de pintado de los estados de la SUPERVISIÓN (no del documento): peor primero.
    private static int GravedadSupervision(EstadoSupervision estado) => estado switch
    {
        EstadoSupervision.NoValido => 5,
        EstadoSupervision.Vencido => 4,
        EstadoSupervision.SinVerificar => 3,
        EstadoSupervision.Urgente => 2,
        EstadoSupervision.Proximo => 1,
        _ => 0
    };

    /// <summary>
    /// Rótulo de la pastilla. Una verificación que caduca pronto se llama «Por vencer», con la palabra del
    /// vocabulario de estado; «No válido» y «Sin verificar» son de la supervisión, no del documento.
    /// </summary>
    private string TextoEstadoSupervision(EstadoSupervision estado) => estado switch
    {
        EstadoSupervision.Urgente or EstadoSupervision.Proximo => EstadoDocumentoFicha360.Texto(EstadoDocumento.Proximo),
        _ => EstadoSupervisionUi.Texto(Textos, estado)
    };

    internal static TonoBadge TonoEstadoSupervision(EstadoSupervision estado) => estado switch
    {
        EstadoSupervision.Vigente => TonoBadge.Exito,
        EstadoSupervision.Urgente or EstadoSupervision.Proximo => TonoBadge.Advertencia,
        _ => TonoBadge.Peligro
    };

    internal static TonoFila? TonoFilaSupervision(EstadoSupervision estado) => estado switch
    {
        EstadoSupervision.NoValido or EstadoSupervision.Vencido or EstadoSupervision.SinVerificar => TonoFila.Peligro,
        EstadoSupervision.Urgente => TonoFila.Advertencia,
        _ => null
    };

    private string DetalleVerificacion(SupervisionTipoDto tipo)
    {
        if (tipo.UltimaVerificacion is not { } ultima)
            return Textos["SupervisionNuncaComprobada"];

        var partes = new List<string>(3)
        {
            Textos["DetalleUltimaVerificacion", EstadoSupervisionUi.TextoResultado(Textos, ultima.Resultado),
                ultima.FechaVerificacion.ToString("dd/MM/yyyy", CultureInfo.CurrentCulture)]
        };
        if (ultima.ValidoHasta is { } validoHasta)
            partes.Add(Textos["SupervisionValidoHasta", validoHasta.ToString("dd/MM/yyyy", CultureInfo.CurrentCulture)]);
        if (!string.IsNullOrWhiteSpace(ultima.Observaciones))
            partes.Add(ultima.Observaciones);
        return string.Join(" · ", partes);
    }

    // La explicación del mockup describe el nivel Supervisada; con Gestionada se conserva la del panel.
    private string ExplicacionDeSupervision =>
        _detalle?.NivelServicio == NivelServicioSubcontrata.Supervisada ? Textos["ExplicacionSupervisada360"] : Textos["ExplicacionSupervision"];

    private string TextoUltimaVerificacion =>
        _supervision?.Centros.SelectMany(c => c.Tipos).Select(t => t.UltimaVerificacion?.FechaVerificacion).Max() is { } fecha
            ? Textos["UltimaVerificacionRegistrada", fecha.ToString("dd/MM/yyyy", CultureInfo.CurrentCulture)]
            : Textos["SinVerificacionesRegistradas"];

    private Task RegistrarVerificacionAsync() =>
        _drawerVerificacion?.AbrirAsync() ?? Task.CompletedTask;

    /// <summary>«Verificar» de una fila: el formulario se abre con su Centro y su tipo ya elegidos.</summary>
    private Task VerificarAsync(Guid centroId, Guid tipoDocumentoId) =>
        _drawerVerificacion?.AbrirAsync(centroId, tipoDocumentoId) ?? Task.CompletedTask;

    private void AbrirEliminarVerificacion(Guid verificacionId)
    {
        _verificacionAEliminarId = verificacionId;
        _confirmarEliminarVerificacionVisible = true;
    }

    private async Task ConfirmarEliminarVerificacionAsync()
    {
        if (_eliminandoVerificacion) return;

        var generacion = _generacion;
        _eliminandoVerificacion = true;

        try
        {
            var resultado = await Mediator.Send(new EliminarVerificacionExternaSubcontrataCommand(_verificacionAEliminarId));

            if (resultado.EsFallido)
            {
                ToastService.MostrarError(resultado.Error);
                return;
            }

            ToastService.Mostrar(Textos["ToastVerificacionEliminada"], TonoToast.Exito);
            if (generacion != _generacion) return;
            _confirmarEliminarVerificacionVisible = false;
            await CargarSupervisionAsync();
        }
        catch (Exception)
        {
            ToastService.Mostrar(Textos["ToastErrorEliminarVerificacion"], TonoToast.Error);
        }
        finally
        {
            if (generacion == _generacion)
                _eliminandoVerificacion = false;
        }
    }

    // ---------- Centros ----------

    private Task AbrirPanelCentro(CentroConActividadDto centro) =>
        WorkspaceService.AbrirAsync(EntidadWorkspace.Centro, centro.Id, centro.Nombre, "informacion");

    private void IrACentro360(Guid centroId) => NavigationManager.NavigateTo($"/centros/{centroId}");

    // ---------- Cabecera, menú y lateral ----------

    private string TextoAnillo =>
        _cumplimiento is { Porcentaje: { } porcentaje } fraccion
            ? Textos["AnilloEtiqueta", porcentaje, fraccion.AlDia, fraccion.Requeridos]
            : Textos["AnilloSinRequisitos"];

    private string? LeyendaAnillo =>
        _cumplimiento is { Porcentaje: not null } fraccion ? Textos["AnilloLeyenda", fraccion.AlDia, fraccion.Requeridos] : Textos["AnilloLeyendaSinRequisitos"];

    private string Plural(int n, string claveUno, string claveVarios) =>
        n == 1 ? Textos[claveUno] : Textos[claveVarios, n];

    private string DescripcionNivel(NivelServicioSubcontrata nivel) =>
        nivel == NivelServicioSubcontrata.Gestionada
            ? Textos["DescripcionNivelGestionada360"]
            : Textos["DescripcionNivelSupervisada360"];

    private string TextoRelaciones(IReadOnlyList<Guid> ids, IReadOnlyDictionary<Guid, string> nombresPorId)
    {
        if (ids.Count == 0) return "—";
        if (_errorNombres) return ids.Count == 1 ? Textos["RelacionesErrorNombresUno"] : Textos["RelacionesErrorNombresVarios", ids.Count];
        if (!_nombresCargados) return ids.Count.ToString(CultureInfo.CurrentCulture);

        var nombres = ids.Where(nombresPorId.ContainsKey)
            .Select(id => nombresPorId[id])
            .OrderBy(n => n, StringComparer.CurrentCulture)
            .ToList();
        var sinNombre = ids.Count - nombres.Count;

        return (nombres.Count, sinNombre) switch
        {
            (0, 1) => Textos["RelacionesSinNombreUno"],
            (0, var n) => Textos["RelacionesSinNombreVarios", n],
            (_, 0) => string.Join(" · ", nombres),
            (_, var n) => Textos["ListaYMas", string.Join(" · ", nombres), n]
        };
    }

    private void IrABreadcrumb(int indice)
    {
        if (indice == 0)
            NavigationManager.NavigateTo("/subcontratas");
    }

    private void CambiarPestana(string pestana)
    {
        _pestana = pestana;
        _pestanaDeLaUrl = pestana == PestanaTrabajadores ? null : pestana;
        NavigationManager.ActualizarFiltroEnUrl("pestana", _pestanaDeLaUrl);
    }


    /// <summary>La edición de la identidad y de las credenciales vive en el panel, pestaña Información.</summary>
    private Task AbrirPanelInformacion() =>
        WorkspaceService.AbrirAsync(EntidadWorkspace.Subcontrata, SubcontrataId, _detalle?.RazonSocial ?? string.Empty, "informacion");

    private IReadOnlyList<PestanaDefinicion> Pestanas =>
    [
        new(PestanaTrabajadores, Textos["PestanaTrabajadores"])
        {
            Contador = _trabajadores is { } trabajadores
                ? new ContadorPestana(trabajadores.Count, Textos["GlosaTrabajadores"], EnAlerta: TrabajadoresConProblema.Count > 0)
                : null
        },
        new(PestanaSupervision, Textos["PestanaSupervision"])
        {
            Contador = _supervision is not null && TiposConProblema > 0
                ? new ContadorPestana(TiposConProblema, Textos["GlosaSupervision"], EnAlerta: true)
                : null
        },
        new(PestanaCentros, Textos["PestanaCentros"])
        {
            Contador = _centros is { } centros ? new ContadorPestana(centros.Count, Textos["GlosaCentros"]) : null
        },
        new(PestanaAgenda, Textos["PestanaAgenda"]),
        new(PestanaHistorial, Textos["PestanaHistorial"])
    ];

    /// <summary>
    /// Sin diálogo de confirmación, como en el panel: el cambio de nivel es reversible en ambos sentidos y conserva
    /// todo el historial (ADR-005 § 2.1). La guarda de <c>_cambiandoNivel</c> impide que un doble clic mande la orden
    /// dos veces.
    /// </summary>
    private async Task CambiarNivelAsync()
    {
        if (_detalle is null || _cambiandoNivel) return;

        var generacion = _generacion;
        var detalle = _detalle;
        var nivelDestino = NivelOpuesto;
        _cambiandoNivel = true;

        try
        {
            var resultado = await Mediator.Send(new CambiarNivelServicioSubcontrataCommand(detalle.Id, nivelDestino, detalle.Version));

            if (resultado.EsFallido)
            {
                ToastService.MostrarError(resultado.Error);
                return;
            }

            ToastService.Mostrar(Textos["ToastNivelCambiado", EstadoSupervisionUi.TextoNivel(Textos, nivelDestino)], TonoToast.Exito);
            if (generacion != _generacion) return;
            await RecargarCabeceraAsync();
        }
        catch (Exception)
        {
            ToastService.Mostrar(Textos["ToastErrorCambiarNivel"], TonoToast.Error);
        }
        finally
        {
            if (generacion == _generacion)
                _cambiandoNivel = false;
        }
    }

    /// <summary>
    /// Mismo flujo que «Eliminar» en el listado: si el servidor lo rechaza (por ejemplo, porque aún tiene
    /// trabajadores) se enseña su motivo; si va bien, se retira su panel si estaba abierto y se vuelve al listado,
    /// porque la ficha de una subcontrata eliminada ya no existe.
    /// </summary>
    private async Task ConfirmarEliminarAsync()
    {
        if (_detalle is null || _eliminando) return;

        var detalle = _detalle;
        _eliminando = true;

        try
        {
            var resultado = await Mediator.Send(new EliminarSubcontrataCommand(detalle.Id));

            if (resultado.EsFallido)
            {
                ToastService.MostrarError(resultado.Error);
                return;
            }

            ToastService.Mostrar(Textos["ToastEliminada"], TonoToast.Exito);
            WorkspaceService.RetirarSiEstaAbierto(EntidadWorkspace.Subcontrata, [detalle.Id]);
            _confirmarEliminarVisible = false;
            NavigationManager.NavigateTo("/subcontratas");
        }
        catch (Exception)
        {
            ToastService.Mostrar(Textos["ToastErrorEliminar"], TonoToast.Error);
        }
        finally
        {
            _eliminando = false;
        }
    }

    // ---------- Nota interna ----------

    private bool _editorNotaVisible;
    private bool _guardandoNota;
    private string _notaEnEdicion = string.Empty;
    private string? _mensajeErrorNota;
    private string? _errorCampoNota;
    private readonly InstantaneaFormulario _instantaneaNota = new();

    /// <summary>Lo lee DrawerFormulario, que lleva dentro el guardián de cerrar y de navegar; cerrado no hay nada que perder.</summary>
    private bool HayCambiosEnLaNota => _editorNotaVisible && _instantaneaNota.Difiere(_notaEnEdicion);

    private void AbrirEditorNota()
    {
        if (_detalle is null) return;

        _notaEnEdicion = _detalle.Notas ?? string.Empty;
        _mensajeErrorNota = null;
        _errorCampoNota = null;
        _instantaneaNota.Fijar(_notaEnEdicion);
        _editorNotaVisible = true;
    }

    private void CambiarVisibilidadEditorNota(bool visible) => _editorNotaVisible = visible;

    /// <summary>
    /// Manda solo la nota y la <c>Version</c> que la ficha leyó: si otra persona guardó la subcontrata entre medias, el
    /// servidor responde el conflicto y el editor lo enseña sin cerrarse, con lo escrito intacto. Tras guardar se relee
    /// la cabecera, porque la <c>Version</c> cambió y la siguiente orden de esta ficha tiene que llevar la nueva.
    /// También se relee tras un rechazo: si fue un conflicto, la ficha tiene una <c>Version</c> vieja y sin releer el
    /// siguiente intento chocaría otra vez, y la tarjeta seguiría enseñando una nota que ya no es la guardada.
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
            var resultado = await Mediator.Send(new GuardarNotaInternaSubcontrataCommand(detalle.Id, _notaEnEdicion, detalle.Version));

            if (resultado.EsFallido)
            {
                if (generacion != _generacion) return;
                _mensajeErrorNota = resultado.Error.Mensaje;
                await RecargarCabeceraAsync();
                return;
            }

            // Como en CambiarNivelAsync: lo guardado se confirma aunque la ficha ya enseñe otra subcontrata.
            ToastService.Mostrar(Textos["ToastNotaInternaGuardada"], TonoToast.Exito);
            if (generacion != _generacion) return;
            _editorNotaVisible = false;

            // La tarjeta enseña ya lo guardado aunque la relectura de abajo falle; la Version buena la trae la relectura.
            _detalle = detalle with { Notas = string.IsNullOrWhiteSpace(_notaEnEdicion) ? null : _notaEnEdicion.Trim() };
            await RecargarCabeceraAsync();
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

    // ---------- Acceso al portal ----------

    private Task MostrarCredencialAsync() => CargarCredencialAsync(conContrasena: false);

    /// <summary>
    /// Pide la credencial. La contraseña solo se guarda en el circuito cuando se pide para revelarla
    /// (<paramref name="conContrasena"/>): «Ver credenciales» se queda con URL, usuario y notas, y con el dato de si
    /// hay contraseña, que es lo único que la tarjeta necesita para ofrecer «Revelar» y «Copiar».
    /// </summary>
    /// <returns>true si la credencial se cargó y sigue siendo la de esta carga.</returns>
    private async Task<bool> CargarCredencialAsync(bool conContrasena)
    {
        if (_detalle is null || _cargandoCredencial) return false;

        var carga = ++_cargaCredencial;
        var id = _detalle.Id;
        _cargandoCredencial = true;
        _errorCredencial = false;

        try
        {
            var credencial = await Mediator.Send(new ObtenerCredencialAccesoSubcontrataQuery(id), _cancelacion);
            if (carga != _cargaCredencial) return false;
            _tieneContrasena = !string.IsNullOrEmpty(credencial?.Contrasena);
            _credencial = conContrasena ? credencial : credencial is null ? null : credencial with { Contrasena = null };
            _credencialConsultada = true;
            _contrasenaVisible = false;
            return true;
        }
        catch (SegundoFactorRequeridoParaCredencialesException)
        {
            if (carga == _cargaCredencial)
            {
                // Sin doble factor la credencial ya cargada tampoco se queda en el circuito, y la tarjeta pasa a
                // avisar: el dato que tenía (o no tenía) quedó desmentido por el servidor.
                OcultarCredencial();
                _dobleFactorActivo = false;
            }
            return false;
        }
        catch (Exception)
        {
            if (carga == _cargaCredencial)
                _errorCredencial = true;
            return false;
        }
        finally
        {
            if (carga == _cargaCredencial)
                _cargandoCredencial = false;
        }
    }

    /// <summary>
    /// Revelar vuelve a pedir la credencial en vez de enseñar la ya cargada: el doble factor se comprueba en el
    /// momento de revelar, así que si se restableció después de abrirla, la contraseña no sale del circuito.
    /// </summary>
    private async Task AlternarContrasenaAsync()
    {
        if (_contrasenaVisible)
        {
            // Ocultar la suelta del circuito, no solo de la vista.
            _contrasenaVisible = false;
            _credencial = _credencial is null ? null : _credencial with { Contrasena = null };
            return;
        }

        var habiaCredencial = _credencial is not null;
        if (await CargarCredencialAsync(conContrasena: true))
            _contrasenaVisible = true;
        else if (_errorCredencial && habiaCredencial)
            // Con la credencial a la vista la tarjeta no tiene dónde pintar el error: sin aviso, «Revelar» no haría nada.
            ToastService.Mostrar(Textos["ErrorCredenciales"], TonoToast.Error);
    }

    /// <summary>Suelta la credencial de la memoria del circuito, no solo de la vista.</summary>
    private void OcultarCredencial()
    {
        _cargaCredencial++;
        _credencial = null;
        _tieneContrasena = false;
        _credencialConsultada = false;
        _cargandoCredencial = false;
        _errorCredencial = false;
        _contrasenaVisible = false;
    }

    /// <summary>La contraseña se pide al servidor en el instante de copiar, nunca se copia la que esté en pantalla.</summary>
    private async Task<string?> ObtenerContrasenaParaCopiarAsync()
    {
        if (_detalle is null) return null;

        var generacion = _generacion;
        CredencialAccesoSubcontrataDto? credencial;
        try
        {
            credencial = await Mediator.Send(new ObtenerCredencialAccesoSubcontrataQuery(_detalle.Id), _cancelacion);
        }
        catch (SegundoFactorRequeridoParaCredencialesException)
        {
            if (generacion != _generacion) return null;
            // El clic llega por BotonCopiar (componente hijo): sin StateHasChanged la tarjeta no se repinta.
            OcultarCredencial();
            _dobleFactorActivo = false;
            StateHasChanged();
            return null;
        }

        if (generacion != _generacion) return null;
        if (string.IsNullOrEmpty(credencial?.Contrasena))
        {
            ToastService.Mostrar(Textos["ToastSinContrasenaGuardada"], TonoToast.Info);
            return null;
        }

        return credencial.Contrasena;
    }

    private string RutaActivarDobleFactor =>
        SegundoFactorParaCredenciales.RutaConfigurarVolviendoA(SegundoFactorParaCredenciales.RutaActual(NavigationManager));

    public void Dispose()
    {
        InvalidarCargas();
        _ciclo.Cancel();
        _ciclo.Dispose();
    }
}
