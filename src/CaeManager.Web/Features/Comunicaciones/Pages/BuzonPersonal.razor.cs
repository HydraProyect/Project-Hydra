using CaeManager.Application.Comunicaciones.Queries.ObtenerMensajesBuzonPersonal;
using CaeManager.Domain.Comunicaciones;
using CaeManager.Web.Components.DesignSystem;
using MediatR;
using Microsoft.AspNetCore.Components;

namespace CaeManager.Web.Features.Comunicaciones.Pages;

/// <summary>
/// Buzón personal del gestor (ronda de reducción de ruido en Comunicaciones) — lista plana de sus
/// propios mensajes entrantes (DocuSign, correo interno, posible phishing), nunca mezclada con
/// conversaciones de Cliente. Vista de solo lectura: no hay Composer ni Action Center, no hay
/// ningún Cliente al que asociar una gestión.
/// </summary>
public partial class BuzonPersonal : ComponentBase
{
    [Inject] private IMediator Mediator { get; set; } = default!;
    [Inject] private ILogger<BuzonPersonal> Logger { get; set; } = default!;

    private bool _cargando = true;
    private bool _error;
    private IReadOnlyList<MensajeBuzonPersonalDto> _mensajes = [];
    private Guid? _mensajeExpandidoId;

    protected override async Task OnInitializedAsync()
    {
        try
        {
            _mensajes = await Mediator.Send(new ObtenerMensajesBuzonPersonalQuery());
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error al cargar el buzón personal.");
            _error = true;
        }
        finally
        {
            _cargando = false;
        }
    }

    private void AlternarExpandido(Guid mensajeId) =>
        _mensajeExpandidoId = _mensajeExpandidoId == mensajeId ? null : mensajeId;

    private static (string Etiqueta, TonoBadge Tono)? DescribirBadge(MensajeBuzonPersonalDto mensaje) => mensaje switch
    {
        { ProveedorPlataformaCaeNombre: { } proveedor } => ($"Plataforma reconocida: {proveedor}", TonoBadge.Info),
        { Motivo: MotivoRuidoMensaje.CorreoInterno } => ("Correo interno", TonoBadge.Neutro),
        { Motivo: MotivoRuidoMensaje.PosiblePhishing } => ("Posible phishing", TonoBadge.Peligro),
        _ => null
    };
}
