using CaeManager.Domain.Common;
using CaeManager.Domain.DocumentosIa;

namespace CaeManager.Application.DocumentosIa.Common;

/// <summary>Resuelve el proveedor documental registrado por su codigo o capacidad.</summary>
public interface IDocumentAIProviderFactory
{
    Result<IDocumentAIProvider> Resolver(string codigo);

    IReadOnlyList<IDocumentAIProvider> ObtenerPorCapacidad(CapacidadesProveedorIa capacidad);
}
