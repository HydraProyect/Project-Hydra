using CaeManager.Application.Clientes.Queries.ObtenerClientesParaSelector;
using CaeManager.Application.Integraciones.Commands.DesconectarBuzon;
using CaeManager.Application.Integraciones.Queries.ObtenerConexionesIntegracion;
using CaeManager.Domain.Integraciones;
using CaeManager.Web.Components.DesignSystem;
using MediatR;
using Microsoft.AspNetCore.Components;

namespace CaeManager.Web.Features.Integraciones.Pages;

/// <summary>Administración de conexiones de Microsoft 365 (P3-33) — solo Administrador, ver ConectarMicrosoft365Endpoints para el flujo OAuth.</summary>
public partial class Conexiones : ComponentBase
{
    [Inject] private IMediator Mediator { get; set; } = default!;
    [Inject] private ToastService ToastService { get; set; } = default!;
    [Inject] private ILogger<Conexiones> Logger { get; set; } = default!;

    [SupplyParameterFromQuery] public bool? Conectado { get; set; }
    [SupplyParameterFromQuery] public string? Error { get; set; }

    private IReadOnlyList<ClienteSelectorDto> _clientes = [];
    private IReadOnlyList<ConexionIntegracionListaDto> _conexiones = [];
    private bool _cargando = true;
    private Guid? _clienteSeleccionadoId;

    private ConexionIntegracionListaDto? _conexionADesconectar;
    private bool _desconectando;
    private Guid? _procesandoId;

    private string UrlConectar => _clienteSeleccionadoId is { } id
        ? $"/integraciones/conectar-microsoft365?clienteId={id}"
        : "/integraciones/conectar-microsoft365";

    protected override async Task OnInitializedAsync()
    {
        if (Conectado == true)
            ToastService.Mostrar("Buzón conectado correctamente.", TonoToast.Exito);
        else if (!string.IsNullOrWhiteSpace(Error))
            ToastService.Mostrar(MensajeError(Error), TonoToast.Error);

        try
        {
            _clientes = await Mediator.Send(new ObtenerClientesParaSelectorQuery());
            await CargarConexionesAsync();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error al cargar las conexiones de integración.");
            ToastService.Mostrar("No pudimos cargar las conexiones.", TonoToast.Error);
        }
        finally
        {
            _cargando = false;
        }
    }

    private async Task CargarConexionesAsync()
    {
        _conexiones = await Mediator.Send(new ObtenerConexionesIntegracionQuery());
    }

    private async Task DesconectarAsync()
    {
        if (_conexionADesconectar is null) return;

        _desconectando = true;
        _procesandoId = _conexionADesconectar.Id;
        StateHasChanged();

        try
        {
            var resultado = await Mediator.Send(new DesconectarBuzonCommand(_conexionADesconectar.Id));

            if (resultado.EsFallido)
            {
                ToastService.Mostrar(resultado.Error.Mensaje, TonoToast.Error);
                return;
            }

            ToastService.Mostrar("Buzón desconectado.", TonoToast.Exito);
            _conexionADesconectar = null;
            await CargarConexionesAsync();
        }
        finally
        {
            _desconectando = false;
            _procesandoId = null;
        }
    }

    private static TonoBadge ObtenerTonoEstado(EstadoConexionIntegracion estado) => estado switch
    {
        EstadoConexionIntegracion.Habilitada => TonoBadge.Exito,
        EstadoConexionIntegracion.ConError => TonoBadge.Peligro,
        _ => TonoBadge.Neutro
    };

    private static string ObtenerEtiquetaEstado(EstadoConexionIntegracion estado) => estado switch
    {
        EstadoConexionIntegracion.Habilitada => "Habilitada",
        EstadoConexionIntegracion.ConError => "Con error",
        _ => "Deshabilitada"
    };

    private static string MensajeError(string codigo) => codigo switch
    {
        "cancelado" => "Conexión cancelada.",
        "autenticacion" => "No pudimos autenticar con Microsoft — revisa Integraciones:Microsoft365 en la configuración.",
        "suscripcion" => "El buzón se autenticó pero no pudimos activar las notificaciones. Inténtalo de nuevo.",
        _ => "No pudimos completar la conexión."
    };
}
