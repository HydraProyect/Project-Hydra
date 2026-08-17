using CaeManager.Application.Clientes.Queries.ObtenerClientesParaSelector;
using Microsoft.AspNetCore.Components;

namespace CaeManager.Web.Features.Configuracion.Pages;

public partial class SeleccionarClienteLecturaIa : ComponentBase
{
    private bool _cargando = true;
    private IReadOnlyList<ClienteSelectorDto> _clientes = [];
    private string _filtro = string.Empty;

    private IEnumerable<ClienteSelectorDto> ClientesFiltrados =>
        string.IsNullOrWhiteSpace(_filtro)
            ? _clientes
            : _clientes.Where(c => c.RazonSocial.Contains(_filtro, StringComparison.OrdinalIgnoreCase));

    protected override async Task OnInitializedAsync()
    {
        _clientes = await Mediator.Send(new ObtenerClientesParaSelectorQuery());
        _cargando = false;
    }
}
