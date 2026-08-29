using CaeManager.Application.Asignaciones;
using CaeManager.Application.Configuracion;
using CaeManager.Application.Reportes;
using CaeManager.Application.Subcontratas;
using CaeManager.Application.Tests.Integraciones;
using CaeManager.Domain.Asignaciones;
using CaeManager.Domain.Configuracion;
using CaeManager.Domain.Reportes;
using CaeManager.Domain.Subcontratas;

namespace CaeManager.Application.Tests.Reportes;

/// <summary>
/// Los cuatro contextos de consulta que los handlers de Reportes necesitan y
/// que todavía no tenían fake propio — el resto se reutiliza de
/// Plantillas/Documentos/TiposDocumento. Envuelven las listas en
/// <see cref="TestAsyncQueryable{T}"/> (ver IntegracionesQueryContextFalso)
/// porque los handlers usan los operadores asíncronos de EF Core.
/// </summary>
public class AsignacionesQueryContextFalso : IAsignacionesQueryContext
{
    public List<Asignacion> ListaAsignaciones { get; } = [];

    public IQueryable<Asignacion> Asignaciones => new TestAsyncQueryable<Asignacion>(ListaAsignaciones.AsQueryable());
}

// F3b/F3c redujeron ISubcontratasQueryContext a estas dos colecciones: la
// entidad Subcontrata y sus tablas puente (SubcontrataCliente,
// SubcontrataEmpresa) se retiraron del modelo. El fake sigue la forma actual
// del contrato, no la que tenía cuando se escribió este test.
public class SubcontratasQueryContextFalso : ISubcontratasQueryContext
{
    public List<CredencialAccesoSubcontrata> ListaCredencialesAccesoSubcontrata { get; } = [];
    public List<VerificacionExternaSubcontrata> ListaVerificacionesExternaSubcontrata { get; } = [];

    public IQueryable<CredencialAccesoSubcontrata> CredencialesAccesoSubcontrata =>
        new TestAsyncQueryable<CredencialAccesoSubcontrata>(ListaCredencialesAccesoSubcontrata.AsQueryable());
    public IQueryable<VerificacionExternaSubcontrata> VerificacionesExternaSubcontrata =>
        new TestAsyncQueryable<VerificacionExternaSubcontrata>(ListaVerificacionesExternaSubcontrata.AsQueryable());
}

public class ConfiguracionQueryContextFalso : IConfiguracionQueryContext
{
    public List<ParametroSistema> ListaParametrosSistema { get; } = [];
    public List<PreferenciaDashboardUsuario> ListaPreferenciasDashboardUsuario { get; } = [];
    public List<FiltroGuardado> ListaFiltrosGuardados { get; } = [];
    public List<EstadoAutomatizacion> ListaEstadosAutomatizacion { get; } = [];

    public IQueryable<ParametroSistema> ParametrosSistema => new TestAsyncQueryable<ParametroSistema>(ListaParametrosSistema.AsQueryable());
    public IQueryable<PreferenciaDashboardUsuario> PreferenciasDashboardUsuario =>
        new TestAsyncQueryable<PreferenciaDashboardUsuario>(ListaPreferenciasDashboardUsuario.AsQueryable());
    public IQueryable<FiltroGuardado> FiltrosGuardados => new TestAsyncQueryable<FiltroGuardado>(ListaFiltrosGuardados.AsQueryable());
    public IQueryable<EstadoAutomatizacion> EstadosAutomatizacion => new TestAsyncQueryable<EstadoAutomatizacion>(ListaEstadosAutomatizacion.AsQueryable());
}

public class ReportesQueryContextFalso : IReportesQueryContext
{
    public List<HistorialInforme> ListaHistorialInformes { get; } = [];

    public IQueryable<HistorialInforme> HistorialInformes => new TestAsyncQueryable<HistorialInforme>(ListaHistorialInformes.AsQueryable());
}
