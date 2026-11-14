using CaeManager.Application.Common;
using CaeManager.Application.Empresas;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Subcontratas.Queries.ObtenerSubcontratasParaSelector;

/// <summary>
/// Lista ligera para poblar selectores. Con EmpresaId, se restringe a las
/// Subcontratas que ya prestan servicio a esa Empresa — sin EmpresaId,
/// devuelve todas las que el usuario alcanza a gestionar.
///
/// <para>
/// <b>Alcance de gestión (2026-11-14).</b> Antes devolvía el catálogo entero del
/// Tenant propietario «para reutilizar un registro al dar de alta», pero los
/// comandos que consumen la elección ya no lo aceptan: <c>CrearVehiculoCommand</c>
/// exige <see cref="IAlcanceDatosService.ObtenerSubcontrataIdsParaGestionAsync"/> y
/// <c>CrearTrabajadorCommand</c> la Subcontrata visible. Un Gestor CAE con
/// Asignación de Cartera veía así en el alta de Trabajador y de Vehículo, y en la
/// pastilla «Empresa» de /vehiculos, las razones sociales de Subcontratas fuera de
/// su cartera, que además no podía elegir. Ahora se acota con el mismo alcance de
/// gestión que <c>ObtenerEmpresasParaSelectorQuery</c>, su hermano: <c>null</c>
/// (un rol sin restricción de cartera, como Administrador o Dirección CAE) no
/// filtra; una lista, también vacía, estrecha. El filtro por EmpresaId se combina con el alcance y nunca lo ensancha.
/// </para>
///
/// F3b-Subcontrata (revisión adversaria del 2026-08-26, misma lección que
/// <c>ObtenerClientesParaSelectorQuery</c>): a diferencia de la rama
/// Subcontrata de <c>BuscarGlobalQuery</c> (congelada, sin flujo de una sola
/// sesión que dependa de ella), este selector se adelanta a leer Empresas —
/// el drawer "Nuevo trabajador" (radio "Subcontrata") lo usa para poblar el
/// desplegable, y una Subcontrata creada en la misma sesión necesita
/// aparecer ahí de inmediato. Ver
/// Project-Hydra-Negocio/tecnico/f3b-subcontrata-selector-adelantado-2026-08-26.md.
/// </summary>
public record ObtenerSubcontratasParaSelectorQuery(Guid? EmpresaId = null) : IRequest<IReadOnlyList<SubcontrataSelectorDto>>;

public record SubcontrataSelectorDto(Guid Id, string RazonSocial);

public class ObtenerSubcontratasParaSelectorQueryHandler(IEmpresasQueryContext empresasContext, IAlcanceDatosService alcanceDatos)
    : IRequestHandler<ObtenerSubcontratasParaSelectorQuery, IReadOnlyList<SubcontrataSelectorDto>>
{
    public async Task<IReadOnlyList<SubcontrataSelectorDto>> Handle(
        ObtenerSubcontratasParaSelectorQuery request, CancellationToken cancellationToken)
    {
        var consulta = empresasContext.Empresas.Where(e => e.NivelServicio != null);

        var subcontrataIdsGestionables = await alcanceDatos.ObtenerSubcontrataIdsParaGestionAsync(cancellationToken);
        if (subcontrataIdsGestionables is not null)
            consulta = consulta.Where(s => subcontrataIdsGestionables.Contains(s.Id));

        if (request.EmpresaId is not null)
        {
            // F4.2b: antes leía SubcontratasEmpresas. En la arista unificada esa
            // shape es (Proveedora = Subcontrata, Cliente = Empresa propia), así
            // que el par se busca por ClienteId.
            //
            // El filtro de vigencia SÍ cambia el comportamiento: la tabla legacy
            // borraba físicamente al desvincular, mientras que una relación
            // cerrada sigue en la tabla — sin él, el selector ofrecería
            // subcontratas que ya no prestan servicio a esa Empresa.
            //
            // El JOIN discriminador es defensa en profundidad, no corrección: la
            // consulta base ya restringe a NivelServicio != null, así que una
            // Empresa propia que llegara a colarse en la subconsulta quedaría
            // descartada igualmente. Se declara explícito para que quitar aquel
            // filtro en el futuro no reintroduzca el fallo en silencio.
            var subcontrataIdsAsociadas = empresasContext.RelacionesEmpresariales
                .Where(r => r.ClienteId == request.EmpresaId && r.VigenciaHasta == null)
                .Join(empresasContext.Empresas.Where(e => e.NivelServicio != null),
                    r => r.ProveedoraId, e => e.Id, (r, e) => e.Id);

            consulta = consulta.Where(s => subcontrataIdsAsociadas.Contains(s.Id));
        }

        return await consulta
            .OrderBy(s => s.RazonSocial)
            .Select(s => new SubcontrataSelectorDto(s.Id, s.RazonSocial))
            .ToListAsync(cancellationToken);
    }
}
