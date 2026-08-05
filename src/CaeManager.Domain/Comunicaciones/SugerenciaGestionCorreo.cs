using CaeManager.Domain.Common;

namespace CaeManager.Domain.Comunicaciones;

/// <summary>
/// Candidato de actualización documental (p. ej. EPI) detectado por lectura
/// IA del cuerpo de un MensajeCorreo entrante — mismo patrón de
/// sugerencia-nunca-automática que <see cref="SugerenciaVisitaCorreo"/>: si
/// el correo parece pedir renovar/entregar un TipoDocumento de un
/// Trabajador, se registra aquí en lugar de crear ninguna Gestion
/// directamente. El Gestor decide desde la Bandeja (botón "Generar
/// gestiones", que crea una Gestion por cada Centro donde el Trabajador esté
/// de alta — ver CrearGestionesParaTrabajadorCommand) — nunca se genera una
/// Gestion sin esa confirmación explícita.
///
/// <see cref="TrabajadorId"/>/<see cref="TipoDocumentoId"/> son null cuando
/// la IA detecta la intención pero no puede resolver con certeza a quién o a
/// qué tipo de documento se refiere (<see cref="Resumen"/> explica el
/// porqué) — el Gestor no puede completar la sugerencia en ese caso, solo
/// descartarla, porque a diferencia de Centro en SugerenciaVisitaCorreo aquí
/// no hay un formulario manual de respaldo: v1 exige que la IA identifique
/// ambos datos.
/// </summary>
public class SugerenciaGestionCorreo : EntidadConTenant
{
    public const int LongitudMaximaResumen = 500;

    public Guid MensajeCorreoId { get; private set; }
    public Guid? TrabajadorId { get; private set; }
    public Guid? TipoDocumentoId { get; private set; }
    public string Resumen { get; private set; } = string.Empty;
    public bool Resuelta { get; private set; }
    public DateTime CreadaEnUtc { get; private set; } = DateTime.UtcNow;

    private SugerenciaGestionCorreo()
    {
    }

    public SugerenciaGestionCorreo(Guid mensajeCorreoId, Guid? trabajadorId, Guid? tipoDocumentoId, string resumen)
    {
        if (mensajeCorreoId == Guid.Empty)
            throw new ArgumentException("La sugerencia debe estar ligada a un mensaje.", nameof(mensajeCorreoId));
        if (string.IsNullOrWhiteSpace(resumen))
            throw new ArgumentException("La sugerencia debe explicar qué se detectó.", nameof(resumen));

        MensajeCorreoId = mensajeCorreoId;
        TrabajadorId = trabajadorId;
        TipoDocumentoId = tipoDocumentoId;

        var normalizado = resumen.Trim();
        Resumen = normalizado.Length > LongitudMaximaResumen ? normalizado[..LongitudMaximaResumen] : normalizado;
    }

    /// <summary>Se llama tanto si el Gestor usó la sugerencia para generar gestiones como si la descartó — mismo criterio que SugerenciaVisitaCorreo.Resolver.</summary>
    public void Resolver() => Resuelta = true;
}
