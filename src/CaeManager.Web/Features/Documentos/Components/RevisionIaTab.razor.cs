using CaeManager.Application.Documentos.Commands.AplicarDeteccionIaDocumento;
using CaeManager.Application.Documentos.Commands.ResolverRevisionIaDocumento;
using CaeManager.Application.Documentos.Queries.ObtenerRevisionesIaPendientes;
using CaeManager.Web.Components.DesignSystem;
using Microsoft.AspNetCore.Components;

namespace CaeManager.Web.Features.Documentos.Components;

public partial class RevisionIaTab : ComponentBase
{
    /// <summary>
    /// Umbral de confirmación masiva (04 § 8.2, DDL-065): 95 — la misma frontera que el sistema
    /// usa en todas partes para <b>actuar sin revisión humana</b> (banda verde de
    /// <see cref="TonoConfianza"/>, Issue #19; auto-creación de SubidaMasiva).
    /// Estuvo en 85 desde la Fase D, con un comentario que afirmaba seguir el badge verde: no era
    /// cierto, el verde empieza en 95, y esta acción <b>renueva el Documento</b> — no descarta un
    /// aviso. Confirmaba en bloque revisiones que su propio badge marcaba en ámbar (OD-32).
    /// </summary>
    private const int UmbralConfianzaLote = 95;

    private IReadOnlyList<RevisionIaDocumentoDto> _revisiones = [];
    private bool _cargando = true;
    private bool _errorCarga;
    private Guid? _procesandoId;
    private bool _confirmandoLote;

    private IReadOnlyList<RevisionIaDocumentoDto> RevisionesConfirmablesEnLote =>
        _revisiones.Where(r => r.ConfianzaGeneral >= UmbralConfianzaLote && r.FechaEmisionDetectada is not null).ToList();

    /// <summary>Como mucho un documento a la vez — evita cargar N iframes de PDF si el usuario despliega varias filas seguidas.</summary>
    private Guid? _documentoIdExpandido;

    protected override Task OnInitializedAsync() => CargarAsync();

    private async Task CargarAsync()
    {
        _cargando = true;
        _errorCarga = false;
        StateHasChanged();

        try
        {
            _revisiones = await Mediator.Send(new ObtenerRevisionesIaPendientesQuery());
        }
        catch (Exception)
        {
            _errorCarga = true;
        }
        finally
        {
            _cargando = false;
        }
    }

    private async Task ResolverAsync(Guid revisionId)
    {
        _procesandoId = revisionId;
        StateHasChanged();

        try
        {
            var resultado = await Mediator.Send(new ResolverRevisionIaDocumentoCommand(revisionId));
            if (resultado.EsFallido)
            {
                ToastService.Mostrar(resultado.Error.Mensaje, TonoToast.Error);
                return;
            }

            ToastService.Mostrar("Revisión marcada como hecha.", TonoToast.Exito);
            await CargarAsync();
        }
        finally
        {
            _procesandoId = null;
        }
    }

    /// <summary>Fase D ("Aceptar lo detectado por la IA") — renueva el Documento con la fecha detectada, en vez de obligar a corregirlo a mano en /documentos.</summary>
    private async Task AceptarDeteccionAsync(Guid revisionId)
    {
        _procesandoId = revisionId;
        StateHasChanged();

        try
        {
            var resultado = await Mediator.Send(new AplicarDeteccionIaDocumentoCommand(revisionId));
            if (resultado.EsFallido)
            {
                ToastService.Mostrar(resultado.Error.Mensaje, TonoToast.Error);
                return;
            }

            ToastService.Mostrar("Detección aplicada al documento.", TonoToast.Exito);
            await CargarAsync();
        }
        finally
        {
            _procesandoId = null;
        }
    }

    /// <summary>
    /// Secuencial a propósito, no en paralelo: el DbContext scoped al
    /// circuito no admite dos llamadas EF concurrentes sobre la misma
    /// instancia (mismo motivo documentado en ObtenerDashboardEjecutivoQueryHandler).
    /// </summary>
    private async Task ConfirmarLoteAsync()
    {
        _confirmandoLote = true;
        StateHasChanged();

        try
        {
            var revisionIds = RevisionesConfirmablesEnLote.Select(r => r.Id).ToList();
            var confirmadas = 0;
            var errores = 0;

            foreach (var revisionId in revisionIds)
            {
                var resultado = await Mediator.Send(new AplicarDeteccionIaDocumentoCommand(revisionId));
                if (resultado.EsExitoso) confirmadas++;
                else errores++;
            }

            var resumen = $"{confirmadas} revisión(es) confirmada(s)" + (errores > 0 ? $", {errores} no se pudieron aplicar." : ".");
            ToastService.Mostrar(resumen, errores == 0 ? TonoToast.Exito : TonoToast.Advertencia);

            await CargarAsync();
        }
        finally
        {
            _confirmandoLote = false;
        }
    }

    private void AlternarPrevisualizacion(Guid documentoId) =>
        _documentoIdExpandido = _documentoIdExpandido == documentoId ? null : documentoId;

    private static TonoBadge TonoConfianza(int confianza) => confianza switch
    {
        >= 95 => TonoBadge.Exito,
        >= 70 => TonoBadge.Advertencia,
        _ => TonoBadge.Peligro
    };
}
