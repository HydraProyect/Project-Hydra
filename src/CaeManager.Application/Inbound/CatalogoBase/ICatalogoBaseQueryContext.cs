using CaeManager.Domain.Inbound.CatalogoBase;

namespace CaeManager.Application.Inbound.CatalogoBase;

/// <summary>
/// Lectura del catálogo base de Plataforma (B-13): tablas globales, sin Tenant, que <c>cae_app_runtime</c> solo puede
/// leer. Sin tracking: la aplicación no escribe el catálogo.
/// </summary>
public interface ICatalogoBaseQueryContext
{
    IQueryable<ActividadCatalogo> CatalogoActividades { get; }
    IQueryable<CodigoOficialActividad> CatalogoActividadesCodigosOficiales { get; }
    IQueryable<RiesgoCatalogo> CatalogoRiesgos { get; }
    IQueryable<RequisitoBase> CatalogoRequisitosBase { get; }
    IQueryable<ReglaRiesgoRequisito> CatalogoReglasRiesgoRequisito { get; }
}
