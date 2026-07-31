using CaeManager.Domain.Common;

namespace CaeManager.Domain.Retencion;

/// <summary>
/// Propuesta de destrucción de datos que ya cumplieron su plazo de retención,
/// pendiente de autorización humana.
///
/// Existe porque la purga <b>no debe ser un proceso automático</b> (decisión
/// del propietario del producto, 2026-07-31): antes de destruir nada hay que
/// poder avisar al tenant para que extraiga sus datos si su política interna
/// exige conservarlos más años que la de la plataforma. Un barrido que borrara
/// solo por haber vencido un plazo haría imposible esa conversación, y el dato
/// destruido no se recupera.
///
/// La invariante que sostiene todo esto vive en los métodos: no hay ningún
/// camino que lleve a <see cref="EstadoSolicitudPurga.Ejecutada"/> sin pasar
/// antes por <see cref="Programar"/>, que exige una fecha y una persona que
/// la autorice. El barrido solo sabe crear solicitudes en
/// <see cref="EstadoSolicitudPurga.PendienteDeRevision"/>.
///
/// Extiende <see cref="EntidadConTenant"/> —con filtro global como todo lo
/// demás— a propósito: cada tenant debe poder ver qué datos suyos están
/// propuestos para destrucción. Quién <b>autoriza</b> es otra cuestión, y
/// depende de una figura de administración de plataforma que hoy no existe
/// en el modelo de roles (ver docs/MULTITENANCY.md § 7: los seis roles son de
/// negocio dentro de un tenant).
/// </summary>
public class SolicitudPurga : EntidadConTenant
{
    public const int LongitudMaximaMotivo = 500;

    /// <summary>Qué se propone destruir. Hoy solo Documentos; el Trabajador como entidad sigue sin plazo decidido.</summary>
    public TipoDatoPurgable TipoDato { get; private set; }

    /// <summary>Cuántos registros entraban en la propuesta cuando se detectó.</summary>
    public int RegistrosAfectados { get; private set; }

    /// <summary>
    /// Fecha de corte: se propone destruir lo que cumplió plazo antes de
    /// esta fecha. Se conserva para que la ejecución sea reproducible y no
    /// dependa de cuándo acabe corriendo.
    /// </summary>
    public DateOnly FechaCorte { get; private set; }

    public EstadoSolicitudPurga Estado { get; private set; }

    public DateTime DetectadaEnUtc { get; private set; } = DateTime.UtcNow;

    /// <summary>Cuándo se comunicó al tenant, para dejar constancia de que tuvo ocasión de extraer sus datos.</summary>
    public DateTime? AvisadaAlTenantEnUtc { get; private set; }

    /// <summary>Fecha a partir de la cual puede ejecutarse. Null mientras nadie la haya autorizado.</summary>
    public DateOnly? FechaEjecucionProgramada { get; private set; }

    /// <summary>Quién autorizó. Guid suelto hacia ApplicationUser, mismo patrón que EliminadoPorUsuarioId.</summary>
    public Guid? AutorizadaPorUsuarioId { get; private set; }

    public DateTime? EjecutadaEnUtc { get; private set; }

    public string? Motivo { get; private set; }

    private SolicitudPurga()
    {
        // Requerido por EF Core.
    }

    public SolicitudPurga(TipoDatoPurgable tipoDato, int registrosAfectados, DateOnly fechaCorte)
    {
        if (registrosAfectados <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(registrosAfectados), "No tiene sentido una solicitud de purga sin registros afectados.");

        TipoDato = tipoDato;
        RegistrosAfectados = registrosAfectados;
        FechaCorte = fechaCorte;
        Estado = EstadoSolicitudPurga.PendienteDeRevision;
    }

    /// <summary>
    /// Deja constancia de que se ha avisado al tenant. Paso intermedio y no
    /// obligatorio en el código —forzarlo aquí impediría casos legítimos como
    /// un tenant que ya lo pidió por escrito—, pero sí el motivo de que este
    /// flujo exista.
    /// </summary>
    public void MarcarTenantAvisado()
    {
        ExigirQueSigaAbierta();

        AvisadaAlTenantEnUtc = DateTime.UtcNow;
        Estado = EstadoSolicitudPurga.TenantAvisado;
    }

    /// <summary>
    /// Autoriza la destrucción y fija cuándo puede ejecutarse. Es el único
    /// camino hacia <see cref="Ejecutar"/>.
    ///
    /// La fecha no puede ser pasada: programar "para ayer" equivaldría a
    /// ejecutar en el acto, que es justo lo que este flujo evita.
    /// </summary>
    public void Programar(DateOnly fechaEjecucion, Guid autorizadaPorUsuarioId, DateOnly hoy)
    {
        ExigirQueSigaAbierta();

        if (autorizadaPorUsuarioId == Guid.Empty)
            throw new ArgumentException(
                "La destrucción de datos tiene que quedar atribuida a quien la autoriza.", nameof(autorizadaPorUsuarioId));

        if (fechaEjecucion < hoy)
            throw new ArgumentException(
                "La fecha de ejecución no puede ser anterior a hoy.", nameof(fechaEjecucion));

        FechaEjecucionProgramada = fechaEjecucion;
        AutorizadaPorUsuarioId = autorizadaPorUsuarioId;
        Estado = EstadoSolicitudPurga.Programada;
    }

    /// <summary>
    /// Descarta la solicitud. Disponible mientras no se haya ejecutado —
    /// incluida una ya programada, que es el caso de un tenant que pide más
    /// plazo después de que se autorizara.
    /// </summary>
    public void Cancelar(string motivo)
    {
        if (Estado is EstadoSolicitudPurga.Ejecutada)
            throw new InvalidOperationException("Una purga ya ejecutada no se puede cancelar.");

        if (string.IsNullOrWhiteSpace(motivo))
            throw new ArgumentException("Cancelar una purga exige dejar dicho por qué.", nameof(motivo));

        Motivo = motivo.Length > LongitudMaximaMotivo ? motivo[..LongitudMaximaMotivo] : motivo;
        Estado = EstadoSolicitudPurga.Cancelada;
    }

    /// <summary>
    /// Marca la solicitud como ejecutada. Solo desde
    /// <see cref="EstadoSolicitudPurga.Programada"/> y solo llegada la fecha:
    /// son las dos condiciones que impiden que un barrido automático destruya
    /// nada por su cuenta.
    /// </summary>
    public void Ejecutar(DateOnly hoy)
    {
        if (Estado is not EstadoSolicitudPurga.Programada)
            throw new InvalidOperationException(
                "Solo puede ejecutarse una purga previamente autorizada y programada.");

        if (FechaEjecucionProgramada is not { } fecha || hoy < fecha)
            throw new InvalidOperationException(
                "Todavía no ha llegado la fecha de ejecución programada.");

        EjecutadaEnUtc = DateTime.UtcNow;
        Estado = EstadoSolicitudPurga.Ejecutada;
    }

    /// <summary>Si a día de hoy puede ejecutarse, sin lanzar — para que el barrido consulte antes de actuar.</summary>
    public bool PuedeEjecutarse(DateOnly hoy) =>
        Estado is EstadoSolicitudPurga.Programada &&
        FechaEjecucionProgramada is { } fecha &&
        hoy >= fecha;

    private void ExigirQueSigaAbierta()
    {
        if (Estado is EstadoSolicitudPurga.Ejecutada or EstadoSolicitudPurga.Cancelada)
            throw new InvalidOperationException($"La solicitud ya está {Estado} y no admite cambios.");
    }
}
