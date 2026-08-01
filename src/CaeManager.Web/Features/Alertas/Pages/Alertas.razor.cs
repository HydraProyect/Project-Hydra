using CaeManager.Application.Alertas.Queries.ObtenerAlertas;
using CaeManager.Domain.Documentos;
using Microsoft.AspNetCore.Components;

namespace CaeManager.Web.Features.Alertas.Pages;

public partial class Alertas : ComponentBase
{
    private const int TamanoPagina = 20;

    [Inject] private NavigationManager NavigationManager { get; set; } = default!;

    /// <summary>
    /// Permite llegar aquí desde el Dashboard con el filtro de Estado ya
    /// aplicado (p. ej. la tarjeta KPI "Documentos vencidos" enlaza a
    /// "/alertas?estado=Vencido").
    /// </summary>
    [SupplyParameterFromQuery] public string? Estado { get; set; }

    private IReadOnlyList<AlertaDto> _alertas = [];
    private string _estadoFiltro = string.Empty;
    private bool _cargando = true;
    private bool _errorCarga;
    private int _pagina = 1;

    private IReadOnlyList<AlertaDto> AlertasFiltradas =>
        Enum.TryParse<EstadoDocumento>(_estadoFiltro, out var estado)
            ? _alertas.Where(a => a.Estado == estado).ToList()
            : _alertas;

    private int TotalPaginas => Math.Max(1, (int)Math.Ceiling(AlertasFiltradas.Count / (double)TamanoPagina));
    private IReadOnlyList<AlertaDto> AlertasDePagina => AlertasFiltradas.Skip((_pagina - 1) * TamanoPagina).Take(TamanoPagina).ToList();

    protected override Task OnInitializedAsync()
    {
        if (!string.IsNullOrWhiteSpace(Estado) && Enum.TryParse<EstadoDocumento>(Estado, out _))
            _estadoFiltro = Estado;

        return CargarAsync();
    }

    private Task IrAPaginaAsync(int pagina)
    {
        _pagina = pagina;
        return Task.CompletedTask;
    }

    private Task CambiarEstadoFiltroAsync(string valor)
    {
        _estadoFiltro = valor;
        _pagina = 1;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Un documento faltante (P1-15) no tiene DocumentoId — no hay nada que
    /// "gestionar" todavía. Lleva al drawer de creación con el propietario y
    /// el tipo ya elegidos en vez de a un documento inexistente.
    /// </summary>
    private void GestionarAlerta(AlertaDto alerta) => NavigationManager.NavigateTo(
        alerta.DocumentoId is { } documentoId
            ? $"/documentos?documentoId={documentoId}"
            : $"/documentos?trabajadorId={alerta.TrabajadorId}&tipoDocumentoId={alerta.TipoDocumentoId}");

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
