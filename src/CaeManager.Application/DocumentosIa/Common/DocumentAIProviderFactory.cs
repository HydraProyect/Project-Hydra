using CaeManager.Domain.Common;
using CaeManager.Domain.DocumentosIa;

namespace CaeManager.Application.DocumentosIa.Common;

public class DocumentAIProviderFactory(IEnumerable<IDocumentAIProvider> proveedores) : IDocumentAIProviderFactory
{
    private readonly IReadOnlyDictionary<string, IDocumentAIProvider> _porCodigo =
        proveedores.ToDictionary(p => p.Codigo, StringComparer.OrdinalIgnoreCase);

    public Result<IDocumentAIProvider> Resolver(string codigo) =>
        _porCodigo.TryGetValue(codigo, out var proveedor)
            ? Result.Exito(proveedor)
            : Result.Fallo<IDocumentAIProvider>(Error.Crear(
                "DocumentAIProvider.NoEncontrado", $"No existe un proveedor de IA documental con código \"{codigo}\"."));

    public IReadOnlyList<IDocumentAIProvider> ObtenerPorCapacidad(CapacidadesProveedorIa capacidad) =>
        _porCodigo.Values.Where(p => p.Capacidades.HasFlag(capacidad)).ToList();
}
