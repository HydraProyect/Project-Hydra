using CaeManager.Domain.Facturacion;

namespace CaeManager.Application.Facturacion;

public interface IFacturacionQueryContext
{
    IQueryable<TarifaCliente> TarifasCliente { get; }
}
