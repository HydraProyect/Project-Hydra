using CaeManager.Domain.Common;

namespace CaeManager.Domain.Comunicaciones;

/// <summary>
/// Candidato de visita detectado por lectura IA del cuerpo de un
/// Mensaje entrante — mismo patrón de sugerencia-nunca-automática que
/// <see cref="Trabajadores.DeteccionTrabajador"/>: si el correo parece pedir
/// agendar la entrada de trabajadores a un Centro, se registra aquí en lugar
/// de crear la Visita directamente. El Gestor decide desde la Bandeja (botón
/// "Crear visita" que abre /visitas prellenado con estos datos) — nunca se
/// crea una Visita sin esa confirmación explícita. <see cref="CentroId"/> es
/// null cuando la IA detecta intención de visita pero no puede resolver con
/// certeza a qué Centro del Cliente se refiere (<see cref="Resumen"/>
/// explica el porqué, y el Gestor elige el centro a mano en el formulario).
/// Nunca se genera para mensajes Salientes ni para conversaciones sin
/// Cliente asignado — no hay Centro candidato sin Cliente.
/// </summary>
public class SugerenciaVisitaCorreo : EntidadConTenant
{
    public const int LongitudMaximaResumen = 500;

    public Guid MensajeId { get; private set; }
    public Guid? CentroId { get; private set; }
    public DateOnly? FechaInicioSugerida { get; private set; }
    public DateOnly? FechaFinSugerida { get; private set; }
    public string Resumen { get; private set; } = string.Empty;

    /// <summary>Confianza agregada de la IA (0-100) — la que se muestra en la cabecera de la tarjeta del Action Center. Sin banda calculada aquí (docs/COMUNICACIONES.md § 12.6 define los cortes: alta ≥90 / media 70-89 / baja &lt;70, es responsabilidad de quien la presenta).</summary>
    public int Confianza { get; private set; }

    /// <summary>Confianza específica de Centro (0-100) — decide si el campo se muestra editable en la revisión (§ 33: la UI no decide fiabilidad, solo representa el estado recibido).</summary>
    public int ConfianzaCentro { get; private set; }

    /// <summary>Confianza específica del par de fechas (0-100) — se piden juntas en el mismo prompt, así que comparten una confianza.</summary>
    public int ConfianzaFechas { get; private set; }

    public bool Resuelta { get; private set; }
    public DateTime CreadaEnUtc { get; private set; } = DateTime.UtcNow;

    private SugerenciaVisitaCorreo()
    {
    }

    public SugerenciaVisitaCorreo(
        Guid mensajeId, Guid? centroId, DateOnly? fechaInicioSugerida, DateOnly? fechaFinSugerida, string resumen,
        int confianza, int confianzaCentro, int confianzaFechas)
    {
        if (mensajeId == Guid.Empty)
            throw new ArgumentException("La sugerencia debe estar ligada a un mensaje.", nameof(mensajeId));
        if (string.IsNullOrWhiteSpace(resumen))
            throw new ArgumentException("La sugerencia debe explicar qué se detectó.", nameof(resumen));
        if (fechaInicioSugerida is not null && fechaFinSugerida is not null && fechaFinSugerida < fechaInicioSugerida)
            throw new ArgumentException("La fecha de fin sugerida no puede ser anterior a la de inicio.", nameof(fechaFinSugerida));
        if (confianza is < 0 or > 100)
            throw new ArgumentOutOfRangeException(nameof(confianza), confianza, "La confianza debe estar entre 0 y 100.");
        if (confianzaCentro is < 0 or > 100)
            throw new ArgumentOutOfRangeException(nameof(confianzaCentro), confianzaCentro, "La confianza debe estar entre 0 y 100.");
        if (confianzaFechas is < 0 or > 100)
            throw new ArgumentOutOfRangeException(nameof(confianzaFechas), confianzaFechas, "La confianza debe estar entre 0 y 100.");

        MensajeId = mensajeId;
        CentroId = centroId;
        FechaInicioSugerida = fechaInicioSugerida;
        FechaFinSugerida = fechaFinSugerida;
        Confianza = confianza;
        ConfianzaCentro = confianzaCentro;
        ConfianzaFechas = confianzaFechas;

        var normalizado = resumen.Trim();
        Resumen = normalizado.Length > LongitudMaximaResumen ? normalizado[..LongitudMaximaResumen] : normalizado;
    }

    /// <summary>Se llama tanto si el Gestor usó la sugerencia para crear la Visita como si la descartó — no distinguimos la acción tomada, a diferencia de DeteccionTrabajador, porque aquí no hay un roster que auditar.</summary>
    public void Resolver() => Resuelta = true;
}
