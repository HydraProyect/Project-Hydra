using CaeManager.Domain.Inbound.Requisitos;
using CaeManager.Domain.Trabajadores;

namespace CaeManager.Application.Inbound.Requisitos.Queries;

/// <param name="ReferenciaId">La Actividad, el riesgo, el Centro o la Empresa Cliente empresarial, según <paramref name="Tipo"/>.</param>
public record PlantillaRequisitosResumenDto(
    Guid Id, TipoPlantillaRequisitos Tipo, Guid ReferenciaId, bool Activa, int RequisitosActivos, Guid Version);

public record PlantillaRequisitosDto(
    Guid Id, TipoPlantillaRequisitos Tipo, Guid ReferenciaId, bool Activa, Guid Version, IReadOnlyList<RequisitoPlantillaDto> Requisitos);

/// <param name="ToleranciaDias">La fijada. La que cuenta para decidir es <see cref="RequisitoPlantilla.ToleranciaEfectiva"/> (R-6).</param>
public record RequisitoPlantillaDto(
    Guid Id,
    Guid TipoDocumentoId,
    Guid? RequisitoBaseId,
    EntidadRequisito Entidad,
    OrigenRequisito Origen,
    ModoVigenciaRequisito ModoVigencia,
    int? VigenciaMeses,
    int? ToleranciaDias,
    bool BloqueaAcceso,
    string? Criterio,
    DestinatarioRequisito SePideA,
    TiposContratacion TiposContratacion,
    IReadOnlyList<string> Equivalencias,
    AmbitoDocumentoRequisito AmbitoDocumento,
    bool Activo);
