using CaeManager.Application.Clientes.Queries.ObtenerClientePorId;
using CaeManager.Application.TiposDocumento.Commands.ActualizarLecturaIaCliente;
using CaeManager.Application.TiposDocumento.Queries.ObtenerConfiguracionIaPorCliente;
using CaeManager.Web.Components.DesignSystem;
using Microsoft.AspNetCore.Components;

namespace CaeManager.Web.Features.Clientes.Pages;

public partial class ConfiguracionIaCliente : ComponentBase
{
    [Parameter] public Guid ClienteId { get; set; }

    private ClienteDetalleDto? _cliente;
    private IReadOnlyList<ConfiguracionIaTipoDocumentoDto> _configuracion = [];
    private bool _cargando = true;
    private bool _errorCarga;
    private Guid? _actualizandoId;

    protected override Task OnInitializedAsync() => CargarAsync();

    private async Task CargarAsync()
    {
        _cargando = true;
        _errorCarga = false;
        StateHasChanged();

        try
        {
            _cliente = await Mediator.Send(new ObtenerClientePorIdQuery(ClienteId));
            _configuracion = await Mediator.Send(new ObtenerConfiguracionIaPorClienteQuery(ClienteId));

            if (_cliente is null)
                _errorCarga = true;
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

    private async Task AlternarAsync(Guid tipoDocumentoId, bool activa)
    {
        _actualizandoId = tipoDocumentoId;
        StateHasChanged();

        try
        {
            var resultado = await Mediator.Send(new ActualizarLecturaIaClienteCommand(ClienteId, tipoDocumentoId, activa));
            if (resultado.EsFallido)
            {
                ToastService.Mostrar(resultado.Error.Mensaje, TonoToast.Error);
                return;
            }

            await CargarAsync();
        }
        finally
        {
            _actualizandoId = null;
        }
    }
}
