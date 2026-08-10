using CaeManager.Application.Clientes.Queries.ObtenerClientesParaSelector;
using CaeManager.Application.Integraciones.Commands.ActualizarLineaWhatsApp;
using CaeManager.Application.Integraciones.Commands.CrearLineaWhatsApp;
using CaeManager.Application.Integraciones.Commands.DesconectarBuzon;
using CaeManager.Application.Integraciones.Queries.ObtenerConexionesIntegracion;
using CaeManager.Application.Integraciones.Queries.ObtenerLineasWhatsApp;
using CaeManager.Domain.Integraciones;
using CaeManager.Infrastructure.Autorizacion;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Components.DesignSystem;
using MediatR;
using Microsoft.AspNetCore.Components;

namespace CaeManager.Web.Features.Integraciones.Pages;

/// <summary>Administración de conexiones de Microsoft 365 (P3-33) y líneas WhatsApp — solo Administrador.</summary>
public partial class Conexiones : ComponentBase
{
    [Inject] private IMediator Mediator { get; set; } = default!;
    [Inject] private ToastService ToastService { get; set; } = default!;
    [Inject] private ILogger<Conexiones> Logger { get; set; } = default!;
    [Inject] private DirectorioUsuariosTenant DirectorioUsuarios { get; set; } = default!;

    [SupplyParameterFromQuery] public bool? Conectado { get; set; }
    [SupplyParameterFromQuery] public string? Error { get; set; }

    private record GestorSelectorDto(Guid Id, string NombreCompleto);

    private IReadOnlyList<ClienteSelectorDto> _clientes = [];
    private IReadOnlyList<ConexionIntegracionListaDto> _conexiones = [];
    private IReadOnlyList<LineaWhatsAppListaDto> _lineas = [];
    private IReadOnlyList<GestorSelectorDto> _gestores = [];
    private bool _cargando = true;
    private Guid? _clienteSeleccionadoId;
    private Guid? _gestorPropietarioSeleccionadoId;

    // --- Modal de línea WhatsApp (alta/edición) ---
    private bool _modalLineaVisible;
    private bool _guardandoLinea;
    private LineaWhatsAppListaDto? _lineaEnEdicion;
    private string _lineaNombre = string.Empty;
    private string _lineaNumero = string.Empty;
    private string _lineaPhoneNumberId = string.Empty;
    private string _lineaWabaId = string.Empty;
    private string _lineaToken = string.Empty;
    private ModoAsignacionLinea _lineaModo = ModoAsignacionLinea.GestorFijo;
    private Guid? _lineaComercialId;
    private readonly HashSet<Guid> _lineaMiembros = [];
    private Guid? _lineaClienteId;
    private string _lineaMensajeAutoTriage = string.Empty;

    private ConexionIntegracionListaDto? _conexionADesconectar;
    private bool _desconectando;
    private Guid? _procesandoId;

    private string UrlConectar
    {
        get
        {
            if (_gestorPropietarioSeleccionadoId is { } gestorId)
                return $"/integraciones/conectar-microsoft365?gestorPropietarioId={gestorId}";
            if (_clienteSeleccionadoId is { } clienteId)
                return $"/integraciones/conectar-microsoft365?clienteId={clienteId}";
            return "/integraciones/conectar-microsoft365";
        }
    }

    /// <summary>Cliente y buzón personal son mutuamente excluyentes — elegir uno limpia el otro.</summary>
    private void SeleccionarCliente(string valor)
    {
        _clienteSeleccionadoId = Guid.TryParse(valor, out var id) ? id : null;
        if (_clienteSeleccionadoId is not null) _gestorPropietarioSeleccionadoId = null;
    }

    private void SeleccionarGestorPropietario(string valor)
    {
        _gestorPropietarioSeleccionadoId = Guid.TryParse(valor, out var id) ? id : null;
        if (_gestorPropietarioSeleccionadoId is not null) _clienteSeleccionadoId = null;
    }

