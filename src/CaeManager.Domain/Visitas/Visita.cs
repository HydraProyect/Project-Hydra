using CaeManager.Domain.Common;

namespace CaeManager.Domain.Visitas;

/// <summary>
/// Programación de la entrada de uno o varios Trabajadores a un Centro
/// durante un rango de fechas — lo que el cliente comunica como "el
/// trabajador X debe entrar en el Centro Y del día A al día B". No es una
/// Asignacion (que es la relación permanente Trabajador↔Centro): una Visita
/// es puntual y temporal, pensada para verificar de antemano que la
/// documentación de la Empresa y de los Trabajadores implicados está en
/// regla antes de que la visita ocurra. Al pasar FechaFin, la visita deja de
/// aparecer en las vistas activas pero no se borra — igual que Asignacion
/// con FechaBaja, el historial se conserva siempre.
/// </summary>
public class Visita : EntidadBase
{
    public const int LongitudMaximaNotas = 1000;

    public Guid CentroId { get; private set; }
    public DateOnly FechaInicio { get; private set; }
    public DateOnly FechaFin { get; private set; }
    public bool NotificadoCliente { get; private set; }
    public string? Notas { get; private set; }

    public bool EstaActiva(DateOnly hoy) => FechaFin >= hoy;

    private Visita()
    {
    }

    public Visita(Guid centroId, DateOnly fechaInicio, DateOnly fechaFin, string? notas)
    {
        if (centroId == Guid.Empty)
            throw new ArgumentException("La visita debe tener un centro.", nameof(centroId));

        CentroId = centroId;
        EstablecerFechas(fechaInicio, fechaFin);
        EstablecerNotas(notas);
    }

    public void Actualizar(DateOnly fechaInicio, DateOnly fechaFin, string? notas)
    {
        EstablecerFechas(fechaInicio, fechaFin);
        EstablecerNotas(notas);
    }

    public void MarcarNotificadoCliente(bool notificado) => NotificadoCliente = notificado;

    private void EstablecerFechas(DateOnly fechaInicio, DateOnly fechaFin)
    {
        if (fechaFin < fechaInicio)
            throw new ArgumentException("La fecha de fin no puede ser anterior a la fecha de inicio.", nameof(fechaFin));

        FechaInicio = fechaInicio;
        FechaFin = fechaFin;
    }

    private void EstablecerNotas(string? notas)
    {
        if (notas is not null && notas.Length > LongitudMaximaNotas)
            throw new ArgumentException($"Las notas no pueden superar {LongitudMaximaNotas} caracteres.", nameof(notas));

        Notas = notas;
    }
}
