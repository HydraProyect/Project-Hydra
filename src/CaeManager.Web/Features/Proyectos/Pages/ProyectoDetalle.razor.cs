using CaeManager.Application.Proyectos.Commands.ActualizarProyecto;
using CaeManager.Application.Proyectos.Commands.AsignarTecnicoProyecto;
using CaeManager.Application.Proyectos.Commands.CerrarProyecto;
using CaeManager.Application.Proyectos.Commands.DesasignarTecnicoProyecto;
using CaeManager.Application.Proyectos.Commands.EliminarProyecto;
using CaeManager.Application.Proyectos.Commands.ReabrirProyecto;
using CaeManager.Application.Proyectos.Commands.RestaurarProyecto;
using CaeManager.Application.Proyectos.Queries.ObtenerProyectoPorId;
using CaeManager.Application.Proyectos.Queries.ObtenerTecnicosProyecto;
using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadoresParaSelector;
using CaeManager.Domain.Common;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Components.Workspace;
using FluentValidation;
using MediatR;
using Microsoft.AspNetCore.Components;

namespace CaeManager.Web.Features.Proyectos.Pages;

/// <summary>
/// Proyecto 360, primer incremento («con lo que ya viaja»): la página compone las MISMAS
/// consultas y los mismos comandos que el panel de detalle del listado de Proyectos
/// (<see cref="Proyectos"/>), sin ninguna consulta nueva. No pinta anillo, banda de
/// incidencias ni las pestañas Empresas, Vehículos, Visitas e Historial: sus datos todavía
/// no existen.
///
/// <para>
/// Lo que el panel enseñaba como campo y aquí cambia de sitio: el estado va en la pastilla
/// de la cabecera, «Técnicos activos» y «Documentos gestionados» en el contador de su
/// pestaña y «Días abiertos» en la tarjeta Plazo.
/// </para>
///
/// <para>
/// Editar y asignar técnico son formularios laterales (<c>DrawerFormulario</c>), que traen
/// su propio aviso de cambios sin guardar; mientras uno está abierto no se puede pulsar
/// cerrar, reabrir ni eliminar, así que esas acciones no necesitan preguntar por lo que
/// haya a medias, como hacía el panel.
/// </para>
/// </summary>
public partial class ProyectoDetalle : CaeManager.Web.Components.PaginaInteractiva, IDisposable
{
    internal const string PestanaTecnicos = "tecnicos";
    internal const string PestanaDocumentos = "documentos";

    [Parameter] public Guid ProyectoId { get; set; }

    /// <summary>Pestaña activa, en la URL (sin parámetro = Técnicos), como en Empresa 360.</summary>
    [Parameter, SupplyParameterFromQuery(Name = "pestana")] public string? Pestana { get; set; }

    [Inject] private IMediator Mediator { get; set; } = default!;
    [Inject] private ContextWorkspaceService WorkspaceService { get; set; } = default!;
    [Inject] private NavigationManager NavigationManager { get; set; } = default!;
    [Inject] private ToastService ToastService { get; set; } = default!;

    private ProyectoDetalleDto? _detalle;
    private bool _cargando = true;
    private bool _error;

    private List<TecnicoProyectoDto> _tecnicos = [];
    private bool _cargandoTecnicos = true;
    private bool _errorTecnicos;

    /// <summary>
    /// Proyecto que esta instancia tiene cargado: Blazor reutiliza la instancia al pasar de
    /// un proyecto a otro, y sin esto cada cambio de pestaña en la URL relanzaría la carga.
    /// </summary>
    private Guid? _proyectoCargado;

    /// <summary>
    /// Un contador por carga (patrón de Empresa 360): cada respuesta compara el suyo antes
    /// de escribir y, si ya no es el vigente, se descarta. Es el equivalente en una página
    /// con ruta propia de la guarda <c>_versionDetalle</c> del panel: la respuesta tardía de
    /// otro proyecto no se pinta en este.
    /// </summary>
    private int _cargaDetalle;
    private int _cargaTecnicos;

    private readonly CancellationTokenSource _ciclo = new();
    private CancellationToken _cancelacion;
    private bool _desechado;

    private static DateOnly Hoy => DiaDeNegocio.Hoy();

