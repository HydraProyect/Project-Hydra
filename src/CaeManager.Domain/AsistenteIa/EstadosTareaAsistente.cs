namespace CaeManager.Domain.AsistenteIa;

/// <summary>
/// Ciclo de vida de una <see cref="TareaAsistente"/>. Las transiciones las hace
/// cumplir la propia entidad; ningún llamador fija el estado a mano.
///
/// <code>
/// Conversando ──GuardarPlan──▶ PlanEnBorrador ◀──▶ PlanListo ──ConfirmarPlan──▶ Confirmada ──(todos ejecutados)──▶ Terminada
///      │                            │                  │                           │
///      └────────────────────────────┴──────Descartar───┴───────────────────────────┴──▶ Descartada
/// </code>
/// </summary>
public enum EstadoTareaAsistente
{
    /// <summary>Hay conversación, todavía ningún plan.</summary>
    Conversando = 0,

    /// <summary>
    /// Hay plan, pero no se puede confirmar: algún paso vivo sigue en
    /// <see cref="EstadoPasoTareaAsistente.Borrador"/> (le falta un dato o
    /// tiene un aviso bloqueante), o no queda ningún paso vivo.
    /// </summary>
    PlanEnBorrador = 1,

    /// <summary>Todos los pasos vivos están listos: el plan se puede confirmar entero.</summary>
    PlanListo = 2,

    /// <summary>
    /// La persona confirmó el plan entero. Solo en este estado se registra la
    /// ejecución de un paso, y el plan ya no se puede modificar.
    /// </summary>
    Confirmada = 3,

    /// <summary>Todos los pasos confirmados se ejecutaron. Terminal.</summary>
    Terminada = 4,

    /// <summary>
    /// La persona la abandonó. Terminal. Lo ya ejecutado sigue ejecutado —
    /// descartar no deshace nada en el dominio—; lo demás queda descartado.
    /// </summary>
    Descartada = 5,
}

/// <summary>Estado de un <see cref="PasoTareaAsistente"/> dentro del plan.</summary>
public enum EstadoPasoTareaAsistente
{
    /// <summary>
    /// Le falta algún dato (<see cref="PasoTareaAsistente.CamposPendientes"/>) o
    /// tiene un aviso bloqueante. Es el borrador reanudable: lo que falta vive
    /// aquí, nunca en una entidad de dominio a medio rellenar.
    /// </summary>
    Borrador = 0,

    /// <summary>Completo y sin avisos bloqueantes; pendiente de que se confirme el plan.</summary>
    Listo = 1,

    /// <summary>Incluido en un plan confirmado; pendiente de ejecutar.</summary>
    Confirmado = 2,

    /// <summary>Ejecutado por su Command. Terminal.</summary>
    Ejecutado = 3,

    /// <summary>
    /// La ejecución falló. Se puede reintentar: el paso es idempotente
    /// (su <see cref="Common.Entity.Id"/> es la clave de idempotencia).
    /// </summary>
    Fallido = 4,

    /// <summary>Retirado del plan por la persona, o por descartar la tarea. Terminal.</summary>
    Descartado = 5,
}

/// <summary>Quién produjo un turno de la conversación.</summary>
public enum AutorTurnoTareaAsistente
{
    /// <summary>La persona que usa el asistente (el Actor real de la tarea).</summary>
    Persona = 0,

    /// <summary>La respuesta que el asistente mostró a la persona.</summary>
    Asistente = 1,
}

/// <summary>
/// Gravedad de un aviso de validación determinista sobre un paso. Mismos dos
/// valores que el aviso de las validaciones deterministas de la preparación de
/// la orden: una advertencia se muestra y no impide confirmar; un bloqueante
/// deja el paso en borrador.
/// </summary>
public enum GravedadAvisoPasoTareaAsistente
{
    Advertencia = 0,
    Bloqueante = 1,
}
