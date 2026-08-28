using CaeManager.Domain.Subcontratas;

namespace CaeManager.Application.Subcontratas;

public interface ISubcontratasQueryContext
{
    IQueryable<CredencialAccesoSubcontrata> CredencialesAccesoSubcontrata { get; }
    IQueryable<VerificacionExternaSubcontrata> VerificacionesExternaSubcontrata { get; }
}
