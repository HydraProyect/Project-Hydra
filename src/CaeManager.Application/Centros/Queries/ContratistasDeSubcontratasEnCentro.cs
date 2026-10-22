using CaeManager.Application.Asignaciones;
using CaeManager.Application.Common;
using CaeManager.Application.Empresas;
using CaeManager.Application.Trabajadores;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Centros.Queries;

/// <summary>
/// La Empresa contratista de cada Subcontrata que aparece en un Centro, para el subtítulo «Subcontrata de …» de
/// Centro 360. La relación es la Relación Empresarial vigente en la que la Subcontrata es la proveedora y una
/// Empresa propia visible es la cliente: el mismo criterio que la columna «Contratada por» del listado de
/// Subcontratas (<c>ObtenerSubcontratasQuery</c>), así que no revela una Empresa que el usuario no ve.
///
/// <para>
/// Si la Subcontrata tiene varias contratistas vigentes, se quedan las que están en ESTE Centro (la Empresa
/// configurada del Centro o la empleadora de algún Trabajador con Asignación activa en él); si ninguna lo está,
/// todas. Varias se unen por coma, por razón social. Sin contratista visible, la Subcontrata no aparece en el
/// diccionario y la pantalla omite el subtítulo. Solo lee: no amplía qué Subcontratas, Trabajadores o Vehículos
/// se enseñan, que lo decide la consulta que llama.
/// </para>
/// </summary>
internal static class ContratistasDeSubcontratasEnCentro
{
    public static async Task<IReadOnlyDictionary<Guid, string>> CargarAsync(
        Guid centroId, IReadOnlyCollection<Guid> subcontrataIds, ICentrosQueryContext centros,
        IAsignacionesQueryContext asignaciones, ITrabajadoresQueryContext trabajadores, IEmpresasQueryContext empresas,
        IAlcanceDatosService alcance, CancellationToken ct)
    {
        if (subcontrataIds.Count == 0) return new Dictionary<Guid, string>();
        var ids = subcontrataIds.Distinct().ToList();
        var visibles = await alcance.ObtenerEmpresaIdsVisiblesAsync(ct);
        var propias = empresas.Empresas.Where(e => e.EsPropia);
        if (visibles is not null)
            propias = propias.Where(e => visibles.Contains(e.Id));

        var relaciones = await (
            from relacion in empresas.RelacionesEmpresariales
            where relacion.VigenciaHasta == null && ids.Contains(relacion.ProveedoraId)
            join empresa in propias on relacion.ClienteId equals empresa.Id
            select new { relacion.ProveedoraId, empresa.Id, empresa.RazonSocial })
            .Distinct().ToListAsync(ct);
        if (relaciones.Count == 0) return new Dictionary<Guid, string>();

        var empresaDelCentro = await centros.Centros.Where(c => c.Id == centroId).Select(c => (Guid?)c.EmpresaId).FirstOrDefaultAsync(ct);
        var empleadoras = await (
            from asignacion in asignaciones.Asignaciones
            where asignacion.CentroId == centroId && asignacion.FechaBaja == null
            join trabajador in trabajadores.Trabajadores on asignacion.TrabajadorId equals trabajador.Id
            where trabajador.EmpresaId != null
            select trabajador.EmpresaId!.Value)
            .Distinct().ToListAsync(ct);
        var enElCentro = empleadoras.ToHashSet();
        if (empresaDelCentro is { } principal) enElCentro.Add(principal);

        return relaciones.GroupBy(r => r.ProveedoraId).ToDictionary(g => g.Key, g =>
        {
            var presentes = g.Where(r => enElCentro.Contains(r.Id)).ToList();
            var elegidas = presentes.Count > 0 ? presentes : g.ToList();
            return string.Join(", ", elegidas.DistinctBy(r => r.Id).Select(r => r.RazonSocial).OrderBy(n => n, StringComparer.CurrentCulture));
        });
    }
}
