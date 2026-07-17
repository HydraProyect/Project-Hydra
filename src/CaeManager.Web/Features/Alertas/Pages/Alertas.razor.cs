using CaeManager.Application.Alertas.Queries.ObtenerAlertas;
using Microsoft.AspNetCore.Components;

namespace CaeManager.Web.Features.Alertas.Pages;

public partial class Alertas : ComponentBase
{
    private const int TamanoPagina = 20;

    [Inject] private NavigationManager NavigationManager { get; set; } = default!;

    private IReadOnlyList<AlertaDto> _alertas = [];
    private bool _cargando = true;
    private bool _errorCarga;
    private int _pagina = 1;

    private int TotalPaginas => Math.Max(1, (int)Math.Ceiling(_alertas.Count / (double)TamanoPagina));
    private IReadOnlyList<AlertaDto> AlertasDePagina => _alertas.Skip((_pagina - 1) * TamanoPagina).Take(TamanoPagina).ToList();

    protected override Task OnInitializedAsync() => CargarAsync();

    private Task IrAPaginaAsync(int pagina)
    {
        _pagina = pagina;
        return Task.CompletedTask;
    }

    private void GestionarDocumento(Guid documentoId) =>
        NavigationManager.NavigateTo($"/documentos?documentoId={documentoId}");

    private async Task CargarAsync()
    {
        _cargando = true;
        _errorCarga = false;
        StateHasChanged();

        try
        {
            _alertas = await Mediator.Send(new ObtenerAlertasQuery());
            _pagina = 1;
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
