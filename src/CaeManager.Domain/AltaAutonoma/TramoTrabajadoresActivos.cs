namespace CaeManager.Domain.AltaAutonoma;

/// <summary>
/// Tramo de precio que declara quien se da de alta, por rango de Trabajadores activos (DA-6). Desde 301
/// Trabajadores activos no hay tramo: se cotiza, y el alta autónoma no lo vende.
/// </summary>
public enum TramoTrabajadoresActivos
{
    De1A10,
    De11A40,
    De41A80,
    De81A150,
    De151A300,
}

public static class TramosTrabajadoresActivos
{
    /// <summary>Límite superior del último tramo: por encima, «cotiza».</summary>
    public const int MaximoConTramo = 300;

    /// <summary>
    /// Tramo que corresponde a <paramref name="trabajadoresActivos"/>, o null si pasa de
    /// <see cref="MaximoConTramo"/> (se cotiza).
    /// </summary>
    public static TramoTrabajadoresActivos? ParaTrabajadoresActivos(int trabajadoresActivos) => trabajadoresActivos switch
    {
        < 1 => throw new ArgumentOutOfRangeException(nameof(trabajadoresActivos), trabajadoresActivos,
            "Hace falta al menos un Trabajador activo."),
        <= 10 => TramoTrabajadoresActivos.De1A10,
        <= 40 => TramoTrabajadoresActivos.De11A40,
        <= 80 => TramoTrabajadoresActivos.De41A80,
        <= 150 => TramoTrabajadoresActivos.De81A150,
        <= MaximoConTramo => TramoTrabajadoresActivos.De151A300,
        _ => null,
    };

    /// <summary>Rango cerrado de Trabajadores activos del tramo.</summary>
    public static (int Minimo, int Maximo) Rango(TramoTrabajadoresActivos tramo) => tramo switch
    {
        TramoTrabajadoresActivos.De1A10 => (1, 10),
        TramoTrabajadoresActivos.De11A40 => (11, 40),
        TramoTrabajadoresActivos.De41A80 => (41, 80),
        TramoTrabajadoresActivos.De81A150 => (81, 150),
        TramoTrabajadoresActivos.De151A300 => (151, MaximoConTramo),
        _ => throw new ArgumentOutOfRangeException(nameof(tramo), tramo, "Tramo desconocido."),
    };
}
