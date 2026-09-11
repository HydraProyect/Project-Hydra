using CaeManager.Application.DocumentosIa;
using CaeManager.Application.Tests.Integraciones;
using CaeManager.Domain.DocumentosIa;

namespace CaeManager.Application.Tests.DocumentosIa;

/// <summary>
/// Fake en memoria de <see cref="IDocumentosIaQueryContext"/>. Envuelve las
/// listas en <see cref="TestAsyncQueryable{T}"/> (ver IntegracionesQueryContextFalso)
/// porque ObtenerAuditoriaIaQueryHandler usa CountAsync/ToListAsync, que un
/// List.AsQueryable() a secas no soporta.
/// </summary>
public class AuditoriaIaQueryContextFalso : IDocumentosIaQueryContext
{
    public List<ExtraccionIaCache> ListaExtraccionesIaCache { get; } = [];
    public List<AuditoriaExtraccionIa> ListaAuditoriasExtraccionIa { get; } = [];

    public IQueryable<ExtraccionIaCache> ExtraccionesIaCache =>
        new TestAsyncQueryable<ExtraccionIaCache>(ListaExtraccionesIaCache.AsQueryable());
    public IQueryable<AuditoriaExtraccionIa> AuditoriasExtraccionIa =>
        new TestAsyncQueryable<AuditoriaExtraccionIa>(ListaAuditoriasExtraccionIa.AsQueryable());
}
