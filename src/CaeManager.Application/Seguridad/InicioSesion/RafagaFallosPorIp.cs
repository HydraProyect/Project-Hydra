using System.Collections.Concurrent;

namespace CaeManager.Application.Seguridad.InicioSesion;

/// <summary>
/// Ráfaga de intentos fallidos por IP, en memoria y por instancia.
///
/// <para>
/// Cuenta los fallos de <b>cualquier</b> cuenta, también de las que no existen
/// (que no dejan fila en la base porque no tienen Tenant): un barrido de
/// credenciales prueba muchas cuentas desde la misma IP, y el bloqueo de
/// Identity, que es por cuenta, no lo ve. Avisa una sola vez por IP y ventana:
/// <see cref="RegistrarFallo"/> devuelve verdadero solo en el intento que
/// alcanza el umbral.
/// </para>
///
/// <para>
/// Por instancia: con las ranuras azul y verde solo una recibe tráfico, y un
/// reinicio vacía el contador, que es aceptable para una señal de aviso (no es
/// un control de acceso; el freno sigue siendo el bloqueo de Identity y el
/// limitador de tasa del login).
/// </para>
/// </summary>
public sealed class RafagaFallosPorIp(OpcionesDeteccionInicioSesion opciones)
{
    /// <summary>Tope de IPs vigiladas a la vez: una inundación de IPs distintas no agota la memoria.</summary>
    public const int MaximoIps = 20_000;

    private readonly ConcurrentDictionary<string, Ventana> _ventanas = new(StringComparer.Ordinal);

    public bool RegistrarFallo(string ip, DateTime ahoraUtc)
    {
        var duracion = TimeSpan.FromMinutes(opciones.MinutosVentanaRafaga);
        if (_ventanas.Count >= MaximoIps)
            Podar(ahoraUtc, duracion);

        var ventana = _ventanas.GetOrAdd(ip, _ => new Ventana());
        lock (ventana)
        {
            while (ventana.Fallos.Count > 0 && ahoraUtc - ventana.Fallos.Peek() > duracion)
                ventana.Fallos.Dequeue();

            ventana.Fallos.Enqueue(ahoraUtc);
            if (ventana.Fallos.Count < opciones.UmbralRafagaFallos)
                return false;

            if (ventana.AvisadaHastaUtc is { } hasta && ahoraUtc < hasta)
                return false;

            ventana.AvisadaHastaUtc = ahoraUtc + duracion;
            return true;
        }
    }

    private void Podar(DateTime ahoraUtc, TimeSpan duracion)
    {
        foreach (var (ip, ventana) in _ventanas)
        {
            lock (ventana)
            {
                if (ventana.Fallos.Count == 0 || ahoraUtc - ventana.Fallos.Last() > duracion)
                    _ventanas.TryRemove(ip, out _);
            }
        }
    }

    private sealed class Ventana
    {
        public Queue<DateTime> Fallos { get; } = new();

        public DateTime? AvisadaHastaUtc { get; set; }
    }
}
