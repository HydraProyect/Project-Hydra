using CaeManager.Domain.Common;

namespace CaeManager.Domain.Cumplimiento.Subencargados;

/// <summary>
/// Un subencargado del tratamiento que TALVEG propone usar (#2010, decisión D-7 del 2026-11-15): catálogo del
/// plano de Plataforma, sin Tenant, el mismo para todos los Tenants propietarios.
///
/// <para>
/// Ciclo de vida: <see cref="EstadoSubencargado.Propuesto"/> → <see cref="EstadoSubencargado.Avisado"/> (se ha
/// empezado a avisar a los Tenants propietarios) → <see cref="EstadoSubencargado.Activo"/> → <see
/// cref="EstadoSubencargado.Retirado"/>. Un subencargado no se activa sin aviso probado (un envío
/// <see cref="FaseAvisoCambioSubencargado.Enviado"/>) a todos los Tenants propietarios y sin que haya pasado
/// <see cref="PlazoMinimoAviso"/> desde cada uno de esos avisos (lo comprueba <see cref="MotivoParaNoActivar"/>).
/// </para>
///
/// <para>
/// Una objeción de un Tenant propietario no frena el subencargado para los demás (D-7): si
/// <see cref="AdmiteExclusionPorTenant"/>, ese Tenant deja de usarlo por su <see cref="Conmutador"/>; si no, la
/// objeción deja constancia de que ese Tenant puede resolver sin penalización (<see cref="EfectoDeUnaObjecion"/>).
/// </para>
/// </summary>
public class Subencargado : Entity, IVersionable
{
    public const int LongitudMaximaNombre = 200;
    public const int LongitudMaximaFinalidad = 500;
    public const int LongitudMaximaUbicacion = 300;
    public const int LongitudMaximaMotivo = 500;

    /// <summary>
    /// Plazo mínimo entre el aviso probado a un Tenant propietario y la activación: 30 días (decisión D-7,
    /// aceptada el 2026-11-15, DECISION_LOG del repositorio de negocio).
    /// </summary>
    public static readonly TimeSpan PlazoMinimoAviso = TimeSpan.FromDays(30);

    public string Nombre { get; private set; } = string.Empty;
    public string Finalidad { get; private set; } = string.Empty;

    /// <summary>Dónde trata los datos y, si la hay, la transferencia internacional y su garantía.</summary>
    public string UbicacionYTransferencia { get; private set; } = string.Empty;

    /// <summary>Si un Tenant propietario puede dejar de usarlo sin que deje de usarlo nadie más.</summary>
    public bool AdmiteExclusionPorTenant { get; private set; }

    /// <summary>El conmutador por el que un Tenant deja de usarlo. Solo con <see cref="AdmiteExclusionPorTenant"/>.</summary>
    public ConmutadorExclusionSubencargado? Conmutador { get; private set; }

    public EstadoSubencargado Estado { get; private set; }
    public DateTime PropuestoEnUtc { get; private set; }
    public Guid PropuestoPorUsuarioId { get; private set; }

    /// <summary>Cuándo se empezó a avisar a los Tenants propietarios (paso a <see cref="EstadoSubencargado.Avisado"/>).</summary>
    public DateTime? AvisadoEnUtc { get; private set; }

    /// <summary><see cref="AvisadoEnUtc"/> + <see cref="PlazoMinimoAviso"/>: antes, no se activa.</summary>
    public DateTime? FechaMinimaActivacionUtc { get; private set; }

    public DateTime? ActivadoEnUtc { get; private set; }
    public Guid? ActivadoPorUsuarioId { get; private set; }
    public DateTime? RetiradoEnUtc { get; private set; }
    public string? MotivoRetirada { get; private set; }

    public Guid Version { get; private set; } = Guid.NewGuid();

    private Subencargado()
    {
        // Requerido por EF Core.
    }

    public Subencargado(
        string nombre,
        string finalidad,
        string ubicacionYTransferencia,
        bool admiteExclusionPorTenant,
        ConmutadorExclusionSubencargado? conmutador,
        Guid propuestoPorUsuarioId,
        DateTime propuestoEnUtc)
    {
        if (admiteExclusionPorTenant != conmutador.HasValue)
            throw new ArgumentException(
                "Un subencargado que admite exclusión por Tenant nombra el conmutador que la aplica, y uno que no la admite no lleva conmutador.",
                nameof(conmutador));
        if (conmutador is { } valor && !Enum.IsDefined(valor))
            throw new ArgumentOutOfRangeException(nameof(conmutador), valor, "Conmutador desconocido.");
        if (propuestoPorUsuarioId == Guid.Empty)
            throw new ArgumentException("La propuesta tiene que quedar atribuida a quien la hizo.", nameof(propuestoPorUsuarioId));

        Nombre = Texto(nombre, LongitudMaximaNombre, nameof(nombre));
        Finalidad = Texto(finalidad, LongitudMaximaFinalidad, nameof(finalidad));
        UbicacionYTransferencia = Texto(ubicacionYTransferencia, LongitudMaximaUbicacion, nameof(ubicacionYTransferencia));
        AdmiteExclusionPorTenant = admiteExclusionPorTenant;
        Conmutador = conmutador;
        Estado = EstadoSubencargado.Propuesto;
        PropuestoPorUsuarioId = propuestoPorUsuarioId;
        PropuestoEnUtc = Utc(propuestoEnUtc, nameof(propuestoEnUtc));
    }

