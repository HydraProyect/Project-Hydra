using CaeManager.Domain.Subcontratas;

namespace CaeManager.Application.Subcontratas;

public interface ISubcontratasQueryContext
{
    IQueryable<Subcontrata> Subcontratas { get; }
    IQueryable<SubcontrataCliente> SubcontratasClientes { get; }
    IQueryable<SubcontrataEmpresa> SubcontratasEmpresas { get; }
    IQueryable<CredencialAccesoSubcontrata> CredencialesAccesoSubcontrata { get; }
}
