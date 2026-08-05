using CaeManager.Application.Bandeja.Queries.ObtenerBandejaGestor;
using CaeManager.Web.Components;
using Microsoft.AspNetCore.Components;

namespace CaeManager.Web.Features.Bandeja.Pages;

public partial class Bandeja : ComponentBase
{
    [Inject] private NavigationManager NavigationManager { get; set; } = default!;

    [SupplyParameterFromQuery(Name = "tipo")]
    public string? TipoInicial { get; set; }

    private IReadOnlyList<ItemBandejaDto> _items = [];
    private string _tipoFiltro = string.Empty;
    private bool _cargando = true;
    private bool _errorCarga;
    private string? _idEnfocado;

    private IReadOnlyList<ItemBandejaDto> ItemsFiltrados =>
        Enum.TryParse<TipoItemBandeja>(_tipoFiltro, out var tipo)
            ? _items.Where(i => i.Tipo == tipo).ToList()
            : _items;

    protected override Task OnInitializedAsync() => CargarAsync();

    /// <summary>
    /// La URL es la fuente de verdad del filtro, no solo su semilla inicial
    /// — mismo patrón que el resto de listados (P1-18 de
    /// docs/business/MATURITY_REVIEW.md).
    /// </summary>
    protected override void OnParametersSet()
    {
        _tipoFiltro = !string.IsNullOrWhiteSpace(TipoInicial) && Enum.TryParse<TipoItemBandeja>(TipoInicial, out _)
            ? TipoInicial
            : string.Empty;
    }

    private Task CambiarFiltroAsync(string valor)
    {
        _tipoFiltro = valor;
        _idEnfocado = null;
        NavigationManager.ActualizarFiltroEnUrl("tipo", valor);
        return Task.CompletedTask;
    }

    private async Task CargarAsync()
    {
        _cargando = true;
        _errorCarga = false;
        StateHasChanged();

        try
        {
            _items = await Mediator.Send(new ObtenerBandejaGestorQuery());
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

    private string ObtenerClaseTarjeta(ItemBandejaDto item) => item.Id == _idEnfocado ? "panel-resolver-item-enfocado" : "";

    private async Task ManejarAtajoAsync(string tecla)
    {
        var items = ItemsFiltrados;
        if (items.Count == 0) return;

        switch (tecla)
        {
            case "j":
                {
                    var indiceActual = _idEnfocado is null ? -1 : items.ToList().FindIndex(i => i.Id == _idEnfocado);
                    _idEnfocado = items[Math.Min(indiceActual + 1, items.Count - 1)].Id;
                    break;
                }
            case "k":
                {
                    var indiceActual = _idEnfocado is null ? 0 : items.ToList().FindIndex(i => i.Id == _idEnfocado);
                    _idEnfocado = items[Math.Max(indiceActual - 1, 0)].Id;
                    break;
                }
            case "Enter":
                if (_idEnfocado is { } idAbrir)
                {
                    var item = items.FirstOrDefault(i => i.Id == idAbrir);
                    if (item is not null)
                        await AccionesBandeja.AbrirAsync(item, NavigationManager, WorkspaceService);
                }
                break;
        }

        StateHasChanged();
    }
}
