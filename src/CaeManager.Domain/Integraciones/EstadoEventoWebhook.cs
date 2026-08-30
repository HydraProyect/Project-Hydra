namespace CaeManager.Domain.Integraciones;

/// <summary>
/// Reemplaza al antiguo <c>bool Procesado</c> (auditoría de colas,
/// 2026-08-30): un único booleano no distinguía "terminó bien" de "se dio
/// por perdido tras agotar <see cref="EventoWebhook.MaximoIntentos"/>" —
/// ambos ponían <c>Procesado=true</c> por igual. Un monitor que filtrara por
/// ese booleano no podía distinguir un poison message descartado de uno
/// procesado con éxito.
/// </summary>
public enum EstadoEventoWebhook
{
    Pendiente,
    Procesando,
    Completado,
    DescartadoDefinitivo,
}
