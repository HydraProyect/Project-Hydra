using System.Text.RegularExpressions;
using CaeManager.Application.Clientes.Queries.ObtenerClientesParaSelector;
using CaeManager.Application.Common;
using CaeManager.Application.Comunicaciones.Commands.AsignarClienteConversacion;
using CaeManager.Application.Comunicaciones.Commands.AsignarEjecutivoConversacion;
using CaeManager.Application.Comunicaciones.Commands.CambiarEstadoConversacion;
using CaeManager.Application.Comunicaciones.Commands.ResponderConversacionWhatsApp;
using CaeManager.Application.Comunicaciones.Eventos;
using CaeManager.Application.Comunicaciones.Queries.ObtenerConversacionPorId;
using CaeManager.Application.Comunicaciones.Queries.ObtenerConversaciones;
using CaeManager.Domain.Comunicaciones;
using CaeManager.Infrastructure.Autorizacion;
using CaeManager.Infrastructure.Comunicaciones;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Components.DesignSystem;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.Options;

namespace CaeManager.Web.Features.Comunicaciones.Pages;

/// <summary>
/// Chat en vivo del canal WhatsApp — master-detail estilo mensajería, a
/// diferencia del flujo formal de la Bandeja de correo. Se suscribe a
/// <see cref="INotificadorMensajesTiempoReal"/> para refrescar al instante
/// cuando la ingesta de fondo persiste un mensaje nuevo (primer pub-sub
/// job→circuito del proyecto).
/// </summary>
public partial class Chat : ComponentBase, IDisposable
{
    [Inject] private IMediator Mediator { get; set; } = default!;
    [Inject] private ToastService ToastService { get; set; } = default!;
    [Inject] private ILogger<Chat> Logger { get; set; } = default!;
    [Inject] private INotificadorMensajesTiempoReal Notificador { get; set; } = default!;
    [Inject] private ITenantActual TenantActual { get; set; } = default!;
    [Inject] private DirectorioUsuariosTenant DirectorioUsuarios { get; set; } = default!;
    [Inject] private IOptions<ComunicacionesOptions> OpcionesComunicaciones { get; set; } = default!;
    [Inject] private NavigationManager NavigationManager { get; set; } = default!;

    private IReadOnlyList<ConversacionListaDto> _conversaciones = [];
    private IReadOnlyList<ClienteSelectorDto> _clientesSelector = [];
    private IReadOnlyList<EjecutivoSelectorDto> _gestores = [];
    private bool _cargandoLista = true;
    private string _filtro = string.Empty;

    private Guid? _conversacionSeleccionadaId;
    private ConversacionDetalleDto? _detalle;

    private string _textoRespuesta = string.Empty;
    private bool _enviando;
    private string _clienteTriageSeleccionado = string.Empty;
    private bool _asignandoCliente;

    private IDisposable? _suscripcionTiempoReal;

    private bool VentanaAbierta =>
        _detalle?.FechaUltimoMensajeEntranteUtc is { } ultimo &&
        DateTime.UtcNow - ultimo < ConversacionCorreo.DuracionVentanaServicio;

    private string CierreVentanaLocal =>
        _detalle?.FechaUltimoMensajeEntranteUtc is { } ultimo
            ? ultimo.Add(ConversacionCorreo.DuracionVentanaServicio).ToLocalTime().ToString("dd/MM 'a las' HH:mm")
            : string.Empty;

    protected override async Task OnInitializedAsync()
    {
        // Mismo interruptor que Bandeja (módulo congelado por defecto).
        if (!OpcionesComunicaciones.Value.Activo)
        {
            NavigationManager.NavigateTo("/not-found");
            return;
        }

        _clientesSelector = await Mediator.Send(new ObtenerClientesParaSelectorQuery());
        var gestores = await DirectorioUsuarios.ObtenerVisiblesEnRolAsync(Roles.GestorCae);
        _gestores = gestores.Select(u => new EjecutivoSelectorDto(u.Id, u.NombreCompleto)).ToList();

        // La suscripción es del tenant del circuito: los avisos de otros
        // tenants nunca llegan aquí (el notificador ya segrega por tenant).
        if (TenantActual.TenantId is { } tenantId)
            _suscripcionTiempoReal = Notificador.Suscribir(tenantId, AlRecibirMensajeAsync);

        await CargarListaAsync();
    }

    public void Dispose() => _suscripcionTiempoReal?.Dispose();

