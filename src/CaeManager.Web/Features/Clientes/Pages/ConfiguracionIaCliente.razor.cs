using CaeManager.Application.Clientes.Queries.ObtenerClientePorId;
using CaeManager.Application.Cumplimiento.Queries.ObtenerEstadoTratamientoIaActual;
using CaeManager.Application.TiposDocumento.Commands.ActualizarLecturaIaCliente;
using CaeManager.Application.TiposDocumento.Queries.ObtenerConfiguracionIaPorCliente;
using CaeManager.Web.Components.DesignSystem;
using Microsoft.AspNetCore.Components;

namespace CaeManager.Web.Features.Clientes.Pages;

public partial class ConfiguracionIaCliente : ComponentBase, IDisposable
{
    [Parameter] public Guid ClienteId { get; set; }
    private CancellationTokenSource? _cicloCarga;
    private ClienteDetalleDto? _cliente;
    private EstadoTratamientoIaActualDto _tratamientoIa = new(false);
    private IReadOnlyList<ConfiguracionIaTipoDocumentoDto> _configuracion = [];
    private bool _cargando = true;
    private bool _errorCarga;
    private Guid? _actualizandoId;
    private Guid _entidadCargada;
    private bool _entidadInicializada;
    private int _versionCarga;
    private int _versionOperacion;
    private bool _desechado;

    private string ResumenNivelUno => $"{_configuracion.Count(tipo => tipo.GlobalActiva)} de {_configuracion.Count} activos";

    protected override Task OnParametersSetAsync()
    {
        if (_entidadInicializada && _entidadCargada == ClienteId) return Task.CompletedTask;
        _entidadInicializada = true;
        _entidadCargada = ClienteId;
        _versionOperacion++;
        _actualizandoId = null;
        _cliente = null;
        _tratamientoIa = new(false);
        _configuracion = [];
        return CargarAsync();
    }

    public void Dispose()
    {
        _desechado = true;
        _versionCarga++;
        _versionOperacion++;
        _cicloCarga?.Cancel();
        _cicloCarga?.Dispose();
    }

    private bool EsVigente(int version, Guid clienteId) => !_desechado && version == _versionCarga && clienteId == ClienteId;
    private bool EsOperacionVigente(int version, Guid clienteId) => !_desechado && version == _versionOperacion && clienteId == ClienteId;

    private async Task CargarAsync()
    {
        var version = ++_versionCarga;
        var clienteId = ClienteId;
        _cicloCarga?.Cancel();
        _cicloCarga?.Dispose();
        var cicloCarga = new CancellationTokenSource();
        _cicloCarga = cicloCarga;
        var token = cicloCarga.Token;
        _cargando = true;
        _errorCarga = false;
        StateHasChanged();
        try
        {
            var cliente = Mediator.Send(new ObtenerClientePorIdQuery(clienteId), token);
            var configuracion = Mediator.Send(new ObtenerConfiguracionIaPorClienteQuery(clienteId), token);
            var tratamientoIa = Mediator.Send(new ObtenerEstadoTratamientoIaActualQuery(), token);
            await Task.WhenAll(cliente, configuracion, tratamientoIa);
            if (!EsVigente(version, clienteId)) return;
            _cliente = await cliente;
            _configuracion = await configuracion;
            _tratamientoIa = await tratamientoIa;
            _errorCarga = _cliente is null;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception)
        {
            if (EsVigente(version, clienteId)) _errorCarga = true;
        }
        finally
        {
            if (EsVigente(version, clienteId)) _cargando = false;
        }
    }

    private async Task AlternarAsync(Guid tipoDocumentoId, bool activa)
    {
        if (_actualizandoId is not null || !_configuracion.Any(tipo => tipo.TipoDocumentoId == tipoDocumentoId && tipo.GlobalActiva)) return;
        var clienteId = ClienteId;
        var version = _versionOperacion;
        _actualizandoId = tipoDocumentoId;
        StateHasChanged();
        try
        {
            var resultado = await Mediator.Send(new ActualizarLecturaIaClienteCommand(clienteId, tipoDocumentoId, activa));
            if (resultado.EsFallido)
            {
                if (EsOperacionVigente(version, clienteId) && _actualizandoId == tipoDocumentoId)
                    ToastService.Mostrar(resultado.Error.Mensaje, TonoToast.Error);
                return;
            }
            if (!EsOperacionVigente(version, clienteId) || _actualizandoId != tipoDocumentoId) return;
            await CargarAsync();
        }
        finally
        {
            if (EsOperacionVigente(version, clienteId) && _actualizandoId == tipoDocumentoId) _actualizandoId = null;
        }
    }
}
