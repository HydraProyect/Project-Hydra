using CaeManager.Application.Integraciones.Commands.CambiarActivoProveedorPlataforma;
using CaeManager.Application.Integraciones.Queries.ObtenerProveedoresPlataformaCae;
using MediatR;
using Microsoft.AspNetCore.Components;

namespace CaeManager.Web.Features.Integraciones.Pages;

public partial class ConectoresCae : ComponentBase
{
    [Inject] private IMediator Mediator { get; set; } = default!;

    private bool _cargando = true;
    private string? _error;
    private IReadOnlyList<ProveedorPlataformaCaeListaDto> _proveedores = [];
    private Guid? _procesandoId;

    protected override async Task OnInitializedAsync() => await CargarAsync();

    private async Task CargarAsync()
    {
        _cargando = true;
        _error = null;
        try
        {
            // SoloActivos: false — a diferencia del selector de canal
            // (CentroWorkspacePanel.razor), esta pantalla existe justo para
            // gestionar los inactivos también.
            _proveedores = await Mediator.Send(new ObtenerProveedoresPlataformaCaeQuery(SoloActivos: false));
        }
        catch
        {
            _error = "No pudimos cargar el catálogo de conectores.";
        }
        finally
        {
            _cargando = false;
        }
    }

    private async Task CambiarActivoAsync(Guid proveedorId, bool activo)
    {
        _procesandoId = proveedorId;
        try
        {
            var resultado = await Mediator.Send(new CambiarActivoProveedorPlataformaCommand(proveedorId, activo));
            if (resultado.EsFallido)
            {
                _error = resultado.Error.Mensaje;
                return;
            }

            await CargarAsync();
        }
        finally
        {
            _procesandoId = null;
        }
    }
}