    /// <summary>Llega desde el hilo del job de fondo — todo lo que toque estado del componente va dentro de InvokeAsync.</summary>
    private Task AlRecibirMensajeAsync(MensajeWhatsAppRecibidoEvent aviso) =>
        InvokeAsync(async () =>
        {
            try
            {
                await CargarListaAsync();
                if (aviso.ConversacionId == _conversacionSeleccionadaId)
                    await CargarDetalleAsync();
                StateHasChanged();
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "No se pudo refrescar el chat al recibir un aviso de tiempo real.");
            }
        });

    private async Task CargarListaAsync()
    {
        try
        {
            _conversaciones = await Mediator.Send(new ObtenerConversacionesQuery(
                SoloAsignadasAMi: _filtro == "mias",
                SoloSinAsignar: _filtro == "sin-asignar",
                Canal: CanalConversacion.WhatsApp));

            if (_filtro == "triage")
                _conversaciones = _conversaciones.Where(c => c.ClienteId is null).ToList();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error al cargar la lista de chats de WhatsApp.");
            ToastService.Mostrar("No pudimos cargar los chats.", TonoToast.Error);
        }
        finally
        {
            _cargandoLista = false;
        }
    }

    private async Task CambiarFiltroAsync(string filtro)
    {
        _filtro = filtro;
        await CargarListaAsync();
    }

    private async Task SeleccionarAsync(Guid conversacionId)
    {
        _conversacionSeleccionadaId = conversacionId;
        _clienteTriageSeleccionado = string.Empty;
        await CargarDetalleAsync();
    }

    private async Task CargarDetalleAsync()
    {
        if (_conversacionSeleccionadaId is not { } id) return;

        try
        {
            _detalle = await Mediator.Send(new ObtenerConversacionPorIdQuery(id));
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error al cargar el detalle del chat {ConversacionId}.", id);
            ToastService.Mostrar("No pudimos cargar la conversación.", TonoToast.Error);
        }
    }

    private async Task EnviarAsync()
    {
        if (_detalle is null || string.IsNullOrWhiteSpace(_textoRespuesta)) return;

        _enviando = true;
        try
        {
            var resultado = await Mediator.Send(new ResponderConversacionWhatsAppCommand(_detalle.Id, _textoRespuesta.Trim()));
            if (resultado.EsFallido)
            {
                ToastService.Mostrar(resultado.Error.Mensaje, TonoToast.Error);
                return;
            }

            _textoRespuesta = string.Empty;
            await CargarDetalleAsync();
            await CargarListaAsync();
        }
        finally
        {
            _enviando = false;
        }
    }

    /// <summary>Enter envía, Shift+Enter hace salto de línea — convención de chat.</summary>
    private async Task ManejarTeclaComposer(KeyboardEventArgs e)
    {
        if (e.Key == "Enter" && !e.ShiftKey)
            await EnviarAsync();
    }

    private async Task AsignarClienteAsync()
    {
        if (_detalle is null || !Guid.TryParse(_clienteTriageSeleccionado, out var clienteId)) return;

        _asignandoCliente = true;
        try
        {
            var resultado = await Mediator.Send(new AsignarClienteConversacionCommand(_detalle.Id, clienteId));
            if (resultado.EsFallido)
            {
                ToastService.Mostrar(resultado.Error.Mensaje, TonoToast.Error);
                return;
            }

            ToastService.Mostrar("Cliente asignado — el teléfono queda memorizado para el enrutamiento.", TonoToast.Exito);
            await CargarDetalleAsync();
            await CargarListaAsync();
        }
        finally
        {
            _asignandoCliente = false;
        }
    }

    private async Task CambiarEjecutivoAsync(string valor)
    {
        if (_detalle is null) return;

        var resultado = await Mediator.Send(new AsignarEjecutivoConversacionCommand(
            _detalle.Id, Guid.TryParse(valor, out var id) ? id : null));
        if (resultado.EsFallido)
        {
            ToastService.Mostrar(resultado.Error.Mensaje, TonoToast.Error);
            return;
        }

        await CargarDetalleAsync();
        await CargarListaAsync();
    }

    private async Task CambiarEstadoAsync(string valor)
    {
        if (_detalle is null || !Enum.TryParse<EstadoConversacion>(valor, out var estado)) return;

        var resultado = await Mediator.Send(new CambiarEstadoConversacionCommand(_detalle.Id, estado));
        if (resultado.EsFallido)
        {
            ToastService.Mostrar(resultado.Error.Mensaje, TonoToast.Error);
            return;
        }

        await CargarDetalleAsync();
        await CargarListaAsync();
    }

    private static string FormatearHora(DateTime fechaUtc)
    {
        var local = fechaUtc.ToLocalTime();
        return local.Date == DateTime.Now.Date ? local.ToString("HH:mm") : local.ToString("dd/MM");
    }

    /// <summary>Los mensajes WhatsApp son texto plano, pero pasan por el mismo saneado HTML del correo — se limpia cualquier resto de marcado para pintar burbujas de texto.</summary>
    private static string QuitarHtml(string cuerpoHtml)
    {
        var texto = Regex.Replace(cuerpoHtml, "<.*?>", " ");
        return System.Net.WebUtility.HtmlDecode(Regex.Replace(texto, "\\s+", " ").Trim());
    }

    private static string TicksEstadoEntrega(MensajeDetalleDto mensaje) => mensaje.EstadoEntrega switch
    {
        EstadoEntregaMensaje.Enviado => "✓",
        EstadoEntregaMensaje.Entregado or EstadoEntregaMensaje.Leido => "✓✓",
        EstadoEntregaMensaje.Fallido => "!",
        _ => string.Empty,
    };

    private static string DescribirEstadoEntrega(MensajeDetalleDto mensaje) => mensaje.EstadoEntrega switch
    {
        EstadoEntregaMensaje.Enviado => "Enviado",
        EstadoEntregaMensaje.Entregado => "Entregado",
        EstadoEntregaMensaje.Leido => "Leído",
        EstadoEntregaMensaje.Fallido => "No entregado",
        _ => string.Empty,
    };
}
