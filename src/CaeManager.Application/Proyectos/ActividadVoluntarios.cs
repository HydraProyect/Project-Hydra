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

    /// <summary>
    /// Los requisitos (<see cref="ProyectoRequisito"/>) de los eventos vigentes (ya empezados y sin terminar, ver
    /// <see cref="VigenciaEvento.AportaRequisitosEn"/>, el mismo criterio que <see cref="Proyecto.EventoVigenteEn"/>) para cada voluntario que participa en ellos, con el Centro del evento.
    /// Es lo que el evento añade al cumplimiento del voluntario mientras dura.
    /// </summary>
    public static IQueryable<RequisitoDeEventoDeVoluntario> RequisitosDeEventosVigentes(
        IQueryable<Trabajador> trabajadores, IProyectosQueryContext proyectos, IQueryable<ProyectoRequisito> requisitos,
        DateOnly hoy) =>
        from proyecto in proyectos.Proyectos.Where(VigenciaEvento.AportaRequisitosEn(hoy))
        join participante in proyectos.ProyectosTecnicos on proyecto.Id equals participante.ProyectoId
        where participante.FechaBaja == null
        join trabajador in trabajadores on participante.TrabajadorId equals trabajador.Id
        where trabajador.TipoContratacion == TipoContratacion.Voluntario
        join requisito in requisitos on proyecto.Id equals requisito.ProyectoId
        select new RequisitoDeEventoDeVoluntario
        {
            TrabajadorId = trabajador.Id,
            TrabajadorNombre = trabajador.Nombre + " " + trabajador.Apellidos,
            EmpresaId = trabajador.EmpresaId,
            CentroId = proyecto.CentroId,
            TipoDocumentoId = requisito.TipoDocumentoId,
        };
}

/// <summary>Un Tipo de Documento que un evento vigente pide a un voluntario que participa en él, en el Centro del evento.</summary>
public sealed record RequisitoDeEventoDeVoluntario
{
    public Guid TrabajadorId { get; init; }
    public string TrabajadorNombre { get; init; } = string.Empty;
    public Guid? EmpresaId { get; init; }
    public Guid CentroId { get; init; }
    public Guid TipoDocumentoId { get; init; }
}
