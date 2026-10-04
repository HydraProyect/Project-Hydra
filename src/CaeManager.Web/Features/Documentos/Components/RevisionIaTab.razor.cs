using CaeManager.Application.Documentos.Commands.AceptarDeteccionesIaEnBloque;
using CaeManager.Application.Documentos.Commands.AplicarDeteccionIaDocumento;
using CaeManager.Application.Documentos.Commands.CorregirRevisionIaDocumento;
using CaeManager.Application.Documentos.Commands.ResolverRevisionIaDocumento;
using CaeManager.Application.Documentos.Queries.ObtenerRevisionesIaPendientes;
using CaeManager.Web.Components.DesignSystem;
using Microsoft.AspNetCore.Components;

namespace CaeManager.Web.Features.Documentos.Components;

public partial class RevisionIaTab : ComponentBase, IDisposable
{
    private Drawer? _drawerCorreccion;
    // Solo preselecciona: nada se acepta sin que el Gestor CAE pulse «Aceptar seleccionadas». No es el umbral del modelo.
    private const int ConfianzaMinimaPorDefecto = 95;
    private static IReadOnlyList<int> ConfianzasMinimas { get; } = [95, 90, 80];

    private sealed record FalloAceptacion(string Propietario, string Tipo, string Motivo);

    private readonly HashSet<Guid> _seleccion = [];
    private int _confianzaMinima = ConfianzaMinimaPorDefecto;
    private bool _preseleccionPendiente = true;
    private IReadOnlyList<FalloAceptacion> _fallosBloque = [];

    private IReadOnlyList<RevisionIaDocumentoDto> _revisiones = [];
    private bool _cargando = true;
    private bool _errorCarga;
    private bool _confirmandoLote;
    private bool _operacionEnCurso;
    private bool _confirmacionLoteVisible;
    private bool _confirmacionDescartarVisible;
    private bool _correccionManualVisible;
    private bool _dispose;
    private Guid? _procesandoId;
    private Guid? _revisionIdSeleccionada;
    private Guid? _documentoIdExpandido;
    private string _fechaEmisionManual = string.Empty;
    private string? _errorFechaManual;
    private FiltroRevision _filtro;
    private CancellationTokenSource? _cargaCts;
    private int _generacionCarga;
    private int _generacionEntidad;

    private enum FiltroRevision
    {
        Todas,
        Aceptables,
        Manuales,
        SinFecha
    }

    private static IReadOnlyList<FiltroRevision> Filtros { get; } =
    [
        FiltroRevision.Todas,
        FiltroRevision.Aceptables,
        FiltroRevision.Manuales,
        FiltroRevision.SinFecha
    ];

    private IReadOnlyList<RevisionIaDocumentoDto> RevisionesVisibles => _revisiones.Where(CumpleFiltro).ToList();
    // Solo lo que se ve: lo que el filtro oculta no se acepta sin que el Gestor CAE lo tenga delante.
    private IReadOnlyList<RevisionIaDocumentoDto> RevisionesSeleccionadas => RevisionesVisibles.Where(r => _seleccion.Contains(r.Id) && EsAceptableEnBloque(r)).ToList();
    private RevisionIaDocumentoDto? RevisionSeleccionada => _revisiones.FirstOrDefault(r => r.Id == _revisionIdSeleccionada);

    protected override Task OnInitializedAsync() => CargarAsync();

    private async Task CargarAsync()
    {
        _cargaCts?.Cancel();
        _cargaCts?.Dispose();
        var cts = _cargaCts = new CancellationTokenSource();
        var generacion = ++_generacionCarga;
        var token = cts.Token;
        _cargando = true;
        _errorCarga = false;
        StateHasChanged();

        try
        {
            var revisiones = await Mediator.Send(new ObtenerRevisionesIaPendientesQuery(), token);
            if (_dispose || generacion != _generacionCarga)
            {
                return;
            }

            _revisiones = revisiones;
            if (_preseleccionPendiente)
            {
                Preseleccionar();
                _preseleccionPendiente = false;
            }
            else
            {
                // Lo ya aceptado o resuelto por otra vía sale de la selección; lo que falló sigue marcado para reintentar.
                _seleccion.IntersectWith(revisiones.Select(r => r.Id));
            }

            SeleccionarPrimeraVisible();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            if (!_dispose && generacion == _generacionCarga)
            {
                _errorCarga = true;
            }
        }
        finally
        {
            if (!_dispose && generacion == _generacionCarga)
            {
                _cargando = false;
                StateHasChanged();
            }
        }
    }

