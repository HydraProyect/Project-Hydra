using CaeManager.Application.Common;
using CaeManager.Domain.Asignaciones;
using CaeManager.Domain.Centros;

namespace CaeManager.Application.Reclamaciones;

/// <summary>
/// Las Asignaciones que dan pie a reclamar los documentos de su Trabajador: activas (sin baja), en un Centro visible con
/// gestión CAE (P1-X2: un Centro sin gestión CAE no exige documentación, así que tampoco da pie a reclamarla) cuyo Cliente
/// empresarial es visible. Es LA condición del lote de Trabajador (<c>ObtenerLoteReclamacionQuery</c>): la reutilizan el
/// envío a la Empresa empleadora (<c>EnviarReclamacionEmpresaEmpleadoraCommand</c>) y el «Reclamable» de la vista Mes
/// (<c>ObtenerVencimientosMesQuery</c>), para que lo que se ofrece sea lo que el cajón enseña y el envío acepta.
///
/// <para>
/// Devuelve una subconsulta componible, sin materializar y sin proyectar, como <c>ActividadVoluntarios</c>: cada lector la
/// une o la usa en un <c>Any</c> con sus propios filtros. Los filtros globales de Tenant y la RLS se aplican a cada
/// IQueryable de origen, así que nunca cruza de Tenant propietario. Los alcances nulos son «sin restricción» (acceso total),
/// con la misma convención que <see cref="IAlcanceDatosService"/>. Lee el alcance de Centros y de Clientes empresariales
/// visibles aquí, una sola vez, para que los tres lectores no repitan la lectura de la columna legacy <c>Centro.ClienteId</c>.
/// </para>
/// </summary>
public static class AsignacionesReclamables
{
    public static async Task<IQueryable<Asignacion>> DeAsync(
        IQueryable<Asignacion> asignaciones,
        IQueryable<Centro> centros,
        IAlcanceDatosService alcanceDatos,
        CancellationToken cancellationToken) =>
        De(asignaciones, centros,
            await alcanceDatos.ObtenerCentroIdsVisiblesAsync(cancellationToken),
            await alcanceDatos.ObtenerClienteIdsVisiblesAsync(cancellationToken));

    private static IQueryable<Asignacion> De(
        IQueryable<Asignacion> asignaciones,
        IQueryable<Centro> centros,
        IReadOnlyList<Guid>? centroIdsVisibles,
        IReadOnlyList<Guid>? clienteIdsVisibles) =>
        from asignacion in asignaciones
        where asignacion.FechaBaja == null
        where centroIdsVisibles == null || centroIdsVisibles.Contains(asignacion.CentroId)
        where centros.Any(centro => centro.Id == asignacion.CentroId
            && centro.GestionCae != ModalidadGestionCae.SinGestionCae
            && (clienteIdsVisibles == null || clienteIdsVisibles.Contains(centro.ClienteId)))
        select asignacion;
}
