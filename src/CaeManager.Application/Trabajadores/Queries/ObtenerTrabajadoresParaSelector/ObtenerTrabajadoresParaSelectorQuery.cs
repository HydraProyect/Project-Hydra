using CaeManager.Application.Common;
using CaeManager.Application.Trabajadores;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadoresParaSelector;

/// <summary>
/// Qué Trabajadores ofrece un selector. No tiene valor por defecto a propósito: cada
/// consumidor elige, y el compilador impide que uno nuevo herede la base general sin
/// haberlo decidido.
/// </summary>
public enum AlcanceSelectorTrabajadores
{
    /// <summary>
    /// Solo los Trabajadores dentro del alcance de datos del usuario
    /// (<see cref="IAlcanceDatosService.ObtenerTrabajadorIdsVisiblesAsync"/>): para un Gestor CAE,
    /// los de su Asignación de Cartera; sin restricción para los roles con acceso total. Es el
    /// criterio de todo selector que cuelga algo de un Trabajador ya existente —un Documento,
    /// una Gestión, un Proyecto, un documento generado, un alias— y de los filtros de listados
    /// que ya están acotados. Varios de esos comandos exigen además que el Trabajador sea visible
    /// (CrearGestionesParaTrabajador, AsignarTecnicoProyecto, GenerarDocumentoIndividual,
    /// AsignarAliasTrabajador): ofrecer ahí a uno fuera de cartera enseña su nombre y su DNI y
    /// termina en «No encontramos este trabajador».
    /// </summary>
    Cartera,

    /// <summary>
    /// Todos los Trabajadores del Tenant actual (RLS y filtro de tenant siguen aplicando). Solo
    /// para los flujos de "elige de la base general": un mismo Trabajador de una Subcontrata puede
    /// prestar servicio a Clientes empresariales de distintos Gestores CAE, y hace falta poder
    /// añadirlo a los propios Centros —crear su primera Asignación— aunque todavía no esté en la
    /// cartera visible (DrawerAsignacionMasiva). Visitas e Incidencias también lo piden, pero
    /// solo porque conservan el comportamiento anterior: sus comandos no comprueban la cartera
    /// del Trabajador y acotarlos es una decisión de producto aún no tomada.
    /// </summary>
    BaseGeneralDelTenant,
}

public record ObtenerTrabajadoresParaSelectorQuery(AlcanceSelectorTrabajadores Alcance)
    : IRequest<IReadOnlyList<TrabajadorSelectorDto>>;

public record TrabajadorSelectorDto(Guid Id, string NombreCompleto, string? Dni, string? Alias);

public class ObtenerTrabajadoresParaSelectorQueryHandler(ITrabajadoresQueryContext dbContext, IAlcanceDatosService alcanceDatos)
    : IRequestHandler<ObtenerTrabajadoresParaSelectorQuery, IReadOnlyList<TrabajadorSelectorDto>>
{
    public async Task<IReadOnlyList<TrabajadorSelectorDto>> Handle(
        ObtenerTrabajadoresParaSelectorQuery request, CancellationToken cancellationToken)
    {
        var consulta = dbContext.Trabajadores.AsQueryable();

        if (request.Alcance != AlcanceSelectorTrabajadores.BaseGeneralDelTenant)
        {
            // Cualquier valor que no sea explícitamente la base general se acota a la cartera
            // (falla cerrado ante un valor de enum fuera de rango).
            var trabajadorIdsVisibles = await alcanceDatos.ObtenerTrabajadorIdsVisiblesAsync(cancellationToken);
            if (trabajadorIdsVisibles is not null)
                consulta = consulta.Where(t => trabajadorIdsVisibles.Contains(t.Id));
        }

        return await consulta
            .OrderBy(t => t.Apellidos).ThenBy(t => t.Nombre)
            .Select(t => new TrabajadorSelectorDto(t.Id, t.Nombre + " " + t.Apellidos, t.Dni, t.Alias))
            .ToListAsync(cancellationToken);
    }
}
