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
    // shortcut: SePideA queda sin leerse en el Trabajador; en la Empresa contratista sigue vigente (decisión 2026-11-17)
    // En un requisito de Trabajador solo viaja para que Editar requisito lo devuelva sin cambiarlo; en uno de Empresa contratista
    // la pantalla lo enseña y decide con él (la decisión del 2026-11-14 sobre el país de la Empresa).
    DestinatarioRequisito SePideA,
    TiposContratacion TiposContratacion,
    IReadOnlyList<string> Equivalencias,
    AmbitoDocumentoRequisito AmbitoDocumento,
    bool Activo);
