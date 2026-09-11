using CaeManager.Application.Comercial.Commands.ActualizarEstadoComercialTenant;
using CaeManager.Application.Comercial.Commands.RegistrarSuscripcionTenant;
using CaeManager.Application.Comercial.Queries.ObtenerEstadoComercialTenants;
using CaeManager.Web.Components.DesignSystem;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace CaeManager.Web.Features.Comercial.Pages;

/// <summary>
/// Panel interno de plataforma (Horizonte 1.7, "Billing mínimo viable") —
/// no es autoservicio: la suscripción se crea en Stripe fuera de TALVEG y
/// aquí solo se registra su Id. Mismo criterio de alcance que ClavesApi.razor
/// (P3-29): panel interno, nunca gestionado por el propio tenant.
///
/// <para>
/// <b>Plano comercial, y solo ese.</b> Cada fila es un <c>Tenant</c>: el
/// Tenant beneficiario de la suscripción, el tenant cuyas escrituras bloquea
/// <c>GateComercialTenantBehavior</c> en «Solo lectura» y «Suspendida». Quién
/// paga (el Pagador TALVEG) es el Customer de Stripe: TALVEG solo guarda su
/// identificador (<c>StripeCustomerId</c>), no lo pinta y nunca lo deduce del
/// tenant. El agregado <c>Tenant</c> no lleva plan, importe ni fecha de
/// renovación, y no existe ninguna entidad Producto Contratado —solo el valor
/// <c>OrigenInstruccionTratamientoIa.ProductoContratado</c>, que la anticipa—:
/// la pantalla no inventa ninguno de esos datos.
/// </para>
///
/// <para>
/// <b>Escrituras.</b> Las dos que ya existían —vincular y resincronizar— y
/// ninguna más. Las dos pasan por <see cref="DialogoConfirmacion"/> con su
/// efecto real (el estado resultante puede dejar al tenant sin escritura) y
/// llevan guarda propia contra el doble clic, además de la del diálogo.
/// </para>
///
/// <para>
/// <b>Carreras.</b> La carga captura <see cref="_versionCarga"/> antes del
/// <c>await</c> y descarta la respuesta si entretanto se pidió otra. Al
/// retirar el componente se cancela la consulta en vuelo y ninguna respuesta
/// tardía toca el estado.
/// </para>
/// </summary>
public partial class EstadoComercial : CaeManager.Web.Components.PaginaIntegrableConfiguracionBase, IAsyncDisposable
{
    private const string PrefijoSuscripcionStripe = "sub_";

    [Inject] private IMediator Mediator { get; set; } = default!;
    [Inject] private ToastService ToastService { get; set; } = default!;
    [Inject] private ILogger<EstadoComercial> Logger { get; set; } = default!;
    [Inject] private IJSRuntime JsRuntime { get; set; } = default!;

    private readonly CancellationTokenSource _ciclo = new();
    private bool _desechado;
    private IJSObjectReference? _moduloPortapapeles;

    private IReadOnlyList<EstadoComercialTenantDto> _tenants = [];
    private bool _cargando;
    private bool _errorCarga;

    /// <summary>
    /// La lista ya se pintó al menos una vez. Mientras sea cierto, una recarga
    /// (tras vincular o actualizar) deja la tabla a la vista en vez de volver
    /// al esqueleto.
    /// </summary>
    private bool _listaPintada;

    private int _versionCarga;

    private Guid? _idEnfocado;

    private EstadoComercialTenantDto? _tenantAVincular;
    private bool _confirmandoVincular;
    private string _stripeSubscriptionId = string.Empty;
    private bool _vinculando;
    private string? _errorVincular;

    private EstadoComercialTenantDto? _tenantAActualizar;
    private Guid? _actualizandoTenantId;

    private bool FormularioVincularVisible => _tenantAVincular is not null && !_confirmandoVincular;

    private bool ConfirmacionVincularVisible => _tenantAVincular is not null && _confirmandoVincular;

