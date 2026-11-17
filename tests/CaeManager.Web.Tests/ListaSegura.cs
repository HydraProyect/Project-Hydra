using System.Collections;

namespace CaeManager.Web.Tests;

/// <summary>
/// Registro de lo que un doble recibe (peticiones al mediador falso) apto para tests de componentes: el
/// componente sigue enviando desde el dispatcher del renderizador mientras el test lo lee en su hilo, y
/// enumerar una <see cref="List{T}"/> viva lanza «Collection was modified» (#2087, #2312). Las escrituras
/// y las lecturas van bajo cerrojo y toda enumeración recorre una copia tomada en ese instante, de modo que
/// un <c>OfType&lt;…&gt;().Single()</c> nunca choca con una inserción concurrente.
/// </summary>
internal sealed class ListaSegura<T> : IReadOnlyList<T>
{
    private readonly object _cerrojo = new();
    private readonly List<T> _elementos = [];

    public void Add(T elemento)
    {
        lock (_cerrojo) _elementos.Add(elemento);
    }

    public void Clear()
    {
        lock (_cerrojo) _elementos.Clear();
    }

    public int Count
    {
        get { lock (_cerrojo) return _elementos.Count; }
    }

    public T this[int indice]
    {
        get { lock (_cerrojo) return _elementos[indice]; }
    }

    public int FindIndex(Predicate<T> condicion)
    {
        lock (_cerrojo) return _elementos.FindIndex(condicion);
    }

    public int FindLastIndex(Predicate<T> condicion)
    {
        lock (_cerrojo) return _elementos.FindLastIndex(condicion);
    }

    public IEnumerator<T> GetEnumerator()
    {
        T[] copia;
        lock (_cerrojo) copia = [.. _elementos];
        return ((IEnumerable<T>)copia).GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