    /// <summary>Lo que deja una objeción de un Tenant propietario a este subencargado (D-7).</summary>
    public EfectoObjecionSubencargado EfectoDeUnaObjecion => AdmiteExclusionPorTenant
        ? EfectoObjecionSubencargado.ExcluidoParaElTenant
        : EfectoObjecionSubencargado.ResolucionSinPenalizacionDisponible;

    /// <summary>Se puede objetar desde que se avisa y mientras no se retire.</summary>
    public bool AdmiteObjecion => Estado is EstadoSubencargado.Avisado or EstadoSubencargado.Activo;

    /// <summary>
    /// Se empieza a avisar. Solo la primera vez: los envíos posteriores (reintentos, Tenants que se dieron de alta
    /// después) no mueven <see cref="AvisadoEnUtc"/> ni la fecha mínima.
    /// </summary>
    public void MarcarAvisado(DateTime ahoraUtc)
    {
        if (Estado != EstadoSubencargado.Propuesto)
            throw new InvalidOperationException("Solo se marca como avisado un subencargado propuesto.");

        AvisadoEnUtc = Utc(ahoraUtc, nameof(ahoraUtc));
        FechaMinimaActivacionUtc = AvisadoEnUtc + PlazoMinimoAviso;
        Estado = EstadoSubencargado.Avisado;
    }

    /// <summary>
    /// Null si se puede activar ahora; si no, por qué. <paramref name="tenantsSinAvisoProbadoConPlazo"/> son los
    /// Tenants propietarios que no tienen un aviso Enviado de hace al menos <see cref="PlazoMinimoAviso"/>.
    /// </summary>
    public string? MotivoParaNoActivar(DateTime ahoraUtc, int tenantsSinAvisoProbadoConPlazo)
    {
        if (Estado != EstadoSubencargado.Avisado)
            return Estado == EstadoSubencargado.Propuesto
                ? "Todavía no se ha avisado a los Tenants propietarios de este subencargado."
                : "Este subencargado ya no se puede activar: está " + (Estado == EstadoSubencargado.Activo ? "activo." : "retirado.");
        if (FechaMinimaActivacionUtc is not { } fechaMinima || ahoraUtc < fechaMinima)
            return $"No han pasado los {PlazoMinimoAviso.Days} días de aviso: no se puede activar antes del "
                   + $"{FechaMinimaActivacionUtc?.EnHoraPeninsular().ToString("dd'/'MM'/'yyyy HH:mm", System.Globalization.CultureInfo.InvariantCulture)} (hora peninsular).";
        if (tenantsSinAvisoProbadoConPlazo < 0)
            throw new ArgumentOutOfRangeException(nameof(tenantsSinAvisoProbadoConPlazo));
        if (tenantsSinAvisoProbadoConPlazo > 0)
            return tenantsSinAvisoProbadoConPlazo == 1
                ? $"Un Tenant propietario no tiene un aviso enviado de hace al menos {PlazoMinimoAviso.Days} días."
                : $"{tenantsSinAvisoProbadoConPlazo} Tenants propietarios no tienen un aviso enviado de hace al menos {PlazoMinimoAviso.Days} días.";
        return null;
    }

    public void Activar(DateTime ahoraUtc, int tenantsSinAvisoProbadoConPlazo, Guid actorUsuarioId)
    {
        if (actorUsuarioId == Guid.Empty)
            throw new ArgumentException("La activación tiene que quedar atribuida a quien la hizo.", nameof(actorUsuarioId));
        if (MotivoParaNoActivar(ahoraUtc, tenantsSinAvisoProbadoConPlazo) is { } motivo)
            throw new InvalidOperationException(motivo);

        ActivadoEnUtc = Utc(ahoraUtc, nameof(ahoraUtc));
        ActivadoPorUsuarioId = actorUsuarioId;
        Estado = EstadoSubencargado.Activo;
    }

    public void Retirar(string motivo, DateTime ahoraUtc)
    {
        if (Estado == EstadoSubencargado.Retirado)
            throw new InvalidOperationException("Este subencargado ya estaba retirado.");

        MotivoRetirada = Texto(motivo, LongitudMaximaMotivo, nameof(motivo));
        RetiradoEnUtc = Utc(ahoraUtc, nameof(ahoraUtc));
        Estado = EstadoSubencargado.Retirado;
    }

    private static string Texto(string valor, int longitudMaxima, string parametro)
    {
        if (string.IsNullOrWhiteSpace(valor))
            throw new ArgumentException("El texto es obligatorio.", parametro);
        var normalizado = valor.Trim();
        if (normalizado.Length > longitudMaxima)
            throw new ArgumentException($"No puede superar {longitudMaxima} caracteres.", parametro);
        return normalizado;
    }

    internal static DateTime Utc(DateTime valor, string parametro) =>
        valor.Kind == DateTimeKind.Utc ? valor : throw new ArgumentException("La fecha tiene que ser UTC.", parametro);
}

public enum EstadoSubencargado
{
    Propuesto,
    Avisado,
    Activo,
    Retirado,
}

/// <summary>Conmutador por el que un Tenant propietario deja de usar un subencargado. Hoy solo la IA (#2006).</summary>
public enum ConmutadorExclusionSubencargado
{
    Ia,
}

public enum EfectoObjecionSubencargado
{
    /// <summary>El Tenant propietario que objeta deja de usar el subencargado; los demás siguen.</summary>
    ExcluidoParaElTenant,

    /// <summary>
    /// El subencargado no se puede excluir para un solo Tenant: la objeción deja constancia de que ese Tenant
    /// propietario puede resolver sin penalización (D-7).
    /// </summary>
    ResolucionSinPenalizacionDisponible,
}
