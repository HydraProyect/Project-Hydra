using CaeManager.Application.Comercial.Common;

namespace CaeManager.Application.Tests.Comercial;

/// <summary>
/// Doble de <see cref="IBloqueoSuscripcionStripe"/>: anota qué se bloqueó y en qué orden, y deja
/// simular lo que otra transacción confirma mientras se esperaba el candado
/// (<see cref="AlBloquearSuscripciones"/>). Que el candado serialice de verdad lo prueba la
/// integración; aquí se prueba que el código lee DESPUÉS de tomarlo.
/// </summary>
public class BloqueoSuscripcionStripeFalso : IBloqueoSuscripcionStripe
{
    public List<string> Eventos { get; } = [];
    public List<Guid> Tenants { get; } = [];
    public List<string> Suscripciones { get; } = [];
    public Action? AlBloquearSuscripciones { get; init; }

    public Task BloquearTenantAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        Tenants.Add(tenantId);
        Eventos.Add("tenant");
        return Task.CompletedTask;
    }

    public Task BloquearSuscripcionesAsync(IReadOnlyCollection<string> idsSuscripcion, CancellationToken cancellationToken = default)
    {
        Suscripciones.AddRange(idsSuscripcion);
        Eventos.Add("suscripcion");
        AlBloquearSuscripciones?.Invoke();
        return Task.CompletedTask;
    }
}
