namespace CaeManager.Domain.Visitas;

/// <summary>Cómo llegó la petición de agendar la visita — no cómo se dio de alta en Hydra (ambas rutas usan el mismo formulario/Command).</summary>
public enum OrigenVisita
{
    Plataforma,
    Correo,

    /// <summary>Fase F: "visita sorpresa" detectada por WhatsApp — mismo mecanismo de sugerencia que Correo (ver SugerenciaVisitaCorreo, que hereda el canal a través de Conversacion.Canal), añadido al final para no renumerar los valores ya persistidos.</summary>
    WhatsApp,

    /// <summary>Visita dada de alta a mano por una persona desde el formulario «Nueva visita» (D-21). Añadido al final para no renumerar los valores ya persistidos: la columna es un entero sin CHECK ni conversión. Las visitas anteriores conservan su origen, aunque se crearan a mano como Plataforma.</summary>
    Manual
}
