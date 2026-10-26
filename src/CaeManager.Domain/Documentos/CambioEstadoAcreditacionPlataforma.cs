using CaeManager.Domain.Common;

namespace CaeManager.Domain.Documentos;

/// <summary>
/// Un cambio de estado de una <see cref="AcreditacionDocumentoPlataforma"/> anotado a mano por el Gestor CAE: ha subido
/// el documento a la plataforma CAE del Cliente (o la plataforma lo ha validado o rechazado) y lo anota en TALVEG, desde
/// Centro 360 o con «Lo he enviado» de la extensión de navegador (<see cref="Origen"/>). La acreditación ya es de un
/// Centro (su canal de gestión es del Centro), así que el cambio solo afecta a ese Centro.
///
/// <para>
/// Guarda lo que se anotó (estado, fecha, motivo del rechazo, nota, usuario, momento y origen) y lo que había antes
/// (estado y vigencia en la plataforma), para que «Deshacer» devuelva el valor exacto sin fiarse de lo que mande el
/// llamador. Nunca se borra: un cambio deshecho queda marcado con <see cref="DeshechoUtc"/> y sigue en el Historial del
/// Centro. Solo <see cref="AcreditacionDocumentoPlataforma.CambiarEstadoAMano"/> crea instancias.
/// </para>
/// </summary>
public class CambioEstadoAcreditacionPlataforma : EntidadConTenant
{
    public const int LongitudMaximaNota = 500;

    public Guid AcreditacionId { get; private set; }
    public EstadoAcreditacion EstadoAnterior { get; private set; }
    public EstadoVigenciaEnPlataforma EstadoVigenciaAnterior { get; private set; }
    public DateOnly? FechaVencimientoAnterior { get; private set; }
    public EstadoAcreditacion Estado { get; private set; }

    /// <summary>El día en que se subió, se validó o se rechazó en la plataforma; lo dice el Gestor CAE y por defecto es hoy.</summary>
    public DateOnly Fecha { get; private set; }

    /// <summary>Solo con <see cref="EstadoAcreditacion.Rechazada"/>, y entonces obligatorio: lo que verá quien corrija el documento.</summary>
    public string? Motivo { get; private set; }

    /// <summary>Opcional, p. ej. el identificador de la solicitud en la plataforma.</summary>
    public string? Nota { get; private set; }

    /// <summary>Quien figura como autor (en una impersonación, el usuario simulado, como en la auditoría).</summary>
    public Guid? UsuarioId { get; private set; }

    public DateTime MomentoUtc { get; private set; }
    public DateTime? DeshechoUtc { get; private set; }

    /// <summary>Desde dónde se anotó: Centro 360 o «Lo he enviado» de la extensión. Se ve en el Historial del Centro.</summary>
    public OrigenCambioEstadoAcreditacion Origen { get; private set; }

    public bool EstaDeshecho => DeshechoUtc is not null;

    private CambioEstadoAcreditacionPlataforma()
    {
    }

    internal CambioEstadoAcreditacionPlataforma(
        Guid acreditacionId, EstadoAcreditacion estadoAnterior, VigenciaEnPlataforma vigenciaAnterior,
        EstadoAcreditacion estado, DateOnly fecha, string? motivo, string? nota, Guid? usuarioId, DateTime momentoUtc,
        OrigenCambioEstadoAcreditacion origen)
    {
        if (acreditacionId == Guid.Empty)
            throw new ArgumentException("El cambio debe pertenecer a una acreditación.", nameof(acreditacionId));
        if (!Enum.IsDefined(origen))
            throw new ArgumentOutOfRangeException(nameof(origen), "Origen del cambio desconocido.");

        var motivoNormalizado = string.IsNullOrWhiteSpace(motivo) ? null : motivo.Trim();
        if (estado == EstadoAcreditacion.Rechazada && motivoNormalizado is null)
            throw new ArgumentException("Un rechazo exige el motivo.", nameof(motivo));
        if (motivoNormalizado is { Length: > RechazoAcreditacionDocumentoPlataforma.LongitudMaximaMotivoLiteral })
            throw new ArgumentException(
                $"El motivo no puede superar {RechazoAcreditacionDocumentoPlataforma.LongitudMaximaMotivoLiteral} caracteres.", nameof(motivo));

        var notaNormalizada = string.IsNullOrWhiteSpace(nota) ? null : nota.Trim();
        if (notaNormalizada is { Length: > LongitudMaximaNota })
            throw new ArgumentException($"La nota no puede superar {LongitudMaximaNota} caracteres.", nameof(nota));

        AcreditacionId = acreditacionId;
        EstadoAnterior = estadoAnterior;
        EstadoVigenciaAnterior = vigenciaAnterior.Estado;
        FechaVencimientoAnterior = vigenciaAnterior.FechaVencimiento;
        Estado = estado;
        Fecha = fecha;
        // El motivo solo describe un rechazo: en otro estado no se guarda, para que el texto bajo la pastilla no lo arrastre.
        Motivo = estado == EstadoAcreditacion.Rechazada ? motivoNormalizado : null;
        Nota = notaNormalizada;
        UsuarioId = usuarioId;
        MomentoUtc = momentoUtc;
        Origen = origen;
    }

    internal VigenciaEnPlataforma VigenciaAnterior =>
        VigenciaEnPlataforma.Rehidratar(EstadoVigenciaAnterior, FechaVencimientoAnterior);

    internal void MarcarDeshecho(DateTime momentoUtc) => DeshechoUtc = momentoUtc;
}
