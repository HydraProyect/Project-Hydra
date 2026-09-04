namespace CaeManager.Application.Cumplimiento;

/// <summary>
/// Único punto de consulta del Nivel 0 (DEC-33, REC-035): ¿tiene este Tenant
/// propietario una instrucción documentada vigente que autorice tratamiento
/// de datos personales mediante IA? Los cinco consumidores de
/// <c>tecnico/docs/POLITICA-TECNICA-IA.md</c> § 4.4 (verificación,
/// detección de trabajadores, detección previa de campos, detección de
/// actualización desde adjunto, y el chat "Pregúntale a Hydra") lo llaman
/// directamente mientras REC-104 (gateway común de IA) no exista — cuando se
/// construya, DEC-46 fija que el gateway consulta esta interfaz una sola vez
/// en su punto de entrada.
///
/// Nunca sabe nada de proveedor, modelo, región ni retención — eso es
/// política técnica común de plataforma (§ 2 del documento de arriba), un
/// plano distinto que esta interfaz no toca.
/// </summary>
public interface IInstruccionTratamientoIaService
{
    Task<bool> EstaHabilitadaAsync(Guid tenantId, CancellationToken cancellationToken = default);
}