    private string PestanaActiva => Pestana == PestanaDocumentos ? PestanaDocumentos : PestanaTecnicos;

    private IReadOnlyList<BreadcrumbElemento> Miguero =>
        [new BreadcrumbElemento(Textos["MigaProyectos"]), new BreadcrumbElemento(_detalle?.Nombre ?? "…")];

    private IReadOnlyList<PestanaDefinicion> Pestanas =>
    [
        new(PestanaTecnicos, Textos["PestanaTecnicos"])
        {
            Contador = _detalle is null ? null : new ContadorPestana(TecnicosActivosAhora, Textos["GlosaTecnicosActivos"])
        },
        new(PestanaDocumentos, Textos["PestanaDocumentos"])
        {
            Contador = _detalle is null ? null : new ContadorPestana(_detalle.DocumentosGestionados, Textos["GlosaDocumentosGestionados"])
        }
    ];

    /// <summary>
    /// Con la lista de técnicos ya cargada se cuenta sobre ella, para que el contador de la
    /// pestaña siga a un alta o a una baja sin volver a pedir el detalle.
    /// </summary>
    private int TecnicosActivosAhora =>
        _cargandoTecnicos || _errorTecnicos ? _detalle?.TecnicosActivos ?? 0 : _tecnicos.Count(t => t.EstaActivo);

    /// <summary>
    /// Con el proyecto cerrado las listas quedan en solo lectura, como dibuja el mockup: la
    /// acción principal pasa a ser «Reabrir» y no se asignan ni se dan de baja técnicos.
    /// </summary>
    private bool ListasEditables => _detalle is { EstaAbierto: true };

    private IEnumerable<TecnicoProyectoDto> TecnicosActivos => _tecnicos.Where(t => t.EstaActivo);
    private IReadOnlyList<TecnicoProyectoDto> TecnicosDeBaja => _tecnicos.Where(t => !t.EstaActivo).ToList();

    protected override void OnInitialized() => _cancelacion = _ciclo.Token;

    /// <summary>Solo recarga cuando cambia el proyecto: la pestaña también llega por aquí (viaja en la URL).</summary>
    protected override async Task OnParametersSetAsync()
    {
        if (_proyectoCargado == ProyectoId)
            return;

        _proyectoCargado = ProyectoId;
        ReiniciarParaNuevoProyecto();
        await CargarAsync();
    }

    /// <summary>Nada del proyecto anterior sobrevive al cambio, tampoco un formulario abierto.</summary>
    private void ReiniciarParaNuevoProyecto()
    {
        _cargaDetalle++;
        _cargaTecnicos++;
        _detalle = null;
        _error = false;
        _tecnicos = [];
        _cargandoTecnicos = true;
        _errorTecnicos = false;
        _editarVisible = false;
        _asignarVisible = false;
        _cerrarVisible = false;
        _confirmarReabrirVisible = false;
        _confirmarEliminarVisible = false;
        _tecnicoADarDeBaja = null;
    }

    private async Task CargarAsync()
    {
        var carga = ++_cargaDetalle;
        var proyectoId = ProyectoId;
        _cargando = true;
        _error = false;

        try
        {
            // Inexistente o fuera de alcance, ObtenerProyectoPorIdQuery devuelve null,
            // y la página no distingue un caso del otro, igual que el panel.
            var detalle = await Mediator.Send(new ObtenerProyectoPorIdQuery(proyectoId), _cancelacion);
            if (carga != _cargaDetalle) return;
            _detalle = detalle;
            _error = detalle is null;
        }
        catch (OperationCanceledException) when (_cancelacion.IsCancellationRequested)
        {
            return;
        }
        catch (Exception)
        {
            if (carga == _cargaDetalle)
            {
                _detalle = null;
                _error = true;
            }
        }
        finally
        {
            if (carga == _cargaDetalle)
                _cargando = false;
        }

        if (carga == _cargaDetalle && _detalle is not null)
            await CargarTecnicosAsync();
    }

