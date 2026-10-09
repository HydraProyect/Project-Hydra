using CaeManager.Domain.Common;
using CaeManager.Domain.Operaciones;

namespace CaeManager.Domain.Tenants;

/// <summary>
/// Acto del <b>plano de Propiedad</b> por el que un Tenant propietario sin
/// Administrador propio encarga su administración al Operador CAE externo que ya
/// lo opera, apoyado en una cláusula de su contrato (ADR-011 § 2.7, enmienda del
/// 2026-10-08, punto 7). Se dice «el Operador CAE externo administra el Tenant
/// propietario <i>por encargo</i>», nunca «es su administrador».
///
/// <b>El encargo no concede acceso.</b> Sube el techo del rol efectivo de las
/// personas del Operador CAE externo dentro de ese Tenant propietario, y solo de
/// las que ya tienen una <see cref="AsignacionCartera"/> vigente bajo la
/// <see cref="AsignacionOperacion"/> a la que se liga: sin cartera no hay rol,
/// haya o no encargo. Tampoco cambia la propiedad de los datos, ni se deduce de
/// quién paga.
///
/// <b>Solo añade (append-only).</b> Ningún método cambia el Tenant propietario,
/// el Operador CAE, la operación, la cláusula, la versión del texto ni la
/// vigencia. La única transición es <see cref="Retirar"/>, una sola vez.
/// «Modificar» un encargo es retirarlo y registrar otro: así la fila responde
/// siempre a «¿con qué cláusula y desde cuándo administraba?».
///
/// Es un catálogo, no una <see cref="EntidadConTenant"/>, por la misma razón que
/// <see cref="AsignacionResponsabilidad"/>: cruza dos Tenants. Pertenece al
/// Tenant propietario —lo registra y lo retira él o la plataforma—, y el
/// Operador CAE externo solo lo lee para calcular su techo. La base de datos lo
/// acota con la política <c>posicion_en_el_encargo</c>.
/// </summary>
public class EncargoAdministracion : Entity, IVersionable
{
    public const int LongitudMaximaClausula = 500;

    public const int LongitudMaximaVersionTexto = 20;

    /// <summary>
    /// Versión del texto del encargo que se muestra a quien lo registra. Se copia
    /// a cada fila: si el texto cambia, las filas anteriores siguen diciendo cuál
    /// leyó quien las registró.
    /// </summary>
    public const string VersionTextoVigente = "2026-10-09";

    /// <summary>El Tenant propietario que encarga su administración.</summary>
    public Guid PropietarioTenantId { get; private set; }

    /// <summary>El Operador CAE externo que la recibe.</summary>
    public Guid OperadorTenantId { get; private set; }

    /// <summary>La operación externa, del Tenant propietario entero, a la que se liga.</summary>
    public Guid AsignacionOperacionId { get; private set; }

    /// <summary>Referencia a la cláusula del contrato que ampara el encargo. Dato del usuario: se escapa al mostrarlo.</summary>
    public string ClausulaContrato { get; private set; } = string.Empty;

    public string VersionTexto { get; private set; } = string.Empty;

    public OrigenEncargoAdministracion Origen { get; private set; }

    /// <summary>Actor real que lo registró; nunca un usuario simulado. Guid suelto hacia <c>ApplicationUser</c>.</summary>
    public Guid RegistradoPorUsuarioId { get; private set; }

    public DateTime RegistradoEnUtc { get; private set; }

    public DateTime VigenciaDesde { get; private set; }

    public DateTime? VigenciaHasta { get; private set; }

    /// <summary>Actor real que lo retiró. Se escribe una sola vez.</summary>
    public Guid? RetiradoPorUsuarioId { get; private set; }

    public DateTime? RetiradoEnUtc { get; private set; }

    /// <inheritdoc cref="IVersionable" />
    public Guid Version { get; private set; } = Guid.NewGuid();

    private EncargoAdministracion()
    {
        // Requerido por EF Core.
    }

