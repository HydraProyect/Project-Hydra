using CaeManager.Domain.Common;
using CaeManager.Domain.DocumentosIa;

namespace CaeManager.Application.DocumentosIa.Common;

/// <summary>
/// Resuelve un <see cref="IDocumentAIProvider"/> por su código — nunca un
/// switch/if disperso comparando nombres de proveedor. Añadir un proveedor
/// nuevo (Gemini/Mistral) es registrarlo en Infrastructure, cero cambios
/// aquí. Mismo patrón que <c>IIntegrationProviderFactory</c>
/// (ARQUITECTURA-INTEGRACIONES.md § 4).
/// </summary>
public interface IDocumentAIProviderFactory
{
    Result<IDocumentAIProvider> Resolver(string codigo);

    IReadOnlyList<IDocumentAIProvider> ObtenerPorCapacidad(CapacidadesProveedorIa capacidad);
}
