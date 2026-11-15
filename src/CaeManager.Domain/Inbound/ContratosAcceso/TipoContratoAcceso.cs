namespace CaeManager.Domain.Inbound.ContratosAcceso;

/// <summary>
/// Tipo del contrato de acceso (CA-10). <see cref="Recurrente"/> es un rango de fechas. Las reglas que dependen del tipo
/// (confirmación manual obligatoria en <see cref="Obra"/> y <see cref="Emergencia"/>, duda 4; mínimo del contrato de
/// Emergencia, D-16) no viven aquí: entran con sus incrementos (B-17, B-45). Se guarda como texto.
/// </summary>
public enum TipoContratoAcceso
{
    Fijo,
    Recurrente,
    Temporal,
    Puntual,
    Obra,
    Emergencia
}
