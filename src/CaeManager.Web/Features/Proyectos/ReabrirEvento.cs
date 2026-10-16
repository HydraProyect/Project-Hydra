using CaeManager.Domain.Proyectos;

namespace CaeManager.Web.Features.Proyectos;

/// <summary>
/// El diálogo de «Reabrir» del listado y de la ficha 360 comparten esta regla: un <see cref="TipoProyecto.Evento"/> cuya
/// fecha de fin prevista ya pasó (decisión 2026-10-16: se cerró solo) pide en el mismo diálogo una fecha de fin nueva, de
/// hoy o posterior, que viaja en <c>ReabrirProyectoCommand.NuevaFechaFinPrevista</c>. Sin ella el cierre automático lo
/// volvería a cerrar en su siguiente pase.
/// </summary>
public static class ReabrirEvento
{
    public static bool PideNuevaFechaFin(TipoProyecto? tipo, DateOnly? finPrevisto, DateOnly hoy) =>
        tipo == TipoProyecto.Evento && finPrevisto < hoy;

    /// <summary>
    /// Lee la fecha del diálogo. Sin campo, no hay fecha que enviar y vale. Con campo, tiene que ser una fecha de hoy o
    /// posterior; si no, devuelve <c>false</c> y el diálogo muestra el error sin enviar nada.
    /// </summary>
    public static bool LeerNuevaFechaFin(bool pideFecha, string texto, DateOnly hoy, out DateOnly? nuevaFechaFin)
    {
        nuevaFechaFin = null;
        if (!pideFecha)
            return true;
        if (!DateOnly.TryParse(texto, out var fecha) || fecha < hoy)
            return false;

        nuevaFechaFin = fecha;
        return true;
    }
}
