using CaeManager.Application.Plantillas.Queries.ObtenerDocumentosGenerados;
using CaeManager.Application.Plantillas.Queries.ObtenerPlantillasDocumento;
using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadoresParaSelector;
using Microsoft.AspNetCore.Components;

namespace CaeManager.Web.Features.Plantillas.Pages;

public partial class DocumentosGenerados : ComponentBase
{
    private IReadOnlyList<DocumentoGeneradoListaDto> _documentosGenerados = [];
    private IReadOnlyList<PlantillaDocumentoListaDto> _plantillasDisponibles = [];
    private IReadOnlyList<TrabajadorSelectorDto> _trabajadoresDisponibles = [];
    private Guid? _plantillaFiltro;
    private Guid? _trabajadorFiltro;
    private bool _cargando = true;
    private bool _errorCarga;

    protected override async Task OnInitializedAsync()
    {
        _plantillasDisponibles = await Mediator.Send(new ObtenerPlantillasDocumentoQuery());
        _trabajadoresDisponibles = await Mediator.Send(new ObtenerTrabajadoresParaSelectorQuery());
        await CargarAsync();
    }

    private async Task CargarAsync()
    {
        _cargando = true;
        _errorCarga = false;
        StateHasChanged();

        try
        {
            _documentosGenerados = await Mediator.Send(new ObtenerDocumentosGeneradosQuery(_plantillaFiltro, _trabajadorFiltro));
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

    private Task CambiarPlantillaFiltroAsync(string valor)
    {
        _plantillaFiltro = Guid.TryParse(valor, out var id) ? id : null;
        return CargarAsync();
    }

    private Task CambiarTrabajadorFiltroAsync(string valor)
    {
        _trabajadorFiltro = Guid.TryParse(valor, out var id) ? id : null;
        return CargarAsync();
    }
}
