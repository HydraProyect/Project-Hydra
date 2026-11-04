using CaeManager.Application.Comercial.Common;
using CaeManager.Application.Common;

namespace CaeManager.Application.Tests.Comercial;

/// <summary>
/// Doble de <see cref="IBloqueoSuscripcionStripe"/> y de <see cref="IDescarteCambiosPendientes"/>,
/// con un único registro de <see cref="Eventos"/> para fijar el orden: candado, descarte del
/// contexto y lectura. Los ganchos simulan lo que otra transacción confirma mientras se espera
/// cada candado. Que el candado serialice de verdad en PostgreSQL lo prueba
/// <c>BloqueoSuscripcionStripeTests</c> en la integración.
/// </summary>
public class BloqueoSuscripcionStripeFalso : IBloqueoSuscripcionStripe, IDescarteCambiosPendientes
{
    public List<string> Eventos { get; } = [];
    public List<Guid> Tenants { get; } = [];
    public List<string> Suscripciones { get; } = [];
    public Action? AlBloquearTenant { get; init; }
    public Action? AlBloquearSuscripciones { get; init; }

    /// <summary>Simula que otro procesamiento tiene el candado de la Subscription.</summary>
    public bool Ocupada { get; init; }

    public Task BloquearTenantAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        Tenants.Add(tenantId);
        Eventos.Add("tenant");
        AlBloquearTenant?.Invoke();
        return Task.CompletedTask;
    }

    public Task BloquearSuscripcionesAsync(IReadOnlyCollection<string> idsSuscripcion, CancellationToken cancellationToken = default)
    {
        Suscripciones.AddRange(idsSuscripcion);
        Eventos.Add("suscripcion");
        AlBloquearSuscripciones?.Invoke();
        return Task.CompletedTask;
    }

    public Task<bool> IntentarBloquearSuscripcionAsync(string idSuscripcion, CancellationToken cancellationToken = default)
    {
        Eventos.Add("intento");
        if (Ocupada)
            return Task.FromResult(false);

        Suscripciones.Add(idSuscripcion);
        AlBloquearSuscripciones?.Invoke();
        return Task.FromResult(true);
    }

    public void DescartarCambiosPendientes() => Eventos.Add("descarte");
}
