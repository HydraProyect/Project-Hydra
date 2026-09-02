using CaeManager.Domain.VigilanciaNormativa;

namespace CaeManager.Application.VigilanciaNormativa;

/// <summary>
/// Lectura del catálogo global de <see cref="AvisoRevisionNormativa"/> —
/// deliberadamente sin ningún filtro por tenant, porque la entidad no tiene
/// <c>TenantId</c> (ver su comentario de clase): el BOE es el mismo para
/// todos, y un aviso no pertenece a quien lo lee. Interfaz de solo lectura
/// separada de <see cref="IAvisoRevisionNormativaRepository"/>, que es el
/// puerto de escritura del sondeo (mismo criterio CQRS que el resto del
/// Application).
/// </summary>
public interface IVigilanciaNormativaQueryContext
{
    IQueryable<AvisoRevisionNormativa> AvisosRevisionNormativa { get; }
}
