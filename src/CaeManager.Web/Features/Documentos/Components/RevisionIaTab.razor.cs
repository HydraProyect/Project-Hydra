using CaeManager.Application.Documentos.Commands.AplicarDeteccionIaDocumento;
using CaeManager.Application.Documentos.Commands.CorregirRevisionIaDocumento;
using CaeManager.Application.Documentos.Commands.ResolverRevisionIaDocumento;
using CaeManager.Application.Documentos.Queries.ObtenerRevisionesIaPendientes;
using CaeManager.Web.Components.DesignSystem;
using Microsoft.AspNetCore.Components;

namespace CaeManager.Web.Features.Documentos.Components;

public partial class RevisionIaTab : ComponentBase, IDisposable
{
    private const int UmbralConfianzaLote = 95;

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
        Confirmables,
        Manuales,
        SinFecha
    }

    private static IReadOnlyList<FiltroRevision> Filtros { get; } =
    [
        FiltroRevision.Todas,
        FiltroRevision.Confirmables,
        FiltroRevision.Manuales,
        FiltroRevision.SinFecha
    ];

    private IReadOnlyList<RevisionIaDocumentoDto> RevisionesVisibles => _revisiones.Where(CumpleFiltro).ToList();
    private IReadOnlyList<RevisionIaDocumentoDto> RevisionesConfirmablesEnLote => _revisiones.Where(EsConfirmable).ToList();
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
                    ToastService.Mostrar(resultado.Error.Mensaje, TonoToast.Error);
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
                    ToastService.Mostrar(resultado.Error.Mensaje, TonoToast.Error);
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
                    ToastService.Mostrar(resultado.Error.Mensaje, TonoToast.Error);
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

    private async Task ConfirmarLoteAsync()
    {
        if (_confirmandoLote || _operacionEnCurso)
        {
            return;
        }

        var revisionIds = RevisionesConfirmablesEnLote.Select(r => r.Id).ToList();
        if (revisionIds.Count == 0)
        {
            return;
        }

        _confirmandoLote = true;
        _operacionEnCurso = true;
        StateHasChanged();

        try
        {
            var confirmadas = 0;
            var errores = 0;
            foreach (var revisionId in revisionIds)
            {
                var resultado = await Mediator.Send(new AplicarDeteccionIaDocumentoCommand(revisionId));
                if (resultado.EsExitoso)
                {
                    confirmadas++;
                }
                else
                {
                    errores++;
                }
            }

            if (_dispose)
            {
                return;
            }

            ToastService.Mostrar(
                ResumenLote(confirmadas, revisionIds.Count, errores),
                confirmadas == revisionIds.Count ? TonoToast.Exito : TonoToast.Advertencia);
            _confirmacionLoteVisible = false;
            await CargarAsync();
        }
        finally
        {
            _confirmandoLote = false;
            _operacionEnCurso = false;
        }
    }

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

    private void PrepararConfirmacionLote() => _confirmacionLoteVisible = RevisionesConfirmablesEnLote.Count > 0;
    private void PrepararDescartar() => _confirmacionDescartarVisible = RevisionSeleccionada is not null;
    private void PrepararCorreccionManual()
    {
        if (RevisionSeleccionada is not { } revision)
        {
            return;
        }

        _fechaEmisionManual = revision.FechaEmisionIntroducida?.ToString("yyyy-MM-dd") ?? string.Empty;
        _errorFechaManual = null;
        _correccionManualVisible = true;
    }

    private void CerrarCorreccionManual()
    {
        _correccionManualVisible = false;
        _errorFechaManual = null;
    }
    private void AlternarPrevisualizacion(Guid documentoId) => _documentoIdExpandido = _documentoIdExpandido == documentoId ? null : documentoId;

    private bool CumpleFiltro(RevisionIaDocumentoDto revision) => _filtro switch
    {
        FiltroRevision.Confirmables => EsConfirmable(revision),
        FiltroRevision.Manuales => revision.FechaEmisionDetectada is not null && !EsConfirmable(revision),
        FiltroRevision.SinFecha => revision.FechaEmisionDetectada is null,
        _ => true
    };

    private static bool EsConfirmable(RevisionIaDocumentoDto revision) => revision.ConfianzaGeneral >= UmbralConfianzaLote && revision.FechaEmisionDetectada is not null;

    private static string TextoFiltro(FiltroRevision f) => f switch
    {
        FiltroRevision.Confirmables => "Confirmables",
        FiltroRevision.Manuales => "Manual",
        FiltroRevision.SinFecha => "Sin fecha",
        _ => "Todas"
    };

    private string ClaseFiltro(FiltroRevision f) => _filtro == f ? "revision-ia-filtro activo" : "revision-ia-filtro";
    private string ClaseRevision(RevisionIaDocumentoDto r) => RevisionSeleccionada?.Id == r.Id ? "revision-ia-fila activa" : "revision-ia-fila";
    private static string Ambito(RevisionIaDocumentoDto r) => r.TrabajadorId is null ? "Empresa" : "Trabajador";
    private static string EtiquetaTratamiento(RevisionIaDocumentoDto r) => EsConfirmable(r)
        ? "Confirmable en lote"
        : r.FechaEmisionDetectada is null ? "Sin fecha" : "Revisión manual";

    private static string ResumenLote(int confirmadas, int solicitadas, int errores) => confirmadas == solicitadas
        ? $"{confirmadas} revisión(es) confirmada(s)."
        : $"Se aplicaron {confirmadas} de {solicitadas} revisiones; {errores} no se pudieron aplicar.";

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
