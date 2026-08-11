using CaeManager.Domain.Common;

namespace CaeManager.Domain.Comunicaciones;

/// <summary>
/// Candidato de actualización documental (p. ej. EPI) detectado por lectura IA del cuerpo de un
/// Mensaje entrante — mismo patrón de sugerencia-nunca-automática que
/// <see cref="SugerenciaVisitaCorreo"/>: si el correo parece pedir renovar/entregar TipoDocumento
/// de uno o varios Trabajadores, se registra aquí en lugar de crear ninguna Gestion directamente.
///
/// Un correo no siempre trata un único trabajador/documento — una misma notificación en bloque
/// puede listar el estado de varios a la vez (ronda de reducción de ruido en Comunicaciones), así
/// que <see cref="Detalles"/> es una colección: cada ítem se revisa y se resuelve
/// independientemente desde la Bandeja (botón "Generar gestiones" por ítem, que crea una Gestion
/// por cada Centro donde ese Trabajador esté de alta — ver CrearGestionesParaTrabajadorCommand).
/// <see cref="Resumen"/>/<see cref="Confianza"/> son del mensaje completo, no de un ítem — la
/// certeza específica de cada Trabajador/TipoDocumento vive en su
/// <see cref="DetalleSugerenciaGestionCorreo"/>.
/// </summary>
public class SugerenciaGestionCorreo : EntidadConTenant
{
    public const int LongitudMaximaResumen = 500;

    private readonly List<DetalleSugerenciaGestionCorreo> _detalles = [];

    public Guid MensajeId { get; private set; }
    public string Resumen { get; private set; } = string.Empty;

    /// <summary>Confianza agregada de la IA (0-100) sobre el mensaje completo — mismo criterio que SugerenciaVisitaCorreo.Confianza.</summary>
    public int Confianza { get; private set; }

    public DateTime CreadaEnUtc { get; private set; } = DateTime.UtcNow;

    public IReadOnlyList<DetalleSugerenciaGestionCorreo> Detalles => _detalles.AsReadOnly();

    private SugerenciaGestionCorreo()
    {
    }

    public SugerenciaGestionCorreo(Guid mensajeId, string resumen, int confianza)
    {
        if (mensajeId == Guid.Empty)
            throw new ArgumentException("La sugerencia debe estar ligada a un mensaje.", nameof(mensajeId));
        if (string.IsNullOrWhiteSpace(resumen))
            throw new ArgumentException("La sugerencia debe explicar qué se detectó.", nameof(resumen));
        if (confianza is < 0 or > 100)
            throw new ArgumentOutOfRangeException(nameof(confianza), confianza, "La confianza debe estar entre 0 y 100.");

        MensajeId = mensajeId;
        Confianza = confianza;

        var normalizado = resumen.Trim();
        Resumen = normalizado.Length > LongitudMaximaResumen ? normalizado[..LongitudMaximaResumen] : normalizado;
    }

    public DetalleSugerenciaGestionCorreo AgregarDetalle(
        Guid? trabajadorId, Guid? tipoDocumentoId, int confianzaTrabajador, int confianzaTipoDocumento)
    {
        var detalle = new DetalleSugerenciaGestionCorreo(Id, trabajadorId, tipoDocumentoId, confianzaTrabajador, confianzaTipoDocumento);
        _detalles.Add(detalle);
        return detalle;
    }
}

/// <summary>
/// Un ítem dentro de una <see cref="SugerenciaGestionCorreo"/> — un Trabajador y un TipoDocumento
/// candidatos, con su propia confianza y su propio estado de resolución. Null cuando la IA detecta
/// la intención de ese ítem pero no puede resolver con certeza a quién o a qué tipo de documento se
/// refiere (mismo criterio que <see cref="SugerenciaVisitaCorreo.CentroId"/>): el Gestor no puede
/// completar el ítem en ese caso, solo descartarlo — v1 exige que la IA identifique ambos datos.
/// </summary>
public class DetalleSugerenciaGestionCorreo : EntidadConTenant
{
    public Guid SugerenciaGestionCorreoId { get; private set; }
    public Guid? TrabajadorId { get; private set; }
    public Guid? TipoDocumentoId { get; private set; }

    /// <summary>Confianza específica de Trabajador (0-100).</summary>
    public int ConfianzaTrabajador { get; private set; }

    /// <summary>Confianza específica de TipoDocumento (0-100).</summary>
    public int ConfianzaTipoDocumento { get; private set; }

    public bool Resuelta { get; private set; }

    /// <summary>Qué hizo el Gestor con este ítem — ver <see cref="SugerenciaVisitaCorreo.Resolucion"/>.</summary>
    public ResolucionSugerencia? Resolucion { get; private set; }

    public DateTime? ResueltaEnUtc { get; private set; }
    public Guid? ResueltaPorUsuarioId { get; private set; }

    private DetalleSugerenciaGestionCorreo()
    {
    }

    public DetalleSugerenciaGestionCorreo(
        Guid sugerenciaGestionCorreoId, Guid? trabajadorId, Guid? tipoDocumentoId, int confianzaTrabajador, int confianzaTipoDocumento)
    {
        if (sugerenciaGestionCorreoId == Guid.Empty)
            throw new ArgumentException("El detalle debe pertenecer a una sugerencia.", nameof(sugerenciaGestionCorreoId));
        if (confianzaTrabajador is < 0 or > 100)
            throw new ArgumentOutOfRangeException(nameof(confianzaTrabajador), confianzaTrabajador, "La confianza debe estar entre 0 y 100.");
        if (confianzaTipoDocumento is < 0 or > 100)
            throw new ArgumentOutOfRangeException(nameof(confianzaTipoDocumento), confianzaTipoDocumento, "La confianza debe estar entre 0 y 100.");

        SugerenciaGestionCorreoId = sugerenciaGestionCorreoId;
        TrabajadorId = trabajadorId;
        TipoDocumentoId = tipoDocumentoId;
        ConfianzaTrabajador = confianzaTrabajador;
        ConfianzaTipoDocumento = confianzaTipoDocumento;
    }

    /// <summary>Cierra el ítem dejando constancia de qué se hizo con él — mismo criterio e idempotencia que <see cref="SugerenciaVisitaCorreo.Resolver"/>.</summary>
    public void Resolver(ResolucionSugerencia resolucion, Guid? usuarioId = null)
    {
        if (Resuelta) return;

        Resuelta = true;
        Resolucion = resolucion;
        ResueltaEnUtc = DateTime.UtcNow;
        ResueltaPorUsuarioId = usuarioId;
    }
}
