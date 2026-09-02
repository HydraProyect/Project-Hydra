using CaeManager.Application.Tests.Integraciones;
using CaeManager.Application.VigilanciaNormativa;
using CaeManager.Domain.VigilanciaNormativa;

namespace CaeManager.Application.Tests.VigilanciaNormativa;

/// <summary>
/// Envuelto en <see cref="TestAsyncQueryable{T}"/> (ver
/// IntegracionesQueryContextFalso) porque el handler usa <c>ToListAsync</c>.
/// </summary>
public class VigilanciaNormativaQueryContextFalso : IVigilanciaNormativaQueryContext
{
    public List<AvisoRevisionNormativa> ListaAvisos { get; } = [];

    public IQueryable<AvisoRevisionNormativa> AvisosRevisionNormativa =>
        new TestAsyncQueryable<AvisoRevisionNormativa>(ListaAvisos.AsQueryable());
}
