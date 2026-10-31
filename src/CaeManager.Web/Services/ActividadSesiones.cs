using System.Collections.Concurrent;

namespace CaeManager.Web.Services;

/// <summary>
/// Última señal de interacción de cada sesión, para que un circuito de Blazor la vea: su
/// principal se queda como estaba al conectarse y no recibe la cookie renovada. Solo en
/// memoria y por identificador de sesión: no se persiste, a propósito (ver
/// <see cref="CaducidadSesion"/>). Tras un reinicio no hay entradas, pero tampoco circuitos:
/// el circuito nuevo sale de una cookie que lleva su propia marca.
/// </summary>
public sealed class ActividadSesiones
{
    /// <summary>Cada cuántas anotaciones se retiran las entradas de sesiones ya caducadas.</summary>
    private const int AnotacionesPorPurga = 1024;

    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _ultimas = new();
    private int _anotaciones;

    public void Anotar(Guid sid, DateTimeOffset momento)
    {
        _ultimas[sid] = momento;

        if (Interlocked.Increment(ref _anotaciones) % AnotacionesPorPurga != 0)
            return;
        foreach (var (clave, ultima) in _ultimas)
            if (momento - ultima >= CaducidadSesion.DuracionMaxima)
                _ultimas.TryRemove(clave, out _);
    }

    public DateTimeOffset? Leer(Guid sid) => _ultimas.TryGetValue(sid, out var ultima) ? ultima : null;
}
