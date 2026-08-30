using CaeManager.Domain.Common;
using CaeManager.Domain.DocumentosIa;

namespace CaeManager.Application.DocumentosIa.Common;

public class DocumentAIProviderFactory(IEnumerable<IDocumentAIProvider> proveedores) : IDocumentAIProviderFactory
{
    /// <summary>
    /// Orden de preferencia por capacidad, declarado aquí y no heredado del
    /// orden de registro en el contenedor.
    ///
    /// Antes, <see cref="ObtenerPorCapacidad"/> devolvía
    /// <c>Dictionary.Values</c> y el router tomaba <c>[0]</c> y <c>[1]</c>. Eso
    /// apoyaba dos cosas que no eran ciertas. La primera, que un
    /// <see cref="Dictionary{TKey,TValue}"/> enumera en orden de inserción:
    /// .NET no lo garantiza, es un detalle de implementación que cambia al
    /// reorganizarse los cubos internos. La segunda, que el orden de
    /// inserción era el deseado: como Mistral se registra antes que Anthropic
    /// y declara también <see cref="CapacidadesProveedorIa.ExtraccionEstructurada"/>,
    /// el proveedor primario real de estructuración era Mistral, mientras el
    /// comentario del registro afirmaba que era Anthropic. Quién recibe los
    /// datos personales de un documento —y en qué país— dependía del orden de
    /// unas líneas de DI.
    ///
    /// Para OCR va primero Mistral, que es el especializado y factura por
    /// página. Para estructuración va primero Anthropic, con Gemini de
    /// alternativa, que es lo que el registro ya decía querer. Cambiar cuál es
    /// el primario sigue siendo una decisión de benchmark: ahora se toma
    /// editando esta tabla, que es el único sitio que lo decide.
    ///
    /// Un proveedor que no aparezca en la tabla se ordena después de los
    /// listados, por código, para que su posición tampoco dependa del
    /// contenedor. <c>ProveedorFalsoDocumentAI</c> (E2E) es la excepción
    /// deliberada: va delante de todo cuando está registrado, que es solo bajo
    /// la bandera de la fixture.
    /// </summary>
    private static readonly IReadOnlyDictionary<CapacidadesProveedorIa, string[]> OrdenPreferente =
        new Dictionary<CapacidadesProveedorIa, string[]>
        {
            [CapacidadesProveedorIa.OcrImagenAEscaneado] = ["falso-e2e", "mistral-ocr", "anthropic"],
            [CapacidadesProveedorIa.ExtraccionEstructurada] = ["falso-e2e", "anthropic", "gemini", "mistral-ocr"],
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
