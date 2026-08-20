using CaeManager.Domain.Common;

namespace CaeManager.Domain.Operaciones;

/// <summary>
/// Lo común a los dos niveles de asignación de responsabilidad operativa
/// (ADR-011 § 2.7): el ámbito, la vigencia y la máquina de estados. Base
/// abstracta <b>sin mapear</b> — cada nivel es su propia tabla, igual que
/// <c>EntidadConTenant</c>/<c>EntidadBase</c> son bases sin mapear del resto del
/// dominio.
///
/// Extiende <see cref="Entity"/> y no <see cref="EntidadBase"/> a propósito:
/// una asignación cruza fronteras de tenant por naturaleza (el operador puede
/// ser otro tenant), así que no puede llevar <c>TenantId</c> ni pasar por el
/// filtro global. Es un catálogo global de autorización, mismo tratamiento que
/// <c>Tenant</c> y <c>DelegacionTenant</c>. Que esté fuera del filtro
/// <b>no</b> la hace legible sin restricción: la política de lectura por
/// posición del llamante vive en Application y está cubierta por un test de
/// arquitectura.
///
/// <b>Append-only</b>: no hay ni un solo método que cambie el operador, el
/// servicio o el ámbito. Cambiar cualquiera de esas tres cosas es cerrar esta
/// fila y abrir otra — es lo que hace que "¿quién era responsable de este
/// ámbito el 15 de marzo?" tenga respuesta sin reconstruir eventos.
/// </summary>
public abstract class AsignacionResponsabilidad : Entity, IVersionable
{
    /// <summary>
    /// Tenant dueño de los datos sobre los que se opera. En las carteras está
    /// denormalizado desde su operación, pero <b>garantizado por la base de
    /// datos</b>: la FK compuesta (AsignacionOperacionId, PropietarioTenantId)
    /// contra la clave alternativa de la operación hace irrepresentable una
    /// cartera cuyo propietario no sea el de su operación.
    ///
    /// Es además el primer componente de las cuatro FKs compuestas del ámbito,
    /// que es lo que impide físicamente apuntar a un cliente, centro,
    /// trabajador o proyecto de otro tenant.
    /// </summary>
    public Guid PropietarioTenantId { get; protected set; }

    /// <summary>
    /// Tenant que opera. Igual al propietario cuando la operación es interna —
    /// sin nullable y sin caso especial: la gestión interna no es una excepción
    /// del modelo, es una asignación como cualquier otra (ADR-011 § 2.7).
    /// </summary>
    public Guid OperadorTenantId { get; protected set; }

    public Guid? AmbitoRelacionClienteId { get; protected set; }
    public Guid? AmbitoCentroId { get; protected set; }
    public Guid? AmbitoTrabajadorId { get; protected set; }
    public Guid? AmbitoProyectoId { get; protected set; }

    public DateTime VigenciaDesde { get; protected set; }
    public DateTime? VigenciaHasta { get; protected set; }
    public EstadoAsignacion Estado { get; protected set; }
    public MotivoCierreAsignacion? MotivoCierre { get; protected set; }

    /// <inheritdoc cref="IVersionable" />
    public Guid Version { get; private set; } = Guid.NewGuid();

    public DateTime CreadoEnUtc { get; protected set; } = DateTime.UtcNow;

    /// <summary>Quién concedió esta asignación. Guid suelto hacia <c>ApplicationUser</c>, mismo patrón que <c>EliminadoPorUsuarioId</c>.</summary>
    public Guid? CreadoPorUsuarioId { get; protected set; }

    public bool EsOperacionInterna => OperadorTenantId == PropietarioTenantId;

    public AmbitoAsignacion Ambito => new(
        AmbitoRelacionClienteId, AmbitoCentroId, AmbitoTrabajadorId, AmbitoProyectoId);

