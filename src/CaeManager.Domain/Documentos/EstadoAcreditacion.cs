namespace CaeManager.Domain.Documentos;

/// <summary>
/// Estado de un Documento frente a una plataforma destino concreta
/// (docs/ux-audit/PLAN-EJECUCION-UX.md § Parte 2 (b)) — independiente del
/// estado de vigencia del propio Documento (<see cref="EstadoDocumento"/>):
/// un documento vigente en Hydra puede estar Rechazada en Nalanda a la vez.
/// </summary>
public enum EstadoAcreditacion
{
    PendienteDeSubir = 0,
    Subida = 1,
    Aceptada = 2,
    Rechazada = 3,
    NoRequerida = 4
}
