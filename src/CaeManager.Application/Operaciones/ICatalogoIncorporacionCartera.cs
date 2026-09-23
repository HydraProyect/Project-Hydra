using CaeManager.Domain.Operaciones;

namespace CaeManager.Application.Operaciones;

/// <summary>
/// Un Tenant propietario al que un Gestor CAE puede pedir incorporarse: lo
/// opera su Operador CAE y él todavía no lo tiene en su cartera. Solo el
/// nombre del Tenant: la lista no puede enseñar ni un dato de dentro.
/// </summary>
public record TenantCandidatoIncorporacion(Guid PropietarioTenantId, string Nombre, Guid AsignacionOperacionId);

/// <summary>
/// Lo que hizo <see cref="ICatalogoIncorporacionCartera.IncorporarAsync"/>: o
/// la cartera creada (y la fila heredada que la acompaña), o por qué la
/// solicitud ya no tenía sentido.
/// </summary>
public record ResultadoIncorporacionCartera(
    AsignacionCartera? Cartera, Guid? AsignacionOperadorDelegadoId, MotivoAnulacionSolicitudCartera? MotivoAnulacion)
{
    public static ResultadoIncorporacionCartera Anulada(MotivoAnulacionSolicitudCartera motivo) => new(null, null, motivo);
}

/// <summary>
/// La parte de la solicitud de incorporación a cartera que toca los
/// catálogos de asignación. Vive detrás de un puerto por la misma razón que
/// <see cref="IAsignacionesOperativasWriter"/>: esos catálogos están fuera del
/// filtro de tenant, y su acceso se concentra en pocos ficheros revisados
/// (ratchet <c>AccesoRestringidoACatalogosDeAsignacionTests</c>).
///
/// <para>
/// <b>«En su cartera»</b> significa que el Tenant ya le aparece al Gestor CAE:
/// tiene una Asignación de Cartera vigente sobre ese Tenant propietario, de
/// cualquier ámbito, o una fila heredada de Operador Delegado en una
/// delegación viva hacia él. Con cualquiera de las dos no es candidato: pedir
/// entrar donde ya se está solo ensancharía en silencio el alcance que otro
/// decidió.
/// </para>
///
/// <para>
/// Las escrituras (<see cref="IncorporarAsync"/>, <see cref="RetirarAsync"/>)
/// tienen que ejecutarse con el Tenant propietario como Tenant activo
/// (<c>AmbitoTenantExplicito</c>): la política RLS de las carteras solo deja
/// escribir sobre el propietario contextual. Es responsabilidad del Command.
/// </para>
/// </summary>
public interface ICatalogoIncorporacionCartera
{
    /// <summary>
    /// Tenants propietarios con una Asignación de Operación externa, universal
    /// y vigente a <paramref name="operadorTenantId"/>, con su delegación de
    /// Operador CAE externo viva, que <paramref name="usuarioId"/> no tiene en
    /// su cartera. Ordenados por nombre.
    /// </summary>
    Task<IReadOnlyList<TenantCandidatoIncorporacion>> ObtenerCandidatosAsync(
        Guid operadorTenantId, Guid usuarioId, CancellationToken cancellationToken = default);

    /// <summary>La operación, si sigue vigente hoy; <c>null</c> si no existe, se cerró o caducó.</summary>
    Task<AsignacionOperacion?> ObtenerOperacionVigenteAsync(
        Guid asignacionOperacionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Crea la Asignación de Cartera universal del solicitante (rol Gestor CAE,
    /// sin caducidad) y su fila heredada de Operador Delegado, sin guardar.
    /// Devuelve un motivo de anulación, sin crear nada, si la operación ya no
    /// está vigente o el solicitante ya tiene el Tenant en su cartera.
    /// </summary>
    Task<ResultadoIncorporacionCartera> IncorporarAsync(
        SolicitudIncorporacionCartera solicitud, CancellationToken cancellationToken = default);

    /// <summary>
    /// Cierra la cartera que creó la solicitud, si sigue vigente, y borra la
    /// fila heredada que creó, si sigue existiendo. Nada más: ni otras
    /// carteras del Gestor CAE ni filas que ya tuviera antes.
    /// </summary>
    Task RetirarAsync(SolicitudIncorporacionCartera solicitud, CancellationToken cancellationToken = default);

    /// <summary>
    /// Guarda el contexto. Devuelve <c>false</c>, y descarta lo pendiente,
    /// solo si perdió una carrera conocida: otra persona resolvió o revocó la
    /// misma solicitud a la vez (versión), el Gestor CAE ya tenía una
    /// pendiente igual, o la cartera universal o la fila heredada ya existían.
    /// Cualquier otro fallo de base de datos —incluida una violación de RLS—
    /// se propaga: disfrazarlo de «ya estaba resuelta» escondería un defecto.
    /// </summary>
    Task<bool> GuardarDetectandoCarreraAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Descarta lo que quedó en el contexto sin guardar tras un fallo que el
    /// Command decide no propagar (el aviso al Gestor CAE). Sin esto, lo que
    /// falló se colaría en el siguiente guardado del mismo contexto.
    /// </summary>
    void DescartarPendientes();

    /// <summary>
    /// Ids de las carteras de la lista que siguen vigentes hoy, con su Asignación de
    /// Operación también vigente: sin operación, la cartera no da acceso.
    /// </summary>
    Task<IReadOnlySet<Guid>> FiltrarCarterasVigentesAsync(
        IReadOnlyCollection<Guid> asignacionCarteraIds, CancellationToken cancellationToken = default);
}
