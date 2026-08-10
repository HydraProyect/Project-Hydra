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

    /// <summary>
    /// Ningún tono del semáforo de cumplimiento (Peligro/Advertencia/Éxito) se usa aquí — están
    /// reservados en exclusiva para vigencia documental (02_BRAND_AND_VISUAL_IDENTITY.md § 3.4,
    /// DDL-010) y nunca son decorativos en otro contexto. "Posible phishing" se distingue por el
    /// texto de la etiqueta, no por el color.
    /// </summary>
    private static (string Etiqueta, TonoBadge Tono)? DescribirBadge(MensajeBuzonPersonalDto mensaje) => mensaje switch
    {
        { ProveedorPlataformaCaeNombre: { } proveedor } => ($"Plataforma reconocida: {proveedor}", TonoBadge.Info),
        { Motivo: MotivoRuidoMensaje.CorreoInterno } => ("Correo interno", TonoBadge.Neutro),
        { Motivo: MotivoRuidoMensaje.PosiblePhishing } => ("Posible phishing", TonoBadge.Neutro),
        _ => null
    };
}