    protected override async Task OnInitializedAsync()
    {
        if (Conectado == true)
            ToastService.Mostrar("Buzón conectado correctamente.", TonoToast.Exito);
        else if (!string.IsNullOrWhiteSpace(Error))
            ToastService.Mostrar(MensajeError(Error), TonoToast.Error);

        try
        {
            _clientes = await Mediator.Send(new ObtenerClientesParaSelectorQuery());

            var gestores = await DirectorioUsuarios.ObtenerVisiblesEnRolAsync(Roles.GestorCae);
            _gestores = gestores.Select(u => new GestorSelectorDto(u.Id, u.NombreCompleto)).ToList();

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
        _lineas = await Mediator.Send(new ObtenerLineasWhatsAppQuery());
    }

    private string DescribirPropietario(ConexionIntegracionListaDto conexion) => conexion switch
    {
        { GestorPropietarioId: { } gestorId } => $"Personal de {_gestores.FirstOrDefault(g => g.Id == gestorId)?.NombreCompleto ?? "—"}",
        { ClienteNombre: { } clienteNombre } => clienteNombre,
        _ => "Tenant propio"
    };

    private string DescribirAsignacion(LineaWhatsAppListaDto linea) => linea.Modo switch
    {
        ModoAsignacionLinea.GestorFijo =>
            $"Gestor fijo: {_gestores.FirstOrDefault(g => g.Id == linea.ComercialAsignadoId)?.NombreCompleto ?? "—"}",
        _ => $"Pool inbound ({linea.MiembrosPool.Count} gestores)",
    };

    private void AbrirAltaLinea()
    {
        _lineaEnEdicion = null;
        _lineaNombre = _lineaNumero = _lineaPhoneNumberId = _lineaWabaId = _lineaToken = string.Empty;
        _lineaModo = ModoAsignacionLinea.GestorFijo;
        _lineaComercialId = null;
        _lineaMiembros.Clear();
        _lineaClienteId = null;
        _lineaMensajeAutoTriage = string.Empty;
        _modalLineaVisible = true;
    }

    private void AbrirEdicionLinea(LineaWhatsAppListaDto linea)
    {
        _lineaEnEdicion = linea;
        _lineaToken = string.Empty;
        _lineaModo = linea.Modo;
        _lineaComercialId = linea.ComercialAsignadoId;
        _lineaMiembros.Clear();
        foreach (var miembro in linea.MiembrosPool) _lineaMiembros.Add(miembro);
        _lineaMensajeAutoTriage = linea.MensajeAutoTriage ?? string.Empty;
        _modalLineaVisible = true;
    }

    private void CerrarModalLinea()
    {
        if (_guardandoLinea) return;
        _modalLineaVisible = false;
        _lineaEnEdicion = null;
    }

    private void AlternarMiembroPool(Guid usuarioId, bool marcado)
    {
        if (marcado) _lineaMiembros.Add(usuarioId);
        else _lineaMiembros.Remove(usuarioId);
    }

    private async Task GuardarLineaAsync()
    {
        _guardandoLinea = true;
        StateHasChanged();

        try
        {
            var mensajeAutoTriage = string.IsNullOrWhiteSpace(_lineaMensajeAutoTriage) ? null : _lineaMensajeAutoTriage;
            var resultadoError = _lineaEnEdicion is null
                ? (await Mediator.Send(new CrearLineaWhatsAppCommand(
                    _lineaNombre, _lineaNumero, _lineaPhoneNumberId, _lineaWabaId, _lineaToken, _lineaModo,
                    _lineaComercialId, _lineaMiembros.ToList(), _lineaClienteId, mensajeAutoTriage)))
                    is { EsFallido: true } fallosAlta ? fallosAlta.Error : null
                : (await Mediator.Send(new ActualizarLineaWhatsAppCommand(
                    _lineaEnEdicion.LineaId, _lineaEnEdicion.Version,
                    string.IsNullOrWhiteSpace(_lineaToken) ? null : _lineaToken, _lineaModo,
                    _lineaComercialId, _lineaMiembros.ToList(), mensajeAutoTriage)))
                    is { EsFallido: true } fallosEdicion ? fallosEdicion.Error : null;

            if (resultadoError is not null)
            {
                ToastService.Mostrar(resultadoError.Mensaje, TonoToast.Error);
                return;
            }

            ToastService.Mostrar(_lineaEnEdicion is null ? "Línea creada." : "Línea actualizada.", TonoToast.Exito);
            _modalLineaVisible = false;
            _lineaEnEdicion = null;
            await CargarConexionesAsync();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error al guardar la línea de WhatsApp.");
            ToastService.Mostrar("No pudimos guardar la línea.", TonoToast.Error);
        }
        finally
        {
            _guardandoLinea = false;
        }
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
