namespace CaeManager.Domain.Comunicaciones;

/// <summary>
/// Estado de negocio de una Conversacion dentro de la bandeja
/// compartida (ver Project-Hydra-Negocio/tecnico/ARQUITECTURA-INTEGRACIONES.md § 12). Abierta es el valor
/// por defecto al crear un hilo nuevo.
///
/// Los valores numéricos son explícitos porque la columna guarda el entero: los dos estados de espera se
/// añadieron después de Cerrada (decisión de producto 2026-10-17, hueco 2 de Comunicaciones) y reordenar el
/// enum cambiaría el significado de las filas ya guardadas. Qué estado puede seguir a cuál lo decide
/// <see cref="TransicionesEstadoConversacion"/>, no el orden de este enum.
/// </summary>
public enum EstadoConversacion
{
    Abierta = 0,

    /// <summary>
    /// Estado previo a los dos de espera: el Gestor CAE tiene el hilo en curso y todavía sin resolver. Se
    /// conserva tal cual (sigue contando como hilo vivo); la espera con motivo se expresa con
    /// <see cref="EsperandoRespuestaExterna"/> y <see cref="EnEsperaDeTercero"/>.
    /// </summary>
    Pendiente = 1,

    Resuelta = 2,
    Cerrada = 3,

    /// <summary>
    /// Esperamos la respuesta del interlocutor externo del hilo (equivale al «Pending» de Zendesk). En Outbound quien
    /// responde es la Empresa gestionada, no el Cliente empresarial, de ahí el nombre «respuesta externa». Un mensaje
    /// entrante del interlocutor la devuelve a <see cref="Abierta"/>.
    /// </summary>
    EsperandoRespuestaExterna = 4,

    /// <summary>
    /// Esperamos a un tercero ajeno al hilo — el Centro, la mutua o el servicio de prevención (equivale al
    /// «On-hold» de Zendesk). Un mensaje entrante NO la mueve: no se sabe de quién viene, así que la saca el
    /// Gestor CAE.
    /// </summary>
    EnEsperaDeTercero = 5
}
