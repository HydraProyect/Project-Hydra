using CaeManager.Application.Operaciones.IncorporacionCartera.Commands;
using CaeManager.Application.Operaciones.IncorporacionCartera.Queries;
using CaeManager.Domain.Operaciones;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.IncorporacionCartera.Recursos;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;

namespace CaeManager.Web.Features.IncorporacionCartera.Components;

/// <summary>
/// Diálogo con el que un Gestor CAE pide incorporarse a una Empresa (Tenant
/// propietario) que su Operador CAE opera y que aún no tiene en cartera.
/// Reutilizable: lo abre la bandeja de solicitudes y lo abrirá el botón
/// «Añadir a mi cartera» de Mi trabajo, que puede preseleccionar la Empresa
/// con <see cref="TenantPropietarioId"/>. Los candidatos se piden al abrir:
/// la lista es de la organización y cambia sin que este circuito se entere.
/// El mensaje se pinta como texto (Blazor lo escapa), nunca como marcado.
/// </summary>
public partial class DialogoSolicitarIncorporacion : ComponentBase
{
    [Inject] private IMediator Mediator { get; set; } = default!;
    [Inject] private ToastService Toasts { get; set; } = default!;
    [Inject] private IStringLocalizer<TextosIncorporacionCartera> Textos { get; set; } = default!;

    [Parameter] public bool Visible { get; set; }
    [Parameter] public EventCallback<bool> VisibleChanged { get; set; }

    /// <summary>Empresa preseleccionada, si quien abre el diálogo ya sabe cuál es.</summary>
    [Parameter] public Guid? TenantPropietarioId { get; set; }

    /// <summary>Se invoca con el Id de la solicitud creada.</summary>
    [Parameter] public EventCallback<Guid> OnSolicitada { get; set; }

    private IReadOnlyList<CandidatoIncorporacionCarteraDto> _candidatos = [];
    private string _tenantSeleccionado = string.Empty;
    private string _mensaje = string.Empty;
    private string? _error;
    private bool _cargando;
    private bool _enviando;
    private bool _visibleAnterior;

    private int LongitudMensaje => _mensaje.Trim().Length;

    private bool MensajeDemasiadoLargo => LongitudMensaje > SolicitudIncorporacionCartera.LongitudMaximaMensaje;

    private CandidatoIncorporacionCarteraDto? Seleccionado =>
        Guid.TryParse(_tenantSeleccionado, out var id) ? _candidatos.FirstOrDefault(c => c.TenantId == id) : null;

    private bool TienePendiente => Seleccionado?.SolicitudPendienteId is not null;

    private bool PuedeEnviar =>
        !_enviando && Seleccionado is not null && !TienePendiente && LongitudMensaje > 0 && !MensajeDemasiadoLargo;

    protected override async Task OnParametersSetAsync()
    {
        // Se recarga en cada apertura, no en cada render.
        if (Visible && !_visibleAnterior)
        {
            _visibleAnterior = true;
            await CargarAsync();
        }
        else if (!Visible)
        {
            _visibleAnterior = false;
        }
    }

    private async Task CargarAsync()
    {
        _cargando = true;
        _mensaje = string.Empty;
        _error = null;
        _candidatos = [];

        var resultado = await Mediator.Send(new ObtenerCandidatosIncorporacionCarteraQuery());
        _cargando = false;

        if (resultado.EsFallido)
        {
            Toasts.Mostrar(TextosIncorporacionCartera.MensajeDeError(Textos, resultado.Error), TonoToast.Error);
            return;
        }

        _candidatos = resultado.Valor;
        _tenantSeleccionado = TenantPropietarioId is { } id && _candidatos.Any(c => c.TenantId == id)
            ? id.ToString()
            : _candidatos.Count == 1 ? _candidatos[0].TenantId.ToString() : string.Empty;
    }

    private void CambiarEmpresa(string valor)
    {
        _tenantSeleccionado = valor;
        _error = null;
    }

    private async Task EnviarAsync()
    {
        if (!PuedeEnviar || Seleccionado is not { } candidato)
            return;

        _enviando = true;
        _error = null;
        try
        {
            var resultado = await Mediator.Send(new SolicitarIncorporacionCarteraCommand(candidato.TenantId, _mensaje));
            if (resultado.EsFallido)
            {
                _error = TextosIncorporacionCartera.MensajeDeError(Textos, resultado.Error);
                return;
            }

            Toasts.Mostrar(Textos["ToastSolicitada"], TonoToast.Exito);
            await OnSolicitada.InvokeAsync(resultado.Valor);
            await CerrarAsync();
        }
        finally
        {
            _enviando = false;
        }
    }

    private async Task CerrarAsync()
    {
        _visibleAnterior = false;
        await VisibleChanged.InvokeAsync(false);
    }
}
