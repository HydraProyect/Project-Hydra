namespace CaeManager.Application.Common;

/// <summary>
/// Fase G: pedido explícito del usuario — una respuesta del módulo de
/// Comunicaciones debe salir **siempre** del buzón configurado del tenant,
/// nunca de una cuenta simulada o personal.
///
/// <c>ResponderConversacionCommandHandler</c> tenía un remitente ficticio
/// (<c>equipo-cae@buzon-simulado.local</c>) para cuando la conversación no
/// tiene un buzón real conectado (<c>ConexionIntegracionId</c> nulo — hoy
/// solo ocurre con datos sembrados por <c>ComunicacionesDatosPruebaSeeder</c>),
/// sin ninguna guarda de entorno: fallaría silenciosamente igual en
/// producción que en una demo. Este interruptor lo restringe a un
/// entorno que lo active a propósito.
///
/// <b>Apagado por defecto</b> — mismo criterio que <c>RetencionDatosOptions</c>:
/// el comportamiento seguro por defecto es fallar con un mensaje claro
/// ("conecta un buzón"), no simular un envío que nunca llegó a nadie.
/// </summary>
public class ComunicacionesRemitenteOptions
{
    public const string SeccionConfiguracion = "Comunicaciones";

    public bool PermitirRemitenteSimulado { get; set; }
}