    /// <summary>
    /// Registra el encargo sobre una operación externa, del Tenant propietario
    /// entero y vigente. Quién puede registrarlo (la plataforma o un
    /// Administrador miembro del Tenant propietario, nunca nadie del Operador
    /// CAE) lo decide Application: Domain no ve Identity ni la sesión.
    /// </summary>
    public static EncargoAdministracion Registrar(
        AsignacionOperacion operacion,
        string clausulaContrato,
        string versionTexto,
        OrigenEncargoAdministracion origen,
        Guid registradoPorUsuarioId,
        DateTime ahora,
        DateTime? vigenciaHasta)
    {
        ArgumentNullException.ThrowIfNull(operacion);
        if (registradoPorUsuarioId == Guid.Empty)
            throw new ArgumentException("El encargo debe tener quien lo registra.", nameof(registradoPorUsuarioId));
        if (operacion.EsRaiz || operacion.EsOperacionInterna)
            throw new ArgumentException(
                "Solo se encarga la administración sobre una operación externa de un Operador CAE.", nameof(operacion));
        if (!operacion.Ambito.EsUniversal)
            throw new ArgumentException(
                "Se encarga la administración del Tenant propietario entero: la operación tiene que ser universal.",
                nameof(operacion));
        if (!operacion.EstaVigenteEn(ahora))
            throw new ArgumentException("La operación no está vigente.", nameof(operacion));
        if (!Enum.IsDefined(origen))
            throw new ArgumentException("Origen del encargo desconocido.", nameof(origen));
        if (vigenciaHasta is not null && vigenciaHasta <= ahora)
            throw new ArgumentException("La vigencia del encargo debe terminar después de empezar.", nameof(vigenciaHasta));

        return new EncargoAdministracion
        {
            PropietarioTenantId = operacion.PropietarioTenantId,
            OperadorTenantId = operacion.OperadorTenantId,
            AsignacionOperacionId = operacion.Id,
            ClausulaContrato = NormalizarClausula(clausulaContrato),
            VersionTexto = NormalizarVersionTexto(versionTexto),
            Origen = origen,
            RegistradoPorUsuarioId = registradoPorUsuarioId,
            RegistradoEnUtc = ahora,
            VigenciaDesde = ahora,
            VigenciaHasta = vigenciaHasta,
        };
    }

    /// <summary>
    /// Retira el encargo. Una sola vez: un encargo retirado no se reactiva ni se
    /// vuelve a retirar; para volver a encargar se registra otro.
    /// </summary>
    public void Retirar(Guid actorRealUsuarioId, DateTime ahora)
    {
        if (actorRealUsuarioId == Guid.Empty)
            throw new ArgumentException("La retirada debe tener un autor.", nameof(actorRealUsuarioId));
        if (RetiradoEnUtc is not null)
            throw new InvalidOperationException("El encargo ya estaba retirado.");

        RetiradoPorUsuarioId = actorRealUsuarioId;
        RetiradoEnUtc = ahora;
    }

    /// <summary>
    /// Sin retirar y dentro de su vigencia <c>[desde, hasta)</c>. Que además
    /// surta efecto exige que la operación y la cartera sigan vigentes: eso lo
    /// comprueba quien calcula el techo, con el predicado único de cartera.
    /// </summary>
    public bool EstaVigente(DateTime ahora) =>
        RetiradoEnUtc is null
        && VigenciaDesde <= ahora
        && (VigenciaHasta is null || ahora < VigenciaHasta);

    private static string NormalizarClausula(string clausulaContrato)
    {
        var limpia = (clausulaContrato ?? string.Empty).Trim();
        if (limpia.Length == 0)
            throw new ArgumentException(
                "Indica la cláusula del contrato que ampara el encargo.", nameof(clausulaContrato));
        if (limpia.Length > LongitudMaximaClausula)
            throw new ArgumentException(
                $"La referencia a la cláusula no puede superar {LongitudMaximaClausula} caracteres.", nameof(clausulaContrato));
        return limpia;
    }

    private static string NormalizarVersionTexto(string versionTexto)
    {
        var limpia = (versionTexto ?? string.Empty).Trim();
        if (limpia.Length == 0)
            throw new ArgumentException("El encargo debe decir qué versión del texto se mostró.", nameof(versionTexto));
        if (limpia.Length > LongitudMaximaVersionTexto)
            throw new ArgumentException(
                $"La versión del texto no puede superar {LongitudMaximaVersionTexto} caracteres.", nameof(versionTexto));
        return limpia;
    }
}