    private async Task AceptarDeteccionAsync(Guid revisionId)
    {
        if (_operacionEnCurso)
        {
            return;
        }

        var generacionEntidad = _generacionEntidad;
        _operacionEnCurso = true;
        _procesandoId = revisionId;
        StateHasChanged();

        try
        {
            var resultado = await Mediator.Send(new AplicarDeteccionIaDocumentoCommand(revisionId));
            if (resultado.EsFallido)
            {
                if (!_dispose && generacionEntidad == _generacionEntidad)
                {
                    ToastService.MostrarError(resultado.Error);
                }

                return;
            }

            if (_dispose || generacionEntidad != _generacionEntidad)
            {
                return;
            }

            ToastService.Mostrar("Detección aplicada al documento.", TonoToast.Exito);
            await CargarAsync();
        }
        finally
        {
            if (!_dispose && generacionEntidad == _generacionEntidad)
            {
                _procesandoId = null;
            }

            _operacionEnCurso = false;
        }
    }

    private async Task ResolverSeleccionadaAsync()
    {
        if (_revisionIdSeleccionada is not { } revisionId || _operacionEnCurso)
        {
            return;
        }

        var generacionEntidad = _generacionEntidad;
        _operacionEnCurso = true;
        _procesandoId = revisionId;
        StateHasChanged();

        try
        {
            var resultado = await Mediator.Send(new ResolverRevisionIaDocumentoCommand(revisionId));
            if (resultado.EsFallido)
            {
                if (!_dispose && generacionEntidad == _generacionEntidad)
                {
                    ToastService.MostrarError(resultado.Error);
                }

                return;
            }

            if (_dispose || generacionEntidad != _generacionEntidad)
            {
                return;
            }

            ToastService.Mostrar("Lectura descartada; el documento no se ha modificado.", TonoToast.Exito);
            _confirmacionDescartarVisible = false;
            await CargarAsync();
        }
        finally
        {
            if (!_dispose && generacionEntidad == _generacionEntidad)
            {
                _procesandoId = null;
            }

            _operacionEnCurso = false;
        }
    }

    private async Task CorregirSeleccionadaAsync()
    {
        if (_revisionIdSeleccionada is not { } revisionId || _operacionEnCurso)
        {
            return;
        }

        if (!DateOnly.TryParse(_fechaEmisionManual, out var fechaEmision))
        {
            _errorFechaManual = "Indica una fecha de emisión válida.";
            return;
        }

        var generacionEntidad = _generacionEntidad;
        _operacionEnCurso = true;
        _procesandoId = revisionId;
        _errorFechaManual = null;
        StateHasChanged();

        try
        {
            var resultado = await Mediator.Send(new CorregirRevisionIaDocumentoCommand(revisionId, fechaEmision));
            if (resultado.EsFallido)
            {
                if (!_dispose && generacionEntidad == _generacionEntidad)
                {
                    ToastService.MostrarError(resultado.Error);
                }

                return;
            }

            if (_dispose || generacionEntidad != _generacionEntidad)
            {
                return;
            }

            ToastService.Mostrar("Documento corregido y revisión resuelta.", TonoToast.Exito);
            _correccionManualVisible = false;
            await CargarAsync();
        }
        finally
        {
            if (!_dispose && generacionEntidad == _generacionEntidad)
            {
                _procesandoId = null;
            }

            _operacionEnCurso = false;
        }
    }

