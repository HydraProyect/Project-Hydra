using CaeManager.Domain.Dashboard;

namespace CaeManager.Application.Dashboard;

public interface ICierresCuadroDireccion
{
    IQueryable<CierreMensualTenantBeneficiario> CierresMensuales { get; }
    void Agregar(CierreMensualTenantBeneficiario cierre);
}
