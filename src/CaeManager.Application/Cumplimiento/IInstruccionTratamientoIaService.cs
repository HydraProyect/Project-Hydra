namespace CaeManager.Application.Cumplimiento;

/// <summary>
/// Único punto de consulta del Nivel 0 (DEC-33, REC-035): ¿tiene este Tenant
/// propietario una instrucción documentada vigente que autorice tratamiento
/// de datos personales mediante IA? Los nueve consumidores de
/// <c>Project-Hydra-Negocio/tecnico/docs/POLITICA-TECNICA-IA.md</c> § 4.4 lo llaman directamente
/// mientras REC-104 (gateway común de IA) no exista — cuando se construya,
/// DEC-46 fija que el gateway consulta esta interfaz una sola vez en su punto
/// de entrada. Los cinco del diseño original: verificación, detección de
/// trabajadores, detección previa de campos, detección de actualización desde
/// adjunto, y el chat "Pregúntale a Hydra". Los cuatro que el documento no
/// había medido y que llamaban al proveedor sin consultar nada hasta
/// 2026-09-21: detección de campos de Plantilla (rama PdfVisual), y las tres
/// detecciones que la ingesta de correo dispara sobre cada mensaje entrante
/// —relevancia CAE, sugerencia de visita y sugerencia de gestión—.
///
/// Que no vuelva a haber un décimo consumidor sin gate no lo sostiene este
/// comentario, sino el ratchet <c>ConsumidoresDeIaConsultanLaInstruccionDeTratamientoTests</c>:
/// toda clase de Application que reciba un puerto de IA tiene que recibir
/// también esta interfaz.
///
/// Desde la #2010 (D-7), también es el punto donde una objeción a un subencargado
/// que se excluye por la IA apaga la IA para ese Tenant propietario: habilitada
/// es «instrucción vigente y sin esa exclusión». Hoy hay un solo canal de IA, así
/// que excluir el subencargado de IA apaga toda la IA del Tenant.
///
/// Nunca sabe nada de proveedor, modelo, región ni retención — eso es
/// política técnica común de plataforma (§ 2 del documento de arriba), un
/// plano distinto que esta interfaz no toca.
/// </summary>
public interface IInstruccionTratamientoIaService
{
    Task<bool> EstaHabilitadaAsync(Guid tenantId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Solo la instrucción, sin la exclusión por objeción: para decirle a quien mira por qué la IA está apagada
    /// («sin instrucción» frente a «desactivada por tu objeción a un subencargado»). Nunca decide si se llama al
    /// proveedor: eso es <see cref="EstaHabilitadaAsync"/>. Por defecto coincide con ella, que es lo que hace un
    /// doble que no modela objeciones.
    /// </summary>
    Task<bool> TieneInstruccionVigenteAsync(Guid tenantId, CancellationToken cancellationToken = default) =>
        EstaHabilitadaAsync(tenantId, cancellationToken);
}