    private async Task CargarTecnicosAsync()
    {
        var carga = ++_cargaTecnicos;
        var proyectoId = ProyectoId;
        _cargandoTecnicos = true;
        _errorTecnicos = false;

        try
        {
            var tecnicos = await Mediator.Send(new ObtenerTecnicosProyectoQuery(proyectoId), _cancelacion);
            if (carga != _cargaTecnicos) return;
            _tecnicos = tecnicos.ToList();
        }
        catch (OperationCanceledException) when (_cancelacion.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            if (carga == _cargaTecnicos)
                _errorTecnicos = true;
        }
        finally
        {
            if (carga == _cargaTecnicos)
                _cargandoTecnicos = false;
        }
    }

    private void IrABreadcrumb(int indice)
    {
        if (indice == 0)
            NavigationManager.NavigateTo("/proyectos");
    }

    private void CambiarPestana(string pestana) =>
        NavigationManager.NavigateTo(
            NavigationManager.GetUriWithQueryParameter("pestana", pestana == PestanaTecnicos ? null : pestana),
            replace: true);

    private Task AbrirPanelTrabajador(TecnicoProyectoDto tecnico) =>
        WorkspaceService.AbrirAsync(EntidadWorkspace.Trabajador, tecnico.TrabajadorId, tecnico.TrabajadorNombreCompleto, "informacion");

    private void IrAFichaTrabajador(TecnicoProyectoDto tecnico) =>
        NavigationManager.NavigateTo($"/trabajadores/{tecnico.TrabajadorId}");

    private string MetaTecnico(TecnicoProyectoDto tecnico)
    {
        var partes = new List<string> { Textos["MetaAltaFecha", tecnico.FechaAlta] };
        if (tecnico.FechaBaja is { } baja)
            partes.Add(Textos["MetaBajaFecha", baja]);
        return string.Join(" · ", partes);
    }

    private string Plural(int cantidad, string claveUno, string claveVarios) =>
        Textos[cantidad == 1 ? claveUno : claveVarios, cantidad];

    // ---- Plazo ----

    private int? PorcentajeDelPlazo => _detalle is null
        ? null
        : PlazoProyecto.PorcentajeDelPlazo(_detalle.FechaInicio, _detalle.FechaFinPrevista, _detalle.FechaCierreReal, Hoy);

    private string PlazoTitulo => _detalle is null
        ? string.Empty
        : PlazoProyecto.DiasAbiertos(_detalle.FechaInicio, _detalle.FechaCierreReal, Hoy) is { } dias
            ? Plural(dias, "PlazoDiasAbiertoUno", "PlazoDiasAbiertoVarios")
            : Textos["PlazoSinEmpezar"];

    private string PlazoDetalle
    {
        get
        {
            if (_detalle is null)
                return string.Empty;

            var fin = _detalle.FechaFinPrevista;

            if (_detalle.FechaCierreReal is { } cierre)
            {
                var cerrado = Textos["PlazoCerradoEl", cierre].Value;
                if (fin is not { } previsto || previsto == cierre)
                    return cerrado;

                var margen = previsto.DayNumber - cierre.DayNumber;
                return margen > 0
                    ? $"{cerrado} · {Plural(margen, "PlazoAntesDelFinUno", "PlazoAntesDelFinVarios")}"
                    : $"{cerrado} · {Plural(-margen, "PlazoDespuesDelFinUno", "PlazoDespuesDelFinVarios")}";
            }

            if (fin is not { } finPrevisto || PorcentajeDelPlazo is not { } porcentaje)
                return fin is null ? Textos["PlazoSinFinPrevisto"] : string.Empty;

            var quedan = finPrevisto.DayNumber - Hoy.DayNumber;
            return quedan >= 0
                ? $"{Textos["PlazoPorcentaje", porcentaje]} · {Plural(quedan, "PlazoQuedanUno", "PlazoQuedanVarios")}"
                : $"{Textos["PlazoPorcentaje", porcentaje]} · {Plural(-quedan, "PlazoSuperadoUno", "PlazoSuperadoVarios")}";
        }
    }

    // ---- Editar proyecto ----

    private bool _editarVisible;
    private bool _guardandoEdicion;
    private string _editNombre = string.Empty;
    private string _editFechaFinPrevista = string.Empty;
    private string _editNotas = string.Empty;
    private Dictionary<string, string> _editErrores = new();
    private string? _editError;
    private readonly InstantaneaFormulario _instantaneaEdicion = new();