    protected void EstablecerAmbito(AmbitoAsignacion ambito)
    {
        AmbitoRelacionClienteId = ambito.RelacionClienteId;
        AmbitoCentroId = ambito.CentroId;
        AmbitoTrabajadorId = ambito.TrabajadorId;
        AmbitoProyectoId = ambito.ProyectoId;
    }

    protected void EstablecerVigencia(DateTime vigenciaDesde, DateTime? vigenciaHasta, DateTime ahora)
    {
        if (vigenciaHasta is not null && vigenciaHasta <= vigenciaDesde)
            throw new ArgumentException("La vigencia debe terminar después de empezar.", nameof(vigenciaHasta));

        VigenciaDesde = vigenciaDesde;
        VigenciaHasta = vigenciaHasta;

        // Una asignación cuyo inicio está en el futuro nace Programada: concede
        // lectura para que el operador entrante vea lo que va a heredar, pero
        // no responde del ámbito ni ocupa el índice único de responsabilidad
        // hasta que se active (ADR-011 § 4.5).
        Estado = vigenciaDesde > ahora ? EstadoAsignacion.Programada : EstadoAsignacion.Vigente;
    }

    /// <summary>
    /// Programada → Vigente. La transición vuelve a validar el solape en el
    /// comando que la invoca: el alta solo pudo comprobar contra las Vigentes
    /// de aquel momento, y entre medias el reparto ha podido cambiar.
    /// </summary>
    public void Activar()
    {
        if (Estado != EstadoAsignacion.Programada)
            throw new InvalidOperationException($"Solo se puede activar una asignación Programada (estaba {Estado}).");

        Estado = EstadoAsignacion.Vigente;
    }

    public void Suspender()
    {
        if (Estado != EstadoAsignacion.Vigente)
            throw new InvalidOperationException($"Solo se puede suspender una asignación Vigente (estaba {Estado}).");

        Estado = EstadoAsignacion.Suspendida;
    }

    public void Reactivar()
    {
        if (Estado != EstadoAsignacion.Suspendida)
            throw new InvalidOperationException($"Solo se puede reactivar una asignación Suspendida (estaba {Estado}).");

        Estado = EstadoAsignacion.Vigente;
    }

    /// <summary>
    /// Estado final. No existe "reabrir": para volver a operar el mismo ámbito
    /// se abre una asignación nueva, y así el histórico conserva las dos etapas
    /// por separado.
    /// </summary>
    public void Cerrar(MotivoCierreAsignacion motivo, DateTime ahora)
    {
        if (Estado == EstadoAsignacion.Cerrada)
            throw new InvalidOperationException("La asignación ya estaba cerrada.");

        Estado = EstadoAsignacion.Cerrada;
        MotivoCierre = motivo;

        // El cierre adelanta el fin de vigencia si estaba abierto o en el
        // futuro; nunca lo alarga, para que una asignación cerrada no pueda
        // aparecer como responsable en una consulta histórica posterior a su
        // cierre.
        if (VigenciaHasta is null || VigenciaHasta > ahora)
            VigenciaHasta = ahora > VigenciaDesde ? ahora : VigenciaDesde;
    }

    /// <summary>
    /// Si responde del ámbito en ese instante. Es la condición que usa la
    /// consulta histórica: vigencia semiabierta <c>[desde, hasta)</c>.
    /// </summary>
    public bool EstaVigenteEn(DateTime momento) =>
        Estado == EstadoAsignacion.Vigente
        && VigenciaDesde <= momento
        && (VigenciaHasta is null || momento < VigenciaHasta);

    /// <summary>
    /// Vigente pero con la fecha de fin ya pasada. Es lo que busca el job de
    /// expiración: sin cerrarla, seguiría ocupando el índice único parcial de
    /// responsabilidad y bloquearía el alta de su sustituta.
    /// </summary>
    public bool HaExpiradoEn(DateTime ahora) =>
        Estado == EstadoAsignacion.Vigente && VigenciaHasta is not null && ahora >= VigenciaHasta;
}
