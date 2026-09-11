using CaeManager.Application.Plantillas.Queries.ObtenerDocumentosGenerados;
using CaeManager.Application.Plantillas.Queries.ObtenerPlantillasDocumento;
using CaeManager.Application.Plantillas.Queries.ObtenerTotalDocumentosGeneradosConAvisos;
using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadoresParaSelector;
using CaeManager.Domain.Plantillas;
using Microsoft.AspNetCore.Components;

namespace CaeManager.Web.Features.Plantillas.Components;

public partial class DocumentosGeneradosPanel : ComponentBase, IDisposable
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
    /// Número de la carga en curso. Cambiar un filtro mientras la anterior
    /// sigue en vuelo lanza otra: cada una se numera antes del <c>await</c> y,
    /// al volver, solo escribe estado si sigue siendo la vigente y el panel
    /// sigue vivo. Sin esto, la respuesta lenta de un filtro anterior pisaría
    /// la lista del filtro que se ve elegido. Mismo patrón que Empresas y
    /// PlantillasTab.
    /// </summary>
    private int _cargaVigente;

    /// <summary>
    /// Se cancela al retirarse el panel (salir de la página, cambiar a la
    /// sub-pestaña Catálogo o a otra pestaña de /documentos): las consultas en
    /// curso dejan de trabajar para nadie. Mismo patrón que
    /// DeteccionTrabajadores y Empresas.
    /// </summary>
    private readonly CancellationTokenSource _ciclo = new();
    private bool _desechado;

    /// <summary>Documento generado con el foco de teclado (atajos j/k, P3-31).</summary>
    private Guid? _idEnfocado;

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

    public void Dispose()
    {
        if (_desechado)
            return;

        _desechado = true;
        _ciclo.Cancel();
        _ciclo.Dispose();
    }

    /// <summary>La respuesta es de la carga vigente y el panel sigue vivo.</summary>
    private bool EsVigente(int carga) => !_desechado && carga == _cargaVigente;

    protected override async Task OnInitializedAsync()
    {
        var token = _ciclo.Token;

        try
        {
            _plantillasDisponibles = await Mediator.Send(new ObtenerPlantillasDocumentoQuery(), token);
            _versionActualPorPlantilla = _plantillasDisponibles.ToDictionary(p => p.Id, p => p.UltimaVersionId);
            _trabajadoresDisponibles = await Mediator.Send(new ObtenerTrabajadoresParaSelectorQuery(), token);

            var avisosPendientes = await Mediator.Send(new ObtenerTotalDocumentosGeneradosConAvisosQuery(), token);
            if (_desechado)
                return;

            await AvisosPendientesCambiado.InvokeAsync(avisosPendientes);
        }
        catch (Exception) when (_desechado)
        {
            // Retirado a mitad del arranque: la cancelación no es un fallo que enseñar.
            return;
        }

        await CargarAsync();
    }

    private async Task CargarAsync()
    {
        if (_desechado)
            return;

        var carga = ++_cargaVigente;
        _cargando = true;
        _errorCarga = false;
        StateHasChanged();

        try
        {
            var generados = await Mediator.Send(new ObtenerDocumentosGeneradosQuery(_plantillaFiltro, _trabajadorFiltro), _ciclo.Token);
            if (!EsVigente(carga))
                return;

            _documentosGenerados = generados;
            _idEnfocado = null;
        }
        catch (Exception) when (!EsVigente(carga))
        {
            // Una carga superada (o cancelada al retirarse) que falla no es un
            // error de la vigente: no puede tapar su resultado con el estado de error.
        }
        catch (Exception)
        {
            _errorCarga = true;
        }
        finally
        {
            if (EsVigente(carga))
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
    /// de "ninguno con este filtro": con una plantilla elegida, la primera
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

    private bool HayFilas => !_cargando && !_errorCarga && _documentosGenerados.Count > 0;

    /// <summary>
    /// Sin filtro, el total de documentos generados (la consulta no pagina). Con
    /// filtro, solo cuántos lo cumplen: el total sin filtrar no llega al panel.
    /// </summary>
    private string TextoResumen
    {
        get
        {
            var total = _documentosGenerados.Count;
            var documentos = total == 1 ? "documento" : "documentos";
            return HayFiltrosActivos
                ? $"{total} {documentos} con este filtro"
                : $"{total} {documentos} {(total == 1 ? "generado" : "generados")}";
        }
    }

    /// <summary>Desglose por estado de lo que se ve en la lista, no del total sin filtrar.</summary>
    private string TituloResumen
    {
        get
        {
            var sinAvisos = _documentosGenerados.Count(d => d.Estado == EstadoDocumentoGenerado.Generado);
            var conAvisos = _documentosGenerados.Count(d => d.Estado == EstadoDocumentoGenerado.GeneradoConAvisos);
            return $"{sinAvisos} generados sin avisos · {conAvisos} generados con avisos";
        }
    }

    private static string DescribirFila(DocumentoGeneradoListaDto generado) =>
        generado.TrabajadorNombreCompleto is { } trabajador
            ? $"{generado.PlantillaNombre} — {trabajador}"
            : generado.PlantillaNombre;

    private void ManejarAtajo(string tecla)
    {
        if (_documentosGenerados.Count == 0) return;

        var indiceActual = _idEnfocado is { } id
            ? _documentosGenerados.ToList().FindIndex(d => d.DocumentoGeneradoId == id)
            : -1;

        switch (tecla)
        {
            case "j":
                _idEnfocado = _documentosGenerados[Math.Min(indiceActual + 1, _documentosGenerados.Count - 1)].DocumentoGeneradoId;
                break;
            case "k":
                _idEnfocado = _documentosGenerados[Math.Max(indiceActual - 1, 0)].DocumentoGeneradoId;
                break;
            case "Enter":
                if (indiceActual >= 0)
                    Navigation.NavigateTo($"/documentos?documentoId={_documentosGenerados[indiceActual].DocumentoId}");
                break;
        }
    }
}
