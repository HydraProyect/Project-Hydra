using CaeManager.Application.Plantillas.Queries.ObtenerDocumentosGenerados;
using CaeManager.Application.Plantillas.Queries.ObtenerPlantillasDocumento;
using CaeManager.Application.Plantillas.Queries.ObtenerTotalDocumentosGeneradosConAvisos;
using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadoresParaSelector;
using Microsoft.AspNetCore.Components;

namespace CaeManager.Web.Features.Plantillas.Components;

public partial class DocumentosGeneradosPanel : ComponentBase
{
    private IReadOnlyList<DocumentoGeneradoListaDto> _documentosGenerados = [];
    private IReadOnlyList<PlantillaDocumentoListaDto> _plantillasDisponibles = [];
    private IReadOnlyList<TrabajadorSelectorDto> _trabajadoresDisponibles = [];
    private Dictionary<Guid, Guid> _versionActualPorPlantilla = [];
    private Guid? _plantillaFiltro;
    private Guid? _trabajadorFiltro;
    private bool _cargando = true;
    private bool _errorCarga;

    /// <summary>
    /// Notifica cuántos documentos generados quedaron en
    /// <see cref="Domain.Plantillas.EstadoDocumentoGenerado.GeneradoConAvisos"/>
    /// — lo usa Plantillas.razor para el badge de la pestaña "Generados"
    /// (Plantillas TALVEG.dc.html: tono aviso, "N documentos generados con
    /// avisos, pendientes de revisar"). Se pide una sola vez en
    /// <see cref="OnInitializedAsync"/>, con
    /// <see cref="ObtenerTotalDocumentosGeneradosConAvisosQuery"/> —que no
    /// admite filtro—, para que no dependa del filtro de este panel:
    /// defecto encontrado el 2026-09-08, el número anterior venía de
    /// <c>_documentosGenerados.Count</c> tras cada <see cref="CargarAsync"/>
    /// y bajaba al filtrar aunque los avisos pendientes de revisar
    /// siguieran siendo los mismos.
    /// </summary>
    [Parameter] public EventCallback<int> AvisosPendientesCambiado { get; set; }

    protected override async Task OnInitializedAsync()
    {
        _plantillasDisponibles = await Mediator.Send(new ObtenerPlantillasDocumentoQuery());
        _versionActualPorPlantilla = _plantillasDisponibles.ToDictionary(p => p.Id, p => p.UltimaVersionId);
        _trabajadoresDisponibles = await Mediator.Send(new ObtenerTrabajadoresParaSelectorQuery());

        var avisosPendientes = await Mediator.Send(new ObtenerTotalDocumentosGeneradosConAvisosQuery());
        await AvisosPendientesCambiado.InvokeAsync(avisosPendientes);

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

    /// <summary>
    /// Los dos filtros de la barra. Separa "todavía no se ha generado ninguno"
    /// de "ninguno con estos filtros": con una plantilla elegida, la primera
    /// frase manda a generar de nuevo algo que ya existe en otra.
    /// </summary>
    private bool HayFiltrosActivos => _plantillaFiltro is not null || _trabajadorFiltro is not null;

    /// <summary>
    /// Quita los dos filtros en una sola recarga; encadenar los manejadores
    /// lanzaría dos consultas y la primera devolvería una lista que ya no se
    /// va a pintar. No vuelve a notificar <see cref="AvisosPendientesCambiado"/>:
    /// ese badge no depende de estos filtros, así que no hay nada que
    /// re-sincronizar aquí.
    /// </summary>
    private Task LimpiarFiltrosAsync()
    {
        _plantillaFiltro = null;
        _trabajadorFiltro = null;
        return CargarAsync();
    }
}
