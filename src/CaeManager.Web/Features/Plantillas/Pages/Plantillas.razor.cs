using CaeManager.Application.Plantillas.Queries.ObtenerPlantillasDocumento;
using Microsoft.AspNetCore.Components;

namespace CaeManager.Web.Features.Plantillas.Pages;

public partial class Plantillas : ComponentBase
{
    private IReadOnlyList<PlantillaDocumentoListaDto> _plantillas = [];
    private bool _cargando = true;
    private bool _errorCarga;

    protected override Task OnInitializedAsync() => CargarAsync();

    private async Task CargarAsync()
    {
        _cargando = true;
        _errorCarga = false;
        StateHasChanged();

        try
        {
            _plantillas = await Mediator.Send(new ObtenerPlantillasDocumentoQuery());
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
}
