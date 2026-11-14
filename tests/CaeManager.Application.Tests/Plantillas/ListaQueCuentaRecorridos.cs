using System.Collections;

namespace CaeManager.Application.Tests.Plantillas;

/// <summary>
/// Una lista de Ids de alcance que cuenta cuántas veces se recorre. Buscar un Id con <c>Contains</c> sobre una
/// <see cref="IReadOnlyList{T}"/> que no es <see cref="ICollection{T}"/> la recorre entera, así que los recorridos
/// delatan una comprobación lineal por registro frente a una sola conversión a conjunto por llamada.
/// </summary>
public sealed class ListaQueCuentaRecorridos(IReadOnlyList<Guid> ids) : IReadOnlyList<Guid>
{
    public int Recorridos { get; private set; }

    public Guid this[int index] => ids[index];

    public int Count => ids.Count;

    public IEnumerator<Guid> GetEnumerator()
    {
        Recorridos++;
        return ids.GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
