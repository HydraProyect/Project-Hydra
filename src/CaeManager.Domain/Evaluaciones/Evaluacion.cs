using CaeManager.Domain.Common;

namespace CaeManager.Domain.Evaluaciones;

/// <summary>
/// Evaluación de riesgo laboral de un Centro (y, opcionalmente, de un
/// Trabajador concreto dentro de él), con una puntuación de cumplimiento de
/// 0 a 100.
/// </summary>
public class Evaluacion : EntidadBase
{
    public const int LongitudMaximaObservaciones = 2000;
    public const int PuntuacionMinima = 0;
    public const int PuntuacionMaxima = 100;

    public Guid CentroId { get; private set; }
    public Guid? TrabajadorId { get; private set; }
    public DateOnly Fecha { get; private set; }
    public int Puntuacion { get; private set; }
    public string? Observaciones { get; private set; }

    private Evaluacion()
    {
        // Requerido por EF Core.
    }

    public Evaluacion(Guid centroId, Guid? trabajadorId, DateOnly fecha, int puntuacion, string? observaciones)
    {
        if (centroId == Guid.Empty)
            throw new ArgumentException("La evaluación debe tener un centro.", nameof(centroId));

        CentroId = centroId;
        TrabajadorId = trabajadorId;
        Fecha = fecha;
        EstablecerPuntuacion(puntuacion);
        EstablecerObservaciones(observaciones);
    }

    public void Actualizar(Guid? trabajadorId, DateOnly fecha, int puntuacion, string? observaciones)
    {
        TrabajadorId = trabajadorId;
        Fecha = fecha;
        EstablecerPuntuacion(puntuacion);
        EstablecerObservaciones(observaciones);
    }

    private void EstablecerPuntuacion(int puntuacion)
    {
        if (puntuacion < PuntuacionMinima || puntuacion > PuntuacionMaxima)
            throw new ArgumentException(
                $"La puntuación debe estar entre {PuntuacionMinima} y {PuntuacionMaxima}.", nameof(puntuacion));

        Puntuacion = puntuacion;
    }

    private void EstablecerObservaciones(string? observaciones)
    {
        if (observaciones is not null && observaciones.Length > LongitudMaximaObservaciones)
            throw new ArgumentException(
                $"Las observaciones no pueden superar {LongitudMaximaObservaciones} caracteres.", nameof(observaciones));

        Observaciones = observaciones;
    }
}
