using CaeManager.Application.Comercial.Commands.ActualizarEstadoComercialTenant;
using CaeManager.Application.Comercial.Commands.RegistrarSuscripcionTenant;
using CaeManager.Application.Comercial.Queries.ObtenerEstadoComercialTenants;
using CaeManager.Web.Components.DesignSystem;
using MediatR;
using Microsoft.AspNetCore.Components;

namespace CaeManager.Web.Features.Comercial.Pages;

/// <summary>
/// Panel interno de plataforma (Horizonte 1.7, "Billing mínimo viable") —
/// no es autoservicio: el operador (hoy solo Christopher) crea la
/// Subscription en Stripe a mano vía Payment Link y aquí solo registra su
/// Id. Mismo criterio de alcance que ClavesApi.razor (P3-29): panel interno,
/// nunca gestionado por el propio tenant cliente.
/// </summary>
public partial class EstadoComercial : CaeManager.Web.Components.PaginaIntegrableConfiguracionBase
{
    [Inject] private IMediator Mediator { get; set; } = default!;
    [Inject] private ToastService ToastService { get; set; } = default!;
    [Inject] private ILogger<EstadoComercial> Logger { get; set; } = default!;

    private IReadOnlyList<EstadoComercialTenantDto> _tenants = [];
    private bool _cargando = true;

    private EstadoComercialTenantDto? _tenantAVincular;
    private string _stripeSubscriptionId = string.Empty;
    private bool _vinculando;
    private string? _errorVincular;

    private Guid? _actualizandoTenantId;

    protected override Task OnInitializedAsync() => CargarAsync();

    private async Task CargarAsync()
    {
        _cargando = true;
        StateHasChanged();

        try
        {
            _tenants = await Mediator.Send(new ObtenerEstadoComercialTenantsQuery());
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error al cargar el estado comercial de los tenants.");
            ToastService.Mostrar("No pudimos cargar el estado comercial.", TonoToast.Error);
        }
        finally
        {
            _cargando = false;
        }
    }

    private void AbrirVincular(EstadoComercialTenantDto tenant)
    {
        _tenantAVincular = tenant;
        _stripeSubscriptionId = string.Empty;
        _errorVincular = null;
    }

    private async Task VincularAsync()
    {
        if (_tenantAVincular is null) return;

        _vinculando = true;
        _errorVincular = null;
        StateHasChanged();

        try
        {
            var resultado = await Mediator.Send(
                new RegistrarSuscripcionTenantCommand(_tenantAVincular.TenantId, _stripeSubscriptionId.Trim()));

            if (resultado.EsFallido)
            {
                _errorVincular = resultado.Error.Mensaje;
                return;
            }

            _tenantAVincular = null;
            ToastService.Mostrar("Suscripción vinculada.", TonoToast.Exito);
            await CargarAsync();
        }
        finally
        {
            _vinculando = false;
        }
    }

    private async Task ActualizarDesdeStripeAsync(EstadoComercialTenantDto tenant)
    {
        _actualizandoTenantId = tenant.TenantId;
        StateHasChanged();

        try
        {
            var resultado = await Mediator.Send(new ActualizarEstadoComercialTenantCommand(tenant.TenantId));

            if (resultado.EsFallido)
            {
                ToastService.Mostrar(resultado.Error.Mensaje, TonoToast.Error);
                return;
            }

            ToastService.Mostrar("Estado comercial actualizado.", TonoToast.Exito);
            await CargarAsync();
        }
        finally
        {
            _actualizandoTenantId = null;
        }
    }
}