    private bool HayCambiosEnEdicion =>
        _editarVisible && _instantaneaEdicion.Difiere(_editNombre, _editFechaFinPrevista, _editNotas);

    private void AbrirEditar()
    {
        if (_detalle is null) return;

        _editNombre = _detalle.Nombre;
        _editFechaFinPrevista = _detalle.FechaFinPrevista?.ToString("yyyy-MM-dd") ?? string.Empty;
        _editNotas = _detalle.Notas ?? string.Empty;
        _editErrores = new();
        _editError = null;
        _instantaneaEdicion.Fijar(_editNombre, _editFechaFinPrevista, _editNotas);
        _editarVisible = true;
    }

    private void CambiarVisibilidadEditar(bool visible)
    {
        if (!visible && !_guardandoEdicion)
            _editarVisible = false;
    }

    private async Task GuardarEdicionAsync()
    {
        if (_detalle is null) return;

        var id = _detalle.Id;
        _editErrores = new();
        _editError = null;
        _guardandoEdicion = true;

        try
        {
            // Fecha vacía o no válida se guarda como «sin fin previsto»; notas en blanco, como vacías.
            DateOnly? fechaFinPrevista = DateOnly.TryParse(_editFechaFinPrevista, out var fv) ? fv : null;
            var notas = string.IsNullOrWhiteSpace(_editNotas) ? null : _editNotas;

            var resultado = await Mediator.Send(
                new ActualizarProyectoCommand(id, _editNombre, fechaFinPrevista, notas, _detalle.Version));

            if (resultado.EsFallido)
            {
                _editError = resultado.Error.Mensaje;
                return;
            }

            ToastService.Mostrar(Textos["ToastActualizado"], TonoToast.Exito);
            _editarVisible = false;

            if (ProyectoId == id)
                await CargarAsync();
        }
        catch (ValidationException ex)
        {
            _editErrores = ex.Errors
                .GroupBy(e => e.PropertyName)
                .ToDictionary(g => g.Key, g => g.First().ErrorMessage);
        }
        catch (Exception)
        {
            _editError = Textos["ErrorGuardar"];
        }
        finally
        {
            _guardandoEdicion = false;
        }
    }

    // ---- Cerrar proyecto ----

    private bool _cerrarVisible;
    private bool _cerrando;
    private string _fechaCierre = string.Empty;
    private string? _errorCierre;
    private readonly InstantaneaFormulario _instantaneaCierre = new();

    private bool HayCambiosEnElModalDeCierre => _cerrarVisible && _instantaneaCierre.Difiere(_fechaCierre);

    private void AbrirCerrar()
    {
        _fechaCierre = Hoy.ToString("yyyy-MM-dd");
        _errorCierre = null;
        _instantaneaCierre.Fijar(_fechaCierre);
        _cerrarVisible = true;
    }

    private async Task ConfirmarCerrarAsync()
    {
        if (_detalle is null) return;

        if (!DateOnly.TryParse(_fechaCierre, out var fechaCierre))
        {
            _errorCierre = Textos["ErrorFechaCierre"];
            return;
        }

        var id = _detalle.Id;
        _cerrando = true;
        _errorCierre = null;

        try
        {
            // Sin Version, como el panel: no cambiarlo sin decidirlo (lista de aceptación, «Cerrar proyecto»).
            var resultado = await Mediator.Send(new CerrarProyectoCommand(id, fechaCierre));

            if (resultado.EsFallido)
            {
                _errorCierre = resultado.Error.Mensaje;
                return;
            }

            ToastService.Mostrar(Textos["ToastCerrado"], TonoToast.Exito);
            _cerrarVisible = false;

            if (ProyectoId == id)
                await CargarAsync();
        }
        finally
        {
            _cerrando = false;
        }
    }

    // ---- Reabrir proyecto ----

    private bool _confirmarReabrirVisible;
    private bool _reabriendo;

    private string MensajeConfirmarReabrir => _detalle is null
        ? string.Empty
        : Textos["ConfirmarReabrirMensaje", _detalle.Nombre, _detalle.FechaCierreReal?.ToString("dd/MM/yyyy") ?? string.Empty];

