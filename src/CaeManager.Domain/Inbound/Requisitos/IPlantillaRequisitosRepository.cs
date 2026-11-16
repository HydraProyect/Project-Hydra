namespace CaeManager.Domain.Inbound.Requisitos;

public interface IPlantillaRequisitosRepository
{
    /// <summary>La plantilla del Tenant actual con todos sus requisitos, activos y retirados, con tracking.</summary>
    Task<PlantillaRequisitos?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Si ya hay una plantilla activa del Tenant actual para esa referencia (Actividad, riesgo, Centro o Cliente empresarial).</summary>
    Task<bool> ExisteActivaAsync(TipoPlantillaRequisitos tipo, Guid referenciaId, CancellationToken cancellationToken = default);

    void Agregar(PlantillaRequisitos plantilla);
}
