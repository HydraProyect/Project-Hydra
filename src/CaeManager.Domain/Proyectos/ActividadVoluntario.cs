using CaeManager.Domain.Trabajadores;

namespace CaeManager.Domain.Proyectos;

/// <summary>
/// Regla de actividad de un Trabajador voluntario, derivada (no se guarda): está activo mientras participa
/// (ProyectoTecnico sin baja) en algún Proyecto de tipo Evento que no ha terminado, sea vigente o futuro; en
/// cuanto no le queda ninguno, pasa a inactivo, y se reactiva solo al asignarlo a otro evento. Un Trabajador
/// que no es voluntario nunca está inactivo por esta regla. El criterio de «sin terminar» vive en <see cref="VigenciaEvento"/>, y la consulta <c>ActividadVoluntarios</c>
/// (Application) lo traduce a SQL desde esa misma expresión.
/// </summary>
public static class ActividadVoluntario
{
    public static bool EstaInactivo(TipoContratacion tipo, IEnumerable<Proyecto> eventosEnLosQueParticipa, DateOnly hoy) =>
        tipo == TipoContratacion.Voluntario
        && !eventosEnLosQueParticipa.Any(p => p.EsEvento && !p.EventoTerminadoEn(hoy));
}