    private string MensajeConfirmacionVincular => _tenantAVincular is null
        ? string.Empty
        : $"Se vinculará {_stripeSubscriptionId.Trim()} al tenant {_tenantAVincular.Nombre}. TALVEG consultará esa " +
          "suscripción en Stripe, guardará quién la paga según Stripe y aplicará el estado comercial que Stripe " +
          $"devuelva: si es «Solo lectura» o «Suspendida», {_tenantAVincular.Nombre} dejará de poder escribir. " +
          "Esta pantalla no ofrece desvincularla después.";

    private string MensajeConfirmacionActualizar => _tenantAActualizar is null
        ? string.Empty
        : $"TALVEG consultará ahora en Stripe la suscripción {_tenantAActualizar.StripeSubscriptionId} y aplicará a " +
          $"{_tenantAActualizar.Nombre} el estado que devuelva. Si Stripe la da por impagada o cancelada, " +
          $"{_tenantAActualizar.Nombre} pasará a «Solo lectura» o «Suspendida» y dejará de poder escribir.";

    protected override Task OnInitializedAsync() => CargarAsync();

    public async ValueTask DisposeAsync()
    {
        _desechado = true;
        _ciclo.Cancel();
        _ciclo.Dispose();

        if (_moduloPortapapeles is not null)
        {
            try
            {
                await _moduloPortapapeles.DisposeAsync();
            }
            catch (JSDisconnectedException)
            {
            }
        }
    }

    // ---------------------------------------------------------------- lista

    private Task ReintentarAsync() => CargarAsync();

    private async Task CargarAsync()
    {
        if (_desechado) return;

        var version = ++_versionCarga;
        _cargando = true;
        _errorCarga = false;

        IReadOnlyList<EstadoComercialTenantDto> tenants;
        try
        {
            tenants = await Mediator.Send(new ObtenerEstadoComercialTenantsQuery(), _ciclo.Token);
        }
        catch (Exception ex)
        {
            if (_desechado || version != _versionCarga) return;

            Logger.LogError(ex, "Error al cargar el estado comercial de los tenants.");
            _errorCarga = true;
            _listaPintada = false;
            _cargando = false;
            return;
        }

        if (_desechado || version != _versionCarga) return;

        _tenants = tenants;
        if (_idEnfocado is { } enfocado && tenants.All(t => t.TenantId != enfocado))
            _idEnfocado = null;

        _listaPintada = true;
        _cargando = false;
    }

    private Task ManejarAtajoAsync(string tecla)
    {
        if (_tenants.Count == 0) return Task.CompletedTask;

        var indiceActual = _idEnfocado is { } id ? IndiceDe(id) : -1;
        switch (tecla)
        {
            case "j":
                _idEnfocado = _tenants[Math.Min(indiceActual + 1, _tenants.Count - 1)].TenantId;
                break;
            case "k":
                _idEnfocado = _tenants[Math.Max(indiceActual - 1, 0)].TenantId;
                break;
            default:
                return Task.CompletedTask;
        }

        StateHasChanged();
        return Task.CompletedTask;
    }

    private int IndiceDe(Guid tenantId)
    {
        for (var i = 0; i < _tenants.Count; i++)
        {
            if (_tenants[i].TenantId == tenantId) return i;
        }

        return -1;
    }

    private static string TextoActualizado(EstadoComercialTenantDto tenant) =>
        tenant.EstadoComercialActualizadoEnUtc?.ToLocalTime().ToString("dd/MM/yyyy HH:mm") ?? "—";

    private async Task CopiarIdAsync(string idSuscripcion)
    {
        try
        {
            _moduloPortapapeles ??= await JsRuntime.InvokeAsync<IJSObjectReference>("import", "./js/clipboard.js");
            await _moduloPortapapeles.InvokeVoidAsync("copiarAlPortapapeles", idSuscripcion);
            ToastService.Mostrar("Se copió el id de suscripción al portapapeles.", TonoToast.Exito);
        }
        catch (Exception)
        {
            ToastService.Mostrar("No pudimos copiar al portapapeles.", TonoToast.Error);
        }
    }

    // ---------------------------------------------------------------- vincular

    private void AbrirVincular(EstadoComercialTenantDto tenant)
    {
        if (_vinculando) return;

        _tenantAVincular = tenant;
        _confirmandoVincular = false;
        _stripeSubscriptionId = string.Empty;
        _errorVincular = null;
    }

