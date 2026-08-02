using CaeManager.Domain.DocumentosIa;

namespace CaeManager.Application.DocumentosIa;

public interface IDocumentosIaQueryContext
{
    IQueryable<ExtraccionIaCache> ExtraccionesIaCache { get; }
    IQueryable<AuditoriaExtraccionIa> AuditoriasExtraccionIa { get; }
}
