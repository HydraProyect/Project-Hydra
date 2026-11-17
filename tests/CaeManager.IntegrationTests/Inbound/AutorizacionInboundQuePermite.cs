using CaeManager.Application.Inbound.Autorizacion;

namespace CaeManager.IntegrationTests.Inbound;

/// <summary>
/// Doble de <see cref="IAutorizacionInbound"/> que concede toda capacidad: para las pruebas de un handler Inbound que
/// prueban otra capa (alcance de cartera, dominio, RLS). La matriz de capacidades se prueba en
/// <c>AutorizacionInboundTests</c>, y que cada handler llame a la puerta, en <c>LlamadaAIAutorizacionInboundTests</c>.
/// </summary>
public sealed class AutorizacionInboundQuePermite : IAutorizacionInbound
{
    public Task<bool> TieneAsync(CapacidadInbound capacidad, CancellationToken cancellationToken) => Task.FromResult(true);
}
