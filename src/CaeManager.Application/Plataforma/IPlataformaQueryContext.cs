using CaeManager.Domain.Plataforma;

namespace CaeManager.Application.Plataforma;

/// <summary>
/// Acceso de solo lectura a los catálogos del plano de privilegio de
/// plataforma.
///
/// Están fuera del filtro global de tenant —una concesión cruza tenants por
/// definición— y eso <b>no</b> las hace legibles sin restricción: cada fila
/// dice qué usuario de TALVEG puede abrir los datos de qué cliente y hasta
/// cuándo. Toda consulta se acota a la posición del llamante: el usuario de
/// plataforma ve las suyas, el tenant visitado ve las que le apuntan, y nadie
/// tiene un "listar todas". Lo vigila un test de arquitectura.
/// </summary>
public interface IPlataformaQueryContext
{
    /// <summary>
    /// La fila única de estado de bootstrap. No es fuente de autoridad: dice
    /// quién fue designado raíz por el despliegue y si el acto fundacional ya se
    /// consumió.
    /// </summary>
    IQueryable<EstadoBootstrapPlataforma> EstadoBootstrapPlataforma { get; }

    IQueryable<ConcesionPrivilegio> ConcesionesPrivilegio { get; }
    IQueryable<SesionPrivilegiada> SesionesPrivilegiadas { get; }
    IQueryable<TenantAlcanzadoPorConcesion> TenantsAlcanzadosPorConcesion { get; }
}
