using CaeManager.Domain.Common;

namespace CaeManager.Domain.Plataforma;

/// <summary>
/// La activación puntual de una <see cref="ConcesionPrivilegio"/> sobre un
/// tenant concreto — efímera, motivada y auditada (ADR-011 § 8.1).
///
/// Separada del grant a propósito: tener la capacidad y ejercerla son dos actos
/// distintos, y solo el segundo abre datos de un cliente. Exige <b>motivo</b> y
/// <b>ventana</b> porque son lo que permite responder después "entramos el día
/// X por la incidencia Y", que es la pregunta que un cliente tiene derecho a
/// hacer sobre sus propios datos.
///
/// <b>Un tenant concreto, nunca "todos"</b>: aunque la concesión sea global, la
/// sesión apunta a uno. Una sesión sin objetivo definido sería indistinguible
/// de un acceso general permanente, que es justo lo que este plano evita.
/// </summary>
public class SesionPrivilegiada : Entity, IVersionable
{
    public const int LongitudMaximaMotivo = 500;
    public const int LongitudMaximaTicket = 100;

    public Guid ConcesionPrivilegioId { get; private set; }

    /// <summary>El tenant cuyos datos se abren. Uno, y elegido al abrir la sesión.</summary>
    public Guid TenantObjetivoId { get; private set; }

    /// <summary>
    /// A quién se simula. Solo puede estar informado si la capacidad es
    /// <see cref="CapacidadPrivilegio.Impersonacion"/>: en cualquier otra sería
    /// una impersonación encubierta, sin la ceremonia que la acompaña.
    /// </summary>
    public Guid? UsuarioSimuladoId { get; private set; }

    /// <summary>Por qué se abre. Obligatorio, sin excepción.</summary>
    public string Motivo { get; private set; } = string.Empty;

    /// <summary>Incidencia o ticket que la justifica, cuando exista.</summary>
    public string? Ticket { get; private set; }

    public DateTime InicioEnUtc { get; private set; }

    /// <summary>
    /// Cuándo deja de valer sola. Obligatorio: una sesión privilegiada sin
    /// caducidad es un acceso permanente con otro nombre.
    /// </summary>
    public DateTime ExpiraEnUtc { get; private set; }

    /// <summary>Cuándo se cerró de verdad, por cierre explícito o por caducidad.</summary>
    public DateTime? CerradaEnUtc { get; private set; }

    public Guid Version { get; private set; } = Guid.NewGuid();

    public bool EstaAbierta => CerradaEnUtc is null;

    private SesionPrivilegiada()
    {
        // Requerido por EF Core.
    }

    private SesionPrivilegiada(
        Guid concesionPrivilegioId,
        Guid tenantObjetivoId,
        Guid? usuarioSimuladoId,
        string motivo,
        string? ticket,
        DateTime inicioEnUtc,
        DateTime expiraEnUtc)
    {
        if (concesionPrivilegioId == Guid.Empty)
            throw new ArgumentException("La sesión debe colgar de una concesión.", nameof(concesionPrivilegioId));
        if (tenantObjetivoId == Guid.Empty)
            throw new ArgumentException("La sesión debe apuntar a un tenant concreto.", nameof(tenantObjetivoId));
        if (string.IsNullOrWhiteSpace(motivo))
            throw new ArgumentException("Una sesión privilegiada exige motivo.", nameof(motivo));
        if (motivo.Length > LongitudMaximaMotivo)
            throw new ArgumentException($"El motivo no puede superar {LongitudMaximaMotivo} caracteres.", nameof(motivo));
        if (ticket is { Length: > LongitudMaximaTicket })
            throw new ArgumentException($"El ticket no puede superar {LongitudMaximaTicket} caracteres.", nameof(ticket));
        if (expiraEnUtc <= inicioEnUtc)
            throw new ArgumentException("La ventana debe terminar después de empezar.", nameof(expiraEnUtc));

        ConcesionPrivilegioId = concesionPrivilegioId;
        TenantObjetivoId = tenantObjetivoId;
        UsuarioSimuladoId = usuarioSimuladoId;
        Motivo = motivo.Trim();
        Ticket = string.IsNullOrWhiteSpace(ticket) ? null : ticket.Trim();
        InicioEnUtc = inicioEnUtc;
        ExpiraEnUtc = expiraEnUtc;
    }

    /// <summary>
    /// Abre una sesión contra una concesión, comprobando aquí mismo las tres
    /// cosas que nadie debería poder saltarse: que la concesión cubra ese
    /// tenant en ese instante, que solo se simule bajo la capacidad de
    /// impersonación, y que la ventana sea finita.
    /// </summary>
    public static SesionPrivilegiada Abrir(
        ConcesionPrivilegio concesion,
        Guid tenantObjetivoId,
        string motivo,
        DateTime ahora,
        TimeSpan ventana,
        Guid? usuarioSimuladoId = null,
        string? ticket = null)
    {
        ArgumentNullException.ThrowIfNull(concesion);

        if (!concesion.CubreEn(tenantObjetivoId, ahora))
            throw new InvalidOperationException(
                "La concesión no cubre ese tenant en este momento: puede estar revocada, caducada o fuera de alcance.");

        if (usuarioSimuladoId is not null && concesion.Capacidad != CapacidadPrivilegio.Impersonacion)
            throw new InvalidOperationException(
                "Solo una concesión de impersonación puede simular a un usuario.");

        if (usuarioSimuladoId == Guid.Empty)
            throw new ArgumentException("El usuario simulado no puede ser vacío.", nameof(usuarioSimuladoId));

        if (ventana <= TimeSpan.Zero)
            throw new ArgumentException("La ventana debe ser positiva.", nameof(ventana));

        return new SesionPrivilegiada(
            concesion.Id, tenantObjetivoId, usuarioSimuladoId, motivo, ticket, ahora, ahora + ventana);
    }

    /// <summary>
    /// Si la sesión está viva en ese instante. La caducidad se comprueba aquí y
    /// no solo al cerrarla: una ventana vencida deja de valer aunque nadie haya
    /// pasado a cerrarla todavía.
    /// </summary>
    public bool EstaVigenteEn(DateTime momento) =>
        CerradaEnUtc is null && InicioEnUtc <= momento && momento < ExpiraEnUtc;

    public void Cerrar(DateTime ahora)
    {
        if (CerradaEnUtc is not null)
            throw new InvalidOperationException("La sesión ya estaba cerrada.");

        CerradaEnUtc = ahora > InicioEnUtc ? ahora : InicioEnUtc;
    }
}
