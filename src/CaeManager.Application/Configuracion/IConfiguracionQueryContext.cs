using CaeManager.Domain.Configuracion;

namespace CaeManager.Application.Configuracion;

public interface IConfiguracionQueryContext
{
    IQueryable<ParametroSistema> ParametrosSistema { get; }
    IQueryable<PreferenciaDashboardUsuario> PreferenciasDashboardUsuario { get; }
    IQueryable<FiltroGuardado> FiltrosGuardados { get; }
    IQueryable<EstadoAutomatizacion> EstadosAutomatizacion { get; }
}
