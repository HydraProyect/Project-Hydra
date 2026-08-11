namespace CaeManager.Domain.Telemetria;

/// <summary>Por qué se cerró un tramo de medición. Sirve para leer los datos con criterio: un tramo cerrado por inactividad no dice lo mismo que uno cerrado al enviar una respuesta.</summary>
public enum MotivoCierreSesionGestion
{
    /// <summary>El Gestor completó una acción sobre el hilo: responder, confirmar una sugerencia, cambiar el estado.</summary>
    AccionCompletada = 0,

    /// <summary>El Gestor pulsó "Pausar / Tarea externa" — una llamada, una reunión, un descanso.</summary>
    PausaManual = 1,

    /// <summary>La pestaña dejó de tener el foco, o el tramo superó el tope duro sin cerrarse.</summary>
    Inactividad = 2,

    /// <summary>Se cerró el hilo o se navegó a otra pantalla sin completar ninguna acción.</summary>
    NavegacionFuera = 3,

    /// <summary>Se perdió el circuito (cierre de pestaña, caída de red). El tramo se guarda igual para no perder trabajo real ya hecho.</summary>
    CierreCircuito = 4
}
