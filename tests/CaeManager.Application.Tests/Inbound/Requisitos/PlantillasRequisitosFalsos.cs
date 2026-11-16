using CaeManager.Application.Inbound.CatalogoBase;
using CaeManager.Application.Inbound.Requisitos;
using CaeManager.Application.Tests.Integraciones;
using CaeManager.Domain.Inbound.CatalogoBase;
using CaeManager.Domain.Inbound.Requisitos;

namespace CaeManager.Application.Tests.Inbound.Requisitos;

public class PlantillaRequisitosRepositorioFalso : IPlantillaRequisitosRepository
{
    public List<PlantillaRequisitos> Plantillas { get; } = [];

    public Task<PlantillaRequisitos?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(Plantillas.FirstOrDefault(p => p.Id == id));

    public Task<bool> ExisteActivaAsync(TipoPlantillaRequisitos tipo, Guid referenciaId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Plantillas.Any(p => p.Activa && p.Tipo == tipo
            && (p.ActividadId ?? p.RiesgoId ?? p.CentroId ?? p.ClienteEmpresarialId) == referenciaId));

    public void Agregar(PlantillaRequisitos plantilla) => Plantillas.Add(plantilla);
}

public class PlantillasRequisitosQueryContextFalso(PlantillaRequisitosRepositorioFalso repositorio) : IPlantillasRequisitosQueryContext
{
    public IQueryable<PlantillaRequisitos> PlantillasRequisitos =>
        new TestAsyncQueryable<PlantillaRequisitos>(repositorio.Plantillas.AsQueryable());

    public IQueryable<RequisitoPlantilla> RequisitosPlantilla =>
        new TestAsyncQueryable<RequisitoPlantilla>(repositorio.Plantillas.SelectMany(p => p.Requisitos).AsQueryable());
}

public class CatalogoBaseQueryContextFalso : ICatalogoBaseQueryContext
{
    public List<ActividadCatalogo> Actividades { get; } = [];
    public List<RiesgoCatalogo> Riesgos { get; } = [];
    public List<RequisitoBase> Requisitos { get; } = [];

    public IQueryable<ActividadCatalogo> CatalogoActividades => new TestAsyncQueryable<ActividadCatalogo>(Actividades.AsQueryable());
    public IQueryable<CodigoOficialActividad> CatalogoActividadesCodigosOficiales =>
        new TestAsyncQueryable<CodigoOficialActividad>(Actividades.SelectMany(a => a.CodigosOficiales).AsQueryable());
    public IQueryable<RiesgoCatalogo> CatalogoRiesgos => new TestAsyncQueryable<RiesgoCatalogo>(Riesgos.AsQueryable());
    public IQueryable<RequisitoBase> CatalogoRequisitosBase => new TestAsyncQueryable<RequisitoBase>(Requisitos.AsQueryable());
    public IQueryable<ReglaRiesgoRequisito> CatalogoReglasRiesgoRequisito =>
        new TestAsyncQueryable<ReglaRiesgoRequisito>(Enumerable.Empty<ReglaRiesgoRequisito>().AsQueryable());
}
