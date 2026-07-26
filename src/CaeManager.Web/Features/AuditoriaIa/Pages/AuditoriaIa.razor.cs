using CaeManager.Application.DocumentosIa.Queries;
using CaeManager.Application.Common;
using CaeManager.Web.Components.DesignSystem;
using MediatR;
using Microsoft.AspNetCore.Components;

namespace CaeManager.Web.Features.AuditoriaIa.Pages;

public partial class AuditoriaIa : ComponentBase
{
    [Inject] private IMediator Mediator { get; set; } = default!;

    private ResultadoPaginado<RegistroAuditoriaIaDto>? _resultado;
    private bool _cargando = true;
    private bool _error;
    private string? _filtroProveedor;
    private int _pagina = 1;
    private const int TamanoPagina = 30;

    protected override Task OnInitializedAsync() => CargarAsync();

    private async Task CargarAsync()
    {
        _cargando = true;
        _error = false;
        StateHasChanged();

        try
        {
            _resultado = await Mediator.Send(new ObtenerAuditoriaIaQuery(_filtroProveedor, _pagina, TamanoPagina));
        }
        catch (Exception)
        {
            _error = true;
        }
        finally
        {
            _cargando = false;
        }
    }

    private Task FiltrarPorProveedorAsync(string? proveedor)
    {
        _filtroProveedor = string.IsNullOrWhiteSpace(proveedor) ? null : proveedor;
        _pagina = 1;
        return CargarAsync();
    }

    private Task IrAPaginaAsync(int pagina)
    {
        _pagina = pagina;
        return CargarAsync();
    }

    private static TonoBadge BadgeParaProveedor(string proveedorCodigo) => proveedorCodigo switch
    {
        "cache" => TonoBadge.Info,
        "ninguno" => TonoBadge.Peligro,
        _ => TonoBadge.Exito
    };

    private static TonoBadge BadgeParaConfianza(int confianza) => confianza switch
    {
        >= 80 => TonoBadge.Exito,
        >= 60 => TonoBadge.Advertencia,
        _ => TonoBadge.Peligro
    };

    private static string FormatearCoste(decimal? coste) =>
        coste.HasValue ? $"${coste.Value:F4}" : "—";
}
