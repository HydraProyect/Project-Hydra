using CaeManager.Application.Documentos.Commands.ResolverRevisionIaDocumento;
using CaeManager.Application.Documentos.Queries.ObtenerRevisionesIaPendientes;
using CaeManager.Web.Components.DesignSystem;
using Microsoft.AspNetCore.Components;

namespace CaeManager.Web.Features.Documentos.Pages;

public partial class RevisionIa : ComponentBase
{
    private IReadOnlyList<RevisionIaDocumentoDto> _revisiones = [];
    private bool _cargando = true;
    private bool _errorCarga;
    private Guid? _procesandoId;

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

    private void AlternarPrevisualizacion(Guid documentoId) =>
        _documentoIdExpandido = _documentoIdExpandido == documentoId ? null : documentoId;

    private static TonoBadge TonoConfianza(int confianza) => confianza switch
    {
        >= 95 => TonoBadge.Exito,
        >= 70 => TonoBadge.Advertencia,
        _ => TonoBadge.Peligro
    };
}
