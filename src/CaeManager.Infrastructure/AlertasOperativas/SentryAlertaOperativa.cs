using CaeManager.Application.Common;
using Sentry;

namespace CaeManager.Infrastructure.AlertasOperativas;

/// <summary>
/// Implementación real de <see cref="IAlertaOperativa"/> (Horizonte 2.4 del
/// plan macro): un evento de Sentry por condición detectada, con el mismo
/// Hub global que <c>Program.cs</c> ya inicializa con <c>UseSentry(...)</c>.
/// No hace ninguna comprobación de "¿está configurado Sentry:Dsn?" — no
/// hace falta: el propio <c>SentrySdk</c> queda inerte (no envía nada, no
/// lanza) cuando el Hub se inicializó con un Dsn vacío, exactamente el mismo
/// patrón "funciona sin configurar" que el resto de integraciones opcionales
/// de este despliegue (ver el comentario de <c>UseSentry</c> en Program.cs).
/// Better Stack ya está enganchado a Sentry para la notificación real al
/// operador de guardia (RUNBOOK-ALERTAS.md, repositorio de negocio) — esta
/// clase es deliberadamente la única vía de alerta operativa nueva: no abre
/// un canal aparte.
/// </summary>
public class SentryAlertaOperativa : IAlertaOperativa
{
    public void Emitir(string mensaje, NivelAlertaOperativa nivel) =>
        SentrySdk.CaptureMessage(mensaje, NivelSentry(nivel));

    public void CapturarExcepcion(Exception excepcion) => SentrySdk.CaptureException(excepcion);

    private static SentryLevel NivelSentry(NivelAlertaOperativa nivel) => nivel switch
    {
        NivelAlertaOperativa.Critica => SentryLevel.Error,
        _ => SentryLevel.Warning,
    };
}
