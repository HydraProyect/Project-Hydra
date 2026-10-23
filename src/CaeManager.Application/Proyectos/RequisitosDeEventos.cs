using CaeManager.Domain.Documentos;
using CaeManager.Domain.Proyectos;
using CaeManager.Domain.Trabajadores;

namespace CaeManager.Application.Proyectos;

/// <summary>
/// Lo que un Proyecto de tipo Evento añade al cumplimiento de quienes están asignados a él, como subconsulta traducible a
/// SQL y sin materializar, para que cada lector la combine con sus propios filtros de alcance. Los filtros globales de
/// Tenant y la RLS se aplican a cada IQueryable de origen, así que el resultado nunca cruza de Tenant propietario.
/// </summary>
public static class RequisitosDeEventos
{
    /// <summary>
    /// Los requisitos (<see cref="ProyectoRequisito"/>) de los eventos que los aportan hoy (desde la asignación, mientras el
    /// evento es futuro o vigente: <see cref="VigenciaEvento.AportaRequisitosEn"/>, el mismo criterio que
    /// <see cref="Proyecto.EventoAportaRequisitosEn"/>) para cada Trabajador con participación activa en ellos
    /// (<see cref="ProyectoTecnico"/> sin baja), con el Centro del evento. Cuenta para todos los asignados, voluntarios o
    /// no, de la Empresa o de una Subcontrata (decisión 2026-10-16), no solo para los voluntarios.
    /// </summary>
    public static IQueryable<RequisitoDeEvento> DeEventosQueLosAportan(
        IQueryable<Trabajador> trabajadores, IProyectosQueryContext proyectos, IQueryable<ProyectoRequisito> requisitos,
        IQueryable<TipoDocumento> tiposDocumento, DateOnly hoy) =>
        from proyecto in proyectos.Proyectos.Where(VigenciaEvento.AportaRequisitosEn(hoy))
        join participante in proyectos.ProyectosTecnicos on proyecto.Id equals participante.ProyectoId
        where participante.FechaBaja == null
        join trabajador in trabajadores on participante.TrabajadorId equals trabajador.Id
        join requisito in requisitos on proyecto.Id equals requisito.ProyectoId
        // Solo lo que se le puede pedir a una persona: un tipo de ámbito Empresa o Vehículo no es un faltante del Trabajador.
        join tipo in tiposDocumento on requisito.TipoDocumentoId equals tipo.Id
        // Un tipo archivado ya no se pide (catálogo del 2026-10-17): la migración que archivó pasó sus requisitos al
        // sustituto, y uno sin sustituto no se puede cumplir porque no admite altas nuevas.
        where tipo.AmbitoAplicacion == AmbitoAplicacion.Trabajador && !tipo.Archivado
        select new RequisitoDeEvento
        {
            TrabajadorId = trabajador.Id,
            TrabajadorNombre = trabajador.Nombre + " " + trabajador.Apellidos,
            EmpresaId = trabajador.EmpresaId,
            SubcontrataId = trabajador.SubcontrataId,
            CentroId = proyecto.CentroId,
            TipoDocumentoId = requisito.TipoDocumentoId,
        };
}

/// <summary>Un Tipo de Documento que un evento pide a un Trabajador asignado a él, en el Centro del evento.</summary>
public sealed record RequisitoDeEvento
{
    public Guid TrabajadorId { get; init; }
    public string TrabajadorNombre { get; init; } = string.Empty;
    public Guid? EmpresaId { get; init; }
    public Guid? SubcontrataId { get; init; }
    public Guid CentroId { get; init; }
    public Guid TipoDocumentoId { get; init; }
}
