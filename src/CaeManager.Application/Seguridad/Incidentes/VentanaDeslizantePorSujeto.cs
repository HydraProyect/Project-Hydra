using System.Collections.Concurrent;

namespace CaeManager.Application.Seguridad.Incidentes;

/// <summary>
/// Suma ponderada por sujeto en una ventana deslizante, en memoria (mismo criterio que
/// <see cref="InicioSesion.RafagaFallosPorIp"/>): sin consultas a la base por cada
/// exportación o rechazo, y cruzando Tenants, que una consulta bajo RLS no podría.
///
/// <para>
/// <b>Límites conocidos.</b> Es por proceso: un reinicio o una segunda instancia
/// empiezan de cero. Para una señal de alta certeza es aceptable (un falso negativo
/// tras un reinicio, nunca un falso positivo); si se escala a varias instancias
/// activas a la vez, el contador tendrá que pasar a la base.
/// </para>
/// </summary>
public sealed class VentanaDeslizantePorSujeto
{
    /// <summary>Tope de sujetos vigilados a la vez: una inundación de sujetos distintos no agota la memoria.</summary>
    public const int MaximoSujetos = 20_000;

    private readonly ConcurrentDictionary<string, Ventana> _ventanas = new(StringComparer.Ordinal);
    private DateTime _ultimaPoda = DateTime.MinValue;

    /// <summary>
    /// Suma <paramref name="peso"/> al sujeto y dice si la suma de la ventana acaba de
    /// alcanzar el umbral. Devuelve <c>true</c> una sola vez por ventana: después de
    /// saltar, el sujeto queda «avisado» hasta que la ventana pasa, para no repetir el
    /// registro por cada operación siguiente (la deduplicación de la base lo agruparía
    /// igual, pero así ni se llama).
    /// </summary>
    public bool Sumar(string sujeto, long peso, long umbral, TimeSpan duracion, DateTime ahoraUtc, Guid? etiqueta = null)
    {
        if (peso <= 0 || umbral <= 0)
            return false;

        if (_ventanas.Count >= MaximoSujetos && !_ventanas.ContainsKey(sujeto))
        {
            if (ahoraUtc - _ultimaPoda > TimeSpan.FromMinutes(1))
            {
                _ultimaPoda = ahoraUtc;
                Podar(ahoraUtc, duracion);
            }
            if (_ventanas.Count >= MaximoSujetos)
                return false;
        }

        var ventana = _ventanas.GetOrAdd(sujeto, _ => new Ventana());
        lock (ventana)
        {
            while (ventana.Entradas.Count > 0 && ahoraUtc - ventana.Entradas.Peek().Momento > duracion)
                ventana.Suma -= ventana.Entradas.Dequeue().Peso;

            ventana.Entradas.Enqueue((ahoraUtc, peso, etiqueta));
            ventana.Suma += peso;
            ventana.UltimaUtc = ahoraUtc;

            if (ventana.Suma < umbral)
                return false;
            if (ventana.AvisadaHastaUtc is { } hasta && ahoraUtc < hasta)
                return false;

            ventana.AvisadaHastaUtc = ahoraUtc + duracion;
            return true;
        }
    }

    /// <summary>Suma actual del sujeto dentro de la ventana (para la evidencia).</summary>
    public long SumaActual(string sujeto)
    {
        if (!_ventanas.TryGetValue(sujeto, out var ventana))
            return 0;
        lock (ventana)
            return ventana.Suma;
    }

    /// <summary>
    /// Etiquetas distintas (p. ej. los Tenants tocados) de las entradas del sujeto que
    /// siguen dentro de la ventana, ordenadas.
    /// </summary>
    public IReadOnlyList<Guid> EtiquetasEnVentana(string sujeto)
    {
        if (!_ventanas.TryGetValue(sujeto, out var ventana))
            return [];
        lock (ventana)
            return ventana.Entradas.Where(e => e.Etiqueta is not null).Select(e => e.Etiqueta!.Value).Distinct().Order().ToList();
    }

    private void Podar(DateTime ahoraUtc, TimeSpan duracion)
    {
        foreach (var (sujeto, ventana) in _ventanas)
        {
            lock (ventana)
            {
                if (ahoraUtc - ventana.UltimaUtc > duracion)
                    _ventanas.TryRemove(sujeto, out _);
            }
        }
    }

    private sealed class Ventana
    {
        public Queue<(DateTime Momento, long Peso, Guid? Etiqueta)> Entradas { get; } = new();
        public long Suma { get; set; }
        public DateTime UltimaUtc { get; set; }
        public DateTime? AvisadaHastaUtc { get; set; }
    }
}
