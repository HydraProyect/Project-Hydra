using System.Net;

namespace CaeManager.Infrastructure.AsistenteIa;

/// <summary>
/// Decide si una respuesta fallida de un proveedor de IA se puede reintentar
/// sin arriesgarse a pagarla dos veces.
///
/// Vive aparte del registro del cliente HTTP para que sea comprobable: metido
/// en el lambda de <c>Retry.ShouldHandle</c>, la regla más cara del pipeline
/// —la que decide si una petición de pago se repite— no tendría ningún test
/// que la observara, y una relajación futura pasaría en verde.
/// </summary>
internal static class ReintentoProveedorIa
{
    /// <summary>
    /// Solo el <c>429</c>. Es la única respuesta que dice explícitamente que la
    /// petición NO llegó a procesarse, así que repetirla no puede duplicar un
    /// cobro ni volver a transmitir el documento; además trae
    /// <c>Retry-After</c>, que el manejador estándar respeta.
    ///
    /// Todo lo demás devuelve <c>false</c>, y cada caso por su motivo:
    ///
    /// <list type="bullet">
    /// <item><b>5xx</b>: el proveedor puede haber procesado la petición y
    /// fallado al responder. Indistinguible desde aquí de no haberla procesado.</item>
    /// <item><b>Timeout</b> (respuesta nula porque saltó la excepción): el caso
    /// más peligroso de todos — cuanto más tarda un modelo, más probable es que
    /// esté trabajando de verdad, así que el timeout correlaciona con "ya se
    /// está cobrando".</item>
    /// <item><b>Error de red</b>: <c>HttpRequestException</c> no distingue "no
    /// conecté" de "envié y perdí la respuesta".</item>
    /// <item><b>408</b>: lo genera el servidor tras recibir la petición.</item>
    /// </list>
    ///
    /// No perder resiliencia por esto es responsabilidad de las capas de
    /// arriba, donde el reintento sí queda registrado: el fallback al siguiente
    /// proveedor del router (que además cambia de destinatario, así que no
    /// repite una petición quizá ya cobrada) y los intentos de
    /// <c>TrabajoAnalisisDocumento</c>.
    /// </summary>
    public static bool EsSeguroReintentar(HttpResponseMessage? respuesta) =>
        respuesta?.StatusCode == HttpStatusCode.TooManyRequests;
}
