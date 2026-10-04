using CaeManager.Domain.Visitas;
using CaeManager.Web.Components.DesignSystem;

namespace CaeManager.Web.Features.Visitas;

/// <summary>
/// Traduce la matriz de antelación a etiquetas y tonos — mismo espíritu que
/// <see cref="NivelUrgenciaVisitaUi"/>. Los textos de atribución están escritos
/// desde el punto de vista de quien defiende la gestión: dicen qué pasó, no
/// acusan a nadie.
/// </summary>
public static class AntelacionVisitaUi
{
    public static TonoBadge Tono(TramoAntelacion tramo) => tramo switch
    {
        TramoAntelacion.Expres => TonoBadge.Peligro,
        TramoAntelacion.Urgente => TonoBadge.Advertencia,
        _ => TonoBadge.Neutro
    };

    public static string Texto(TramoAntelacion tramo) => tramo switch
    {
        TramoAntelacion.Expres => "Exprés",
        TramoAntelacion.Urgente => "Urgente",
        _ => "Estándar"
    };

    public static string TextoAtribucion(AtribucionUrgencia atribucion) => atribucion switch
    {
        AtribucionUrgencia.SolicitudTardiaCliente => "El aviso llegó dentro de la ventana de urgencia",
        AtribucionUrgencia.DocumentacionTardiaCliente => "Se avisó con tiempo, pero la documentación llegó al filo",
        AtribucionUrgencia.RetrasoGestor => "Retraso en la gestión",
        _ => "Hubo margen suficiente"
    };

    /// <summary>Horas con un decimal y sin ceros de relleno — "15 h", "71,5 h".</summary>
    public static string Horas(decimal? horas) => horas is null ? "—" : $"{horas.Value:0.#} h";

    /// <summary>
    /// Cuánto falta para la Visita, en días de negocio (Europe/Madrid): el
    /// llamador pasa <c>DiaDeNegocio.Hoy()</c>, no la fecha UTC. Solo lee las
    /// fechas de la Visita; no cambia su semántica.
    /// </summary>
    public static (PlazoVisita Plazo, int Dias) Plazo(DateOnly inicio, DateOnly fin, DateOnly hoy)
    {
        if (fin < hoy) return (PlazoVisita.Finalizada, 0);
        if (inicio == hoy) return (PlazoVisita.Hoy, 0);
        if (inicio < hoy) return (PlazoVisita.EnCurso, 0);
        var dias = inicio.DayNumber - hoy.DayNumber;
        return dias == 1 ? (PlazoVisita.Manana, 1) : (PlazoVisita.EnDias, dias);
    }
}

public enum PlazoVisita { Finalizada, EnCurso, Hoy, Manana, EnDias }
