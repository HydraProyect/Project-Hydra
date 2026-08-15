namespace CaeManager.Domain.Plantillas;

/// <summary>
/// Quién debe firmar un elemento de tipo <see cref="TipoElementoPlantilla.Firma"/>
/// — metadato informativo (checklist), no un tipo de firma nuevo (ADR-010
/// § 2.7): el acto de firma sigue siendo <c>FirmaEnCampoDocumento</c> tal
/// cual. Solo <see cref="Trabajador"/> y <see cref="Gestor"/> son firmantes
/// que ya encajan en el flujo actual (Caso A); <see cref="ResponsableEmpresa"/>
/// y <see cref="RepresentanteLegal"/> anticipan el Caso B (firmante externo),
/// explícitamente no resuelto todavía.
/// </summary>
public enum RolFirmantePlantilla
{
    Trabajador,
    ResponsableEmpresa,
    Gestor,
    RepresentanteLegal,
    Otro
}