    private async Task AceptarSeleccionadasAsync()
    {
        if (_confirmandoLote || _operacionEnCurso)
        {
            return;
        }

        var seleccionadas = RevisionesSeleccionadas;
        if (seleccionadas.Count == 0)
        {
            return;
        }

        var datos = seleccionadas.ToDictionary(r => r.Id);
        _confirmandoLote = true;
        _operacionEnCurso = true;
        _fallosBloque = [];
        StateHasChanged();

        try
        {
            var resultado = await Mediator.Send(new AceptarDeteccionesIaEnBloqueCommand(seleccionadas.Select(r => r.Id).ToList()));
            if (_dispose)
            {
                return;
            }

            if (resultado.EsFallido)
            {
                ToastService.MostrarError(resultado.Error);
                return;
            }

            var desenlace = resultado.Valor;
            _fallosBloque = desenlace.Resultados
                .Where(r => !r.Aceptada)
                .Select(r => new FalloAceptacion(
                    datos[r.RevisionId].PropietarioNombre, datos[r.RevisionId].TipoDocumentoNombre, r.Mensaje ?? Textos["RevIaFalloGenerico"]))
                .ToList();
            ToastService.Mostrar(
                ResumenBloque(desenlace.Aceptadas, seleccionadas.Count, desenlace.Fallidas),
                desenlace.Fallidas == 0 ? TonoToast.Exito : TonoToast.Advertencia);
            _confirmacionLoteVisible = false;
            await CargarAsync();
        }
        finally
        {
            _confirmandoLote = false;
            _operacionEnCurso = false;
        }
    }

    private void Preseleccionar()
    {
        _seleccion.Clear();
        foreach (var revision in _revisiones
            .Where(r => EsAceptableEnBloque(r) && r.ConfianzaGeneral >= _confianzaMinima)
            .Take(AceptarDeteccionesIaEnBloqueCommandHandler.MaximoRevisionesPorBloque))
        {
            _seleccion.Add(revision.Id);
        }
    }

    private void CambiarConfianzaMinima(string valor)
    {
        if (!int.TryParse(valor, out var minima) || !ConfianzasMinimas.Contains(minima) || _operacionEnCurso)
        {
            return;
        }

        _confianzaMinima = minima;
        _confirmacionLoteVisible = false;
        Preseleccionar();
    }

    private void AlternarSeleccion(RevisionIaDocumentoDto revision, bool marcada)
    {
        if (_operacionEnCurso || !EsAceptableEnBloque(revision))
        {
            return;
        }

        _confirmacionLoteVisible = false;
        if (marcada)
        {
            if (_seleccion.Count >= AceptarDeteccionesIaEnBloqueCommandHandler.MaximoRevisionesPorBloque)
            {
                return;
            }

            _seleccion.Add(revision.Id);
        }
        else
        {
            _seleccion.Remove(revision.Id);
        }
    }

    private void CerrarFallosBloque() => _fallosBloque = [];

    private void CambiarFiltro(FiltroRevision filtro)
    {
        _filtro = filtro;
        _confirmacionLoteVisible = false;
        _confirmacionDescartarVisible = false;
        _correccionManualVisible = false;
        SeleccionarPrimeraVisible();
    }

    private void QuitarFiltro() => CambiarFiltro(FiltroRevision.Todas);

    private void SeleccionarRevision(Guid revisionId)
    {
        if (_revisionIdSeleccionada == revisionId)
        {
            return;
        }

        _revisionIdSeleccionada = revisionId;
        ReiniciarOperacionPorCambioDeEntidad();
    }

    private void SeleccionarPrimeraVisible()
    {
        if (RevisionSeleccionada is not null && RevisionesVisibles.Any(r => r.Id == _revisionIdSeleccionada))
        {
            return;
        }

        _revisionIdSeleccionada = RevisionesVisibles.FirstOrDefault()?.Id;
        ReiniciarOperacionPorCambioDeEntidad();
    }

    private void ReiniciarOperacionPorCambioDeEntidad()
    {
        _generacionEntidad++;
        _documentoIdExpandido = null;
        _confirmacionDescartarVisible = false;
        _correccionManualVisible = false;
        _procesandoId = null;
    }

