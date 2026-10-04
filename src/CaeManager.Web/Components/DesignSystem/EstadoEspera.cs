namespace CaeManager.Web.Components.DesignSystem;

/// <summary>Cómo va la espera de una operación que el usuario ha lanzado desde un botón (<see cref="BotonConEspera"/>).</summary>
public enum EstadoEspera
{
    /// <summary>Nada en curso: el botón muestra su etiqueta.</summary>
    Reposo,

    /// <summary>En curso y dentro de lo habitual: relleno con porcentaje si se conoce el total, franja en movimiento si no.</summary>
    Guardando,

    /// <summary>Pasado el umbral de lentitud, con progreso aún: se avisa de que tarda y se ofrece cancelar.</summary>
    Lenta,

    /// <summary>Sin progreso durante el segundo umbral: aviso de que no hay respuesta y, si el llamador lo permite, reintentar.</summary>
    Colgada,

    /// <summary>Terminó bien: check que se dibuja, un instante, y vuelta al reposo.</summary>
    Hecha
}

/// <summary>
/// Regla única de la espera, aparte del componente para poder medirla sin reloj ni renderizado: dado cuánto lleva la operación
/// y cuánto sin dar señales, en qué estado está. La lentitud se mide desde el inicio; el colgado, desde el último progreso
/// (sin total conocido no hay progreso que ver, y cuenta desde el inicio).
/// </summary>
public static class ClasificadorEspera
{
    public static EstadoEspera Clasificar(bool enCurso, TimeSpan transcurrido, TimeSpan sinProgreso, TimeSpan umbralLenta, TimeSpan umbralColgada)
    {
        if (!enCurso) return EstadoEspera.Reposo;
        if (sinProgreso >= umbralColgada) return EstadoEspera.Colgada;
        return transcurrido >= umbralLenta ? EstadoEspera.Lenta : EstadoEspera.Guardando;
    }
}
