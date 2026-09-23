using CaeManager.Application.Common;
using CaeManager.Application.Empresas;
using CaeManager.Application.Trabajadores;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadoresParaSelector;

/// <summary>
/// Qué Trabajadores ofrece un selector. La consulta lo exige como parámetro posicional, así
/// que cada consumidor elige de forma explícita y el compilador impide que uno nuevo herede la
/// base general sin haberlo decidido. <c>default</c> (0) es <see cref="Cartera"/>, y cualquier
/// valor fuera de rango también se trata como Cartera (falla cerrado): solo
/// <see cref="BaseGeneralDelTenant"/>, pedido literalmente, abre todo el Tenant.
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
    /// AsignarAliasTrabajador): ofrecer ahí a uno fuera de cartera enseña su nombre y
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

/// <summary>
/// Opción de un selector de Trabajador. Sin DNI por diseño, en los dos alcances (P4, 2026-09-23):
/// la etiqueta de un selector no es una vista autorizada ni justificada para enseñar un dato
/// identificativo, y la cartera de un Gestor CAE puede ser el Tenant entero, así que el alcance
/// Cartera tampoco lo justifica. Que el tipo no tenga la propiedad hace la garantía estructural:
/// ninguna superficie puede volver a pintarlo sin cambiar este contrato (lo fija
/// TrabajadorSelectorDtoSinDniTests). Los homónimos se distinguen con datos no sensibles —el
/// empleador (<see cref="EmpleadorNombre"/>: razón social de su Empresa o Subcontrata) y el
/// Alias—, y la etiqueta la construye siempre <see cref="EtiquetasSelectorTrabajador"/>, que la
/// garantiza única dentro de la lista. El DNI sigue en la ficha del Trabajador y en las lecturas
/// que lo necesitan (pre-relleno documental, que verifica la identidad por DNI en el servidor,
/// nunca por la etiqueta).
/// </summary>
public record TrabajadorSelectorDto(Guid Id, string NombreCompleto, string? Alias, string? EmpleadorNombre);

public class ObtenerTrabajadoresParaSelectorQueryHandler(
    ITrabajadoresQueryContext dbContext, IEmpresasQueryContext empresasContext, IAlcanceDatosService alcanceDatos)
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

        // El empleador sale en la misma consulta (dos LEFT JOIN, sin N+1), igual que en
        // ObtenerDocumentacionVisitaQuery: primero la Empresa, si no la Subcontrata.
        return await (
            from trabajador in consulta
            join empresa in empresasContext.Empresas on trabajador.EmpresaId equals empresa.Id into empresasCoincidentes
            from empresa in empresasCoincidentes.DefaultIfEmpty()
            join subcontrata in empresasContext.Empresas on trabajador.SubcontrataId equals subcontrata.Id into subcontratasCoincidentes
            from subcontrata in subcontratasCoincidentes.DefaultIfEmpty()
            orderby trabajador.Apellidos, trabajador.Nombre
            select new TrabajadorSelectorDto(
                trabajador.Id,
                trabajador.Nombre + " " + trabajador.Apellidos,
                trabajador.Alias,
                empresa != null ? empresa.RazonSocial : (subcontrata != null ? subcontrata.RazonSocial : null)))
            .ToListAsync(cancellationToken);
    }
}
