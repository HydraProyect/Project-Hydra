using CaeManager.Domain.Proyectos;
using CaeManager.Domain.Trabajadores;

namespace CaeManager.Application.Proyectos;

/// <summary>
/// Gemelo traducible a SQL de <see cref="ActividadVoluntario"/> (Domain): devuelve subconsultas componibles, sin
/// materializar, para que cada lector las combine con sus propios filtros de alcance. Los filtros globales de
/// Tenant y la RLS se aplican a cada IQueryable de origen, así que el resultado nunca cruza de Tenant propietario.
/// </summary>
public static class ActividadVoluntarios
{
    /// <summary>
    /// Ids de los Trabajadores voluntarios inactivos: voluntarios sin participación activa (ProyectoTecnico sin
    /// baja) en ningún Proyecto de tipo Evento sin terminar (<see cref="VigenciaEvento.SinTerminarEn"/>), vigente o futuro.
    /// </summary>
    public static IQueryable<Guid> VoluntariosInactivosIds(
        IQueryable<Trabajador> trabajadores, IProyectosQueryContext proyectos, DateOnly hoy)
    {
        var eventosSinTerminar = proyectos.Proyectos.Where(VigenciaEvento.SinTerminarEn(hoy));
        return from trabajador in trabajadores
               where trabajador.TipoContratacion == TipoContratacion.Voluntario
               where !(from participante in proyectos.ProyectosTecnicos
                       join proyecto in eventosSinTerminar on participante.ProyectoId equals proyecto.Id
                       where participante.TrabajadorId == trabajador.Id && participante.FechaBaja == null
                       select participante.ProyectoId).Any()
               select trabajador.Id;
    }
}
