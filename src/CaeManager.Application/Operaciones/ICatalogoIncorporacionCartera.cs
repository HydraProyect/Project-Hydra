using CaeManager.Domain.Operaciones;

namespace CaeManager.Application.Operaciones;

/// <summary>
/// Un Tenant propietario al que un Gestor CAE puede pedir incorporarse: lo
/// opera su Operador CAE y él todavía no lo tiene en su cartera. Solo el
/// nombre del Tenant: la lista no puede enseñar ni un dato de dentro.
/// </summary>
public record TenantCandidatoIncorporacion(Guid PropietarioTenantId, string Nombre, Guid AsignacionOperacionId);

/// <summary>Un Tenant propietario que un Gestor CAE tiene entero en su cartera. Solo el nombre.</summary>
public record TenantEnCarteraDeGestor(Guid PropietarioTenantId, string Nombre);

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

    /// <summary>
    /// Los mismos Tenants propietarios que <see cref="ObtenerCandidatosAsync"/>
    /// sin descontar los que alguien ya tenga en su cartera: todos los que el
    /// Operador CAE puede poner en una cartera. Es el predicado del que sale la
    /// lista de candidatos, no una segunda regla: lo usa el alta de un Gestor
    /// CAE, cuya cuenta todavía no tiene cartera que descontar.
    /// </summary>
    Task<IReadOnlyList<TenantCandidatoIncorporacion>> ObtenerAsignablesAsync(
        Guid operadorTenantId, CancellationToken cancellationToken = default);

    /// <summary>La operación, si sigue vigente hoy; <c>null</c> si no existe, se cerró o caducó.</summary>
    Task<AsignacionOperacion?> ObtenerOperacionVigenteAsync(
        Guid asignacionOperacionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Crea la Asignación de Cartera universal del solicitante (rol Gestor CAE,
    /// sin caducidad) y su fila heredada de Operador Delegado, sin guardar.
    /// Devuelve un motivo de anulación, sin crear nada, si la operación ya no
    /// está vigente o el solicitante ya tiene el Tenant en su cartera.
    ///
    /// <b>Marca de principal</b> (ADR-011 § 2.7, enmienda 2026-10-08): si la
    /// Asignación de Operación no tiene ninguna cartera principal viva (no
    /// cerrada), la que se crea nace principal; si ya la tiene, nace sin marca.
    /// Dos emisiones simultáneas las separa el índice único de la base de datos:
    /// la que pierde hace fallar el guardado (<see cref="GuardarDetectandoCarreraAsync"/>).
    /// </summary>
    Task<ResultadoIncorporacionCartera> IncorporarAsync(
        SolicitudIncorporacionCartera solicitud, CancellationToken cancellationToken = default);

    /// <summary>
    /// La misma incorporación sin solicitud detrás: la usa el alta de un Gestor
    /// CAE con su cartera ya elegida (<c>CrearUsuarioCommand</c>). Mismas
    /// comprobaciones y misma escritura que la de una solicitud aceptada; además
    /// anula si la operación no es la externa de <paramref name="operadorTenantId"/>
    /// sobre <paramref name="propietarioTenantId"/>.
    /// </summary>
    Task<ResultadoIncorporacionCartera> IncorporarAsync(
        Guid propietarioTenantId, Guid operadorTenantId, Guid asignacionOperacionId, Guid usuarioId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Los Tenants propietarios que <paramref name="usuarioId"/> tiene <b>enteros</b> en su
    /// cartera por una Asignación de Operación no raíz de <paramref name="operadorTenantId"/>:
    /// Asignación de Cartera universal, vigente y con rol Gestor CAE. No exige que la
    /// operación siga vigente: una cartera cuya operación caducó se puede retirar aunque ya
    /// no se pueda asignar. Ordenados por nombre.
    /// </summary>
    Task<IReadOnlyList<TenantEnCarteraDeGestor>> ObtenerCarteraUniversalAsync(
        Guid operadorTenantId, Guid usuarioId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retira el Tenant entero de la cartera de <paramref name="usuarioId"/> sin pasar por una
    /// solicitud: cierra sus Asignaciones de Cartera universales vigentes sobre ese Tenant
    /// (las del Operador CAE indicado, con rol Gestor CAE), marca como revocadas —por
    /// <paramref name="actorUsuarioId"/>— las solicitudes de incorporación aceptadas que las
    /// crearon y borra la fila heredada de Operador Delegado <b>solo si no le queda otra
    /// cartera vigente</b> sobre el mismo Tenant (por ejemplo, una de otro rol). Sin guardar. Devuelve <c>false</c> sin tocar nada si no
    /// tenía ese Tenant entero en su cartera. Como
    /// <see cref="IncorporarAsync(Guid, Guid, Guid, Guid, CancellationToken)"/>, exige el
    /// Tenant propietario como Tenant activo (<c>AmbitoTenantExplicito</c>).
    /// </summary>
    Task<bool> RetirarCarteraUniversalAsync(
        Guid propietarioTenantId, Guid operadorTenantId, Guid usuarioId, Guid actorUsuarioId,
        CancellationToken cancellationToken = default);

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
