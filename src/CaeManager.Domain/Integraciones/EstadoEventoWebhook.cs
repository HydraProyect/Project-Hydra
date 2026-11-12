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

    /// <summary>
    /// Su conexión lleva sin identidad verificada más de <see cref="EventoWebhook.TopeEsperaVerificacion"/>: deja
    /// de reintentarse, pero no se descarta ni se redacta. Vuelve a <see cref="Pendiente"/> cuando la conexión se
    /// verifica otra vez (<see cref="EventoWebhook.Liberar"/>).
    /// </summary>
    Retenido,
}