    private async Task ConfirmarReabrirAsync()
    {
        if (_reabriendo || _detalle is null) return;
        _reabriendo = true;
        var id = _detalle.Id;

        try
        {
            var resultado = await Mediator.Send(new ReabrirProyectoCommand(id));

            if (resultado.EsFallido)
            {
                ToastService.MostrarError(resultado.Error);
                return;
            }

            ToastService.Mostrar(Textos["ToastReabierto"], TonoToast.Exito);

            // Si mientras tanto se navegó a otro proyecto, el resultado es del anterior:
            // no se toca ningún diálogo ni lista del que se está viendo ahora.
            if (ProyectoId != id) return;

            _confirmarReabrirVisible = false;
            await CargarAsync();
        }
        finally
        {
            _reabriendo = false;
        }
    }

    // ---- Eliminar proyecto ----

    private bool _restaurando;

    /// <summary>
    /// «Deshacer» del aviso tras eliminar — ver RestaurarProyectoCommand. Se pulsa ya desde el
    /// listado (esta página se dejó al eliminar): si restaura, vuelve a la página del proyecto.
    /// </summary>
    private async Task DeshacerEliminarAsync(Guid id)
    {
        if (_restaurando) return;
        _restaurando = true;

        try
        {
            var resultado = await Mediator.Send(new RestaurarProyectoCommand(id));

            ToastService.Mostrar(
                resultado.EsExitoso ? Textos["ToastRestaurado"].Value : resultado.Error.Mensaje,
                resultado.EsExitoso ? TonoToast.Exito : TonoToast.Error);

            if (resultado.EsExitoso)
                NavigationManager.NavigateTo($"/proyectos/{id}");
        }
        catch (Exception)
        {
            ToastService.Mostrar(Textos["ErrorRestaurar"], TonoToast.Error);
        }
        finally
        {
            _restaurando = false;
        }
    }

    private bool _confirmarEliminarVisible;
    private bool _eliminando;

    private async Task ConfirmarEliminarAsync()
    {
        if (_eliminando || _detalle is null) return;
        _eliminando = true;
        var id = _detalle.Id;

        try
        {
            var resultado = await Mediator.Send(new EliminarProyectoCommand(id));

            if (resultado.EsFallido)
            {
                ToastService.MostrarError(resultado.Error);
                return;
            }

            // El proyecto eliminado ya no tiene página: se vuelve al listado, que es donde deja de aparecer.
            // Con «Deshacer», como el listado: el aviso sobrevive a la vuelta al listado.
            ToastService.Mostrar(Textos["ToastEliminado"], TonoToast.Exito, Textos["ToastAccionDeshacer"], () => DeshacerEliminarAsync(id));

            // Si mientras tanto se navegó a otro proyecto, el resultado es del anterior:
            // no se toca ningún diálogo ni lista del que se está viendo ahora.
            if (ProyectoId != id) return;

            _confirmarEliminarVisible = false;
            NavigationManager.NavigateTo("/proyectos");
        }
        catch (Exception)
        {
            ToastService.Mostrar(Textos["ErrorEliminar"], TonoToast.Error);
        }
        finally
        {
            _eliminando = false;
        }
    }

    // ---- Técnicos ----

    private bool _asignarVisible;
    private bool _asignando;
    private bool _cargandoTrabajadores;
    private IReadOnlyList<TrabajadorSelectorDto> _trabajadoresDisponibles = [];
    private string _nuevoTecnicoTrabajadorId = string.Empty;
    private string _nuevoTecnicoFechaAlta = string.Empty;
    private string? _errorTecnico;
    private readonly InstantaneaFormulario _instantaneaTecnico = new();

    /// <summary>Comparado con cómo se abrió: la fecha de hoy que trae puesta no es un cambio.</summary>
    private bool HayCambiosEnAsignar =>
        _asignarVisible && _instantaneaTecnico.Difiere(_nuevoTecnicoTrabajadorId, _nuevoTecnicoFechaAlta);

    /// <summary>La acción primaria de la cabecera: asignar con el proyecto abierto, reabrir con él cerrado.</summary>
    private Task AccionPrincipalAsync()
    {
        if (_detalle is { EstaAbierto: true })
            return AbrirAsignarAsync();

        _confirmarReabrirVisible = true;
        return Task.CompletedTask;
    }