    private void PrepararConfirmacionLote() => _confirmacionLoteVisible = RevisionesSeleccionadas.Count > 0;
    private void PrepararDescartar() => _confirmacionDescartarVisible = RevisionSeleccionada is not null;
    private void PrepararCorreccionManual()
    {
        if (RevisionSeleccionada is not { } revision)
        {
            return;
        }

        _fechaEmisionManual = revision.FechaEmisionIntroducida?.ToString("yyyy-MM-dd") ?? string.Empty;
        _fechaEmisionManualAlAbrir = _fechaEmisionManual;
        _errorFechaManual = null;
        _correccionManualVisible = true;
    }

    // P1-E2: único punto de verdad de «hay cambios» en la corrección manual: la fecha
    // tecleada frente a la precargada al abrir.
    private string _fechaEmisionManualAlAbrir = string.Empty;

    private bool HayCambiosSinGuardar => _correccionManualVisible && _fechaEmisionManual != _fechaEmisionManualAlAbrir;

    private void CerrarCorreccionManual()
    {
        _correccionManualVisible = false;
        _errorFechaManual = null;
    }
    private void AlternarPrevisualizacion(Guid documentoId) => _documentoIdExpandido = _documentoIdExpandido == documentoId ? null : documentoId;

    private bool CumpleFiltro(RevisionIaDocumentoDto revision) => _filtro switch
    {
        FiltroRevision.Aceptables => EsAceptableEnBloque(revision),
        FiltroRevision.Manuales => revision.FechaEmisionDetectada is not null && !EsAceptableEnBloque(revision),
        FiltroRevision.SinFecha => revision.FechaEmisionDetectada is null,
        _ => true
    };

    // Aceptable en bloque = hay fecha de emisión leída y el tipo calcula el vencimiento desde ella. La confianza no
    // decide esto, solo la preselección; la vigencia que confirma el Gestor CAE a mano no se acepta en bloque.
    private static bool EsAceptableEnBloque(RevisionIaDocumentoDto revision) => revision.FechaEmisionDetectada is not null && revision.VigenciaLaFijaElTipo;

    private static string TextoFiltro(FiltroRevision f) => f switch
    {
        FiltroRevision.Aceptables => "Aceptables",
        FiltroRevision.Manuales => "Manual",
        FiltroRevision.SinFecha => "Sin fecha",
        _ => "Todas"
    };

    private string ClaseFiltro(FiltroRevision f) => _filtro == f ? "revision-ia-filtro activo" : "revision-ia-filtro";
    private string ClaseRevision(RevisionIaDocumentoDto r) => RevisionSeleccionada?.Id == r.Id ? "revision-ia-fila activa" : "revision-ia-fila";
    private static string Ambito(RevisionIaDocumentoDto r) => r.TrabajadorId is null ? "Empresa" : "Trabajador";
    private string EtiquetaTratamiento(RevisionIaDocumentoDto r) => EsAceptableEnBloque(r)
        ? Textos["RevIaAceptable"]
        : r.FechaEmisionDetectada is null ? "Sin fecha" : Textos["RevIaVigenciaAMano"];

    private string ResumenBloque(int aceptadas, int solicitadas, int fallidas) => aceptadas == solicitadas
        ? Textos["RevIaResumenTodas", aceptadas]
        : Textos["RevIaResumenParcial", aceptadas, solicitadas, fallidas];

    private static string Fecha(DateOnly? fecha) => fecha?.ToString("dd/MM/yyyy") ?? "No disponible";
    private static string Firma(bool? tieneFirma) => tieneFirma switch
    {
        true => "Detectada",
        false => "No detectada",
        _ => "No determinada"
    };

    private static TonoBadge TonoConfianza(int confianza) => confianza switch
    {
        >= 95 => TonoBadge.Exito,
        >= 70 => TonoBadge.Advertencia,
        _ => TonoBadge.Peligro
    };

    public void Dispose()
    {
        _dispose = true;
        _cargaCts?.Cancel();
        _cargaCts?.Dispose();
        _cargaCts = null;
    }
}
