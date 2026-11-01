using CaeManager.Domain.Common;
using CaeManager.Domain.DocumentosIa;

namespace CaeManager.Application.DocumentosIa.Common;

public class DocumentAIProviderFactory(IEnumerable<IDocumentAIProvider> proveedores) : IDocumentAIProviderFactory
{
    /// <summary>El doble E2E tiene prioridad cuando está registrado; Anthropic atiende las rutas reales.</summary>
    private static readonly IReadOnlyDictionary<CapacidadesProveedorIa, string[]> OrdenPreferente =
        new Dictionary<CapacidadesProveedorIa, string[]>
        {
            [CapacidadesProveedorIa.OcrImagenAEscaneado] = ["falso-e2e", "anthropic"],
            [CapacidadesProveedorIa.ExtraccionEstructurada] = ["falso-e2e", "anthropic"],
        };

    private readonly IReadOnlyDictionary<string, IDocumentAIProvider> _porCodigo =
        proveedores.ToDictionary(p => p.Codigo, StringComparer.OrdinalIgnoreCase);

    public Result<IDocumentAIProvider> Resolver(string codigo) =>
        _porCodigo.TryGetValue(codigo, out var proveedor)
            ? Result.Exito(proveedor)
            : Result.Fallo<IDocumentAIProvider>(Error.Crear(
                "DocumentAIProvider.NoEncontrado", $"No existe un proveedor de IA documental con código \"{codigo}\"."));

    /// <summary>
    /// Candidatos para esta capacidad, en orden de preferencia declarado y
    /// filtrando los que no pueden responder (ver
    /// <see cref="IDocumentAIProvider.EstaDisponible"/>). El router puede
    /// recorrer la lista de principio a fin sin comprobar nada más: todo lo
    /// que sale de aquí es un proveedor que declara la capacidad y tiene
    /// credencial.
    /// </summary>
    public IReadOnlyList<IDocumentAIProvider> ObtenerPorCapacidad(CapacidadesProveedorIa capacidad)
    {
        var orden = OrdenPreferente.TryGetValue(capacidad, out var codigos) ? codigos : [];

        return _porCodigo.Values
            .Where(p => p.Capacidades.HasFlag(capacidad) && p.EstaDisponible)
            .OrderBy(p => IndiceDePreferencia(orden, p.Codigo))
            .ThenBy(p => p.Codigo, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Los no listados van al final (<see cref="int.MaxValue"/>), desempatados por código — nunca por el orden del contenedor.</summary>
    private static int IndiceDePreferencia(string[] orden, string codigo)
    {
        var indice = Array.FindIndex(orden, c => string.Equals(c, codigo, StringComparison.OrdinalIgnoreCase));
        return indice < 0 ? int.MaxValue : indice;
    }
}