    private async Task AbrirAsignarAsync()
    {
        _nuevoTecnicoTrabajadorId = string.Empty;
        _nuevoTecnicoFechaAlta = Hoy.ToString("yyyy-MM-dd");
        _errorTecnico = null;
        _instantaneaTecnico.Fijar(_nuevoTecnicoTrabajadorId, _nuevoTecnicoFechaAlta);
        _asignarVisible = true;

        if (_trabajadoresDisponibles.Count > 0)
            return;

        _cargandoTrabajadores = true;
        try
        {
            _trabajadoresDisponibles = await Mediator.Send(
                new ObtenerTrabajadoresParaSelectorQuery(AlcanceSelectorTrabajadores.Cartera), _cancelacion);
        }
        finally
        {
            _cargandoTrabajadores = false;
        }
    }

    private void CambiarVisibilidadAsignar(bool visible)
    {
        if (!visible && !_asignando)
            _asignarVisible = false;
    }

    private async Task AsignarTecnicoAsync()
    {
        if (_detalle is null) return;

        if (!Guid.TryParse(_nuevoTecnicoTrabajadorId, out var trabajadorId))
        {
            _errorTecnico = Textos["ErrorFaltaTecnico"];
            return;
        }

        if (!DateOnly.TryParse(_nuevoTecnicoFechaAlta, out var fechaAlta))
        {
            _errorTecnico = Textos["ErrorFechaAlta"];
            return;
        }

        _asignando = true;
        _errorTecnico = null;
        var id = _detalle.Id;

        try
        {
            var resultado = await Mediator.Send(new AsignarTecnicoProyectoCommand(id, trabajadorId, fechaAlta));

            if (resultado.EsFallido)
            {
                if (ProyectoId == id)
                    _errorTecnico = resultado.Error.Mensaje;
                return;
            }

            ToastService.Mostrar(Textos["ToastTecnicoAsignado"], TonoToast.Exito);

            // Si mientras tanto se navegó a otro proyecto, el resultado es del anterior:
            // no se toca ningún diálogo ni lista del que se está viendo ahora.
            if (ProyectoId != id) return;

            _asignarVisible = false;
            await CargarTecnicosAsync();
        }
        finally
        {
            _asignando = false;
        }
    }

    // Dar de baja a un técnico lo saca de la facturación por días del proyecto
    // y la aplicación no lo deshace: se confirma antes, como eliminar un proyecto.
    private TecnicoProyectoDto? _tecnicoADarDeBaja;
    private bool _dandoDeBajaTecnico;

    private string MensajeConfirmarBajaTecnico => _tecnicoADarDeBaja is null
        ? string.Empty
        : Textos["ConfirmarBajaTecnicoMensaje", _tecnicoADarDeBaja.TrabajadorNombreCompleto];

    private void CerrarConfirmacionBajaTecnico(bool visible)
    {
        if (!visible && !_dandoDeBajaTecnico)
            _tecnicoADarDeBaja = null;
    }

    private async Task ConfirmarBajaTecnicoAsync()
    {
        if (_dandoDeBajaTecnico || _tecnicoADarDeBaja is not { } tecnico)
            return;

        _dandoDeBajaTecnico = true;
        var id = ProyectoId;
        try
        {
            var resultado = await Mediator.Send(new DesasignarTecnicoProyectoCommand(tecnico.Id, Hoy));

            if (resultado.EsFallido)
            {
                ToastService.MostrarError(resultado.Error);
                return;
            }

            ToastService.Mostrar(Textos["ToastTecnicoDeBaja"], TonoToast.Exito);

            if (ProyectoId == id)
                await CargarTecnicosAsync();
        }
        catch (Exception)
        {
            ToastService.Mostrar(Textos["ErrorDarDeBaja"], TonoToast.Error);
        }
        finally
        {
            _dandoDeBajaTecnico = false;

            // Solo se cierra la confirmación de ESTA baja: si se navegó a otro proyecto y
            // allí se abrió otra, esa no es la que acaba de resolverse.
            if (ReferenceEquals(_tecnicoADarDeBaja, tecnico))
                _tecnicoADarDeBaja = null;
        }
    }

    public void Dispose()
    {
        if (_desechado)
            return;
        _desechado = true;
        _ciclo.Cancel();
        _ciclo.Dispose();
    }
}