    private void CambiarIdSuscripcion(string valor)
    {
        _stripeSubscriptionId = valor;
        _errorVincular = null;
    }

    private void CerrarFormularioVincular(bool visible)
    {
        if (visible || _vinculando) return;

        _tenantAVincular = null;
        _confirmandoVincular = false;
        _errorVincular = null;
    }

    /// <summary>
    /// Comprobación de forma, solo de pantalla: el validador del comando ya
    /// anuncia que el Id «empieza por sub_», y Stripe no lo reconocería de
    /// otra manera. Evita abrir la confirmación para algo que no puede ser una
    /// suscripción. La verdad sobre si existe la sigue diciendo Stripe.
    /// </summary>
    private void PedirConfirmacionVincular()
    {
        if (_tenantAVincular is null || _vinculando) return;

        if (!_stripeSubscriptionId.Trim().StartsWith(PrefijoSuscripcionStripe, StringComparison.Ordinal))
        {
            _errorVincular = "El identificador debe empezar por sub_. Cópialo del panel de Stripe.";
            return;
        }

        _errorVincular = null;
        _confirmandoVincular = true;
    }

    /// <summary>Cancelar la confirmación devuelve al formulario con lo que se había escrito.</summary>
    private void CerrarConfirmacionVincular(bool visible)
    {
        if (visible || _vinculando) return;

        _confirmandoVincular = false;
    }

    private async Task VincularAsync()
    {
        if (_tenantAVincular is not { } tenant || _vinculando) return;

        _vinculando = true;
        _errorVincular = null;

        try
        {
            var resultado = await Mediator.Send(
                new RegistrarSuscripcionTenantCommand(tenant.TenantId, _stripeSubscriptionId.Trim()));

            if (_desechado) return;

            if (resultado.EsFallido)
            {
                _errorVincular = resultado.Error.Mensaje;
                _confirmandoVincular = false;
                return;
            }

            _tenantAVincular = null;
            _confirmandoVincular = false;
            ToastService.Mostrar("Suscripción vinculada.", TonoToast.Exito);
        }
        catch (Exception ex)
        {
            if (_desechado) return;

            Logger.LogError(ex, "Error al vincular la suscripción de Stripe del tenant {TenantId}.", tenant.TenantId);
            _errorVincular = "No pudimos completar la vinculación. Vuelve a cargar la lista para ver en qué estado quedó.";
            _confirmandoVincular = false;
            return;
        }
        finally
        {
            _vinculando = false;
        }

        await CargarAsync();
    }

    // ---------------------------------------------------------------- actualizar

    private void PedirConfirmacionActualizar(EstadoComercialTenantDto tenant)
    {
        if (_actualizandoTenantId is not null) return;

        _tenantAActualizar = tenant;
    }

    private void CerrarConfirmacionActualizar(bool visible)
    {
        if (visible || _actualizandoTenantId is not null) return;

        _tenantAActualizar = null;
    }

    private async Task ActualizarDesdeStripeAsync()
    {
        if (_tenantAActualizar is not { } tenant || _actualizandoTenantId is not null) return;

        _actualizandoTenantId = tenant.TenantId;

        try
        {
            var resultado = await Mediator.Send(new ActualizarEstadoComercialTenantCommand(tenant.TenantId));

            if (_desechado) return;

            if (resultado.EsFallido)
            {
                ToastService.Mostrar(resultado.Error.Mensaje, TonoToast.Error);
                return;
            }

            ToastService.Mostrar("Estado comercial actualizado.", TonoToast.Exito);
        }
        catch (Exception ex)
        {
            if (_desechado) return;

            Logger.LogError(ex, "Error al actualizar desde Stripe el estado comercial del tenant {TenantId}.", tenant.TenantId);
            ToastService.Mostrar("No pudimos actualizar el estado comercial. Vuelve a cargar la lista para ver en qué estado quedó.", TonoToast.Error);
            return;
        }
        finally
        {
            _actualizandoTenantId = null;
            _tenantAActualizar = null;
        }

        await CargarAsync();
    }
}
