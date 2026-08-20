namespace CaeManager.Application.Common;

/// <summary>
/// Comprobación de que un usuario es alcanzable desde el tenant activo.
/// Existe para que un Command pueda revalidar en servidor un Id de usuario
/// que llegó de un selector: que la interfaz solo ofrezca opciones válidas no
/// impide que alguien envíe otro Guid (hallazgo N-10 de
/// INFORME-AUDITORIA-2.md).
///
/// Es una abstracción y no una consulta directa porque <c>ApplicationUser</c>
/// vive en Infrastructure.Identity, que Application no puede referenciar
/// —mismo motivo por el que <c>AsignacionOperadorDelegado.UsuarioId</c> es un
/// Guid suelto sin navegación.
/// </summary>
public interface IDirectorioUsuariosService
{
    /// <summary>
    /// True si el usuario pertenece al tenant activo o es un Operador
    /// Delegado con asignación viva sobre él (ADR-004 § 5.3). Sin tenant
    /// resuelto devuelve false: fallo cerrado, igual que el resto de la
    /// cadena de resolución.
    /// </summary>
    Task<bool> EsVisibleEnTenantActualAsync(Guid usuarioId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Nombre visible de los usuarios indicados que sean alcanzables desde el tenant
    /// activo. Los que no lo sean simplemente no aparecen en el diccionario — mismo
    /// fallo cerrado que <see cref="EsVisibleEnTenantActualAsync"/>: un Id de otro
    /// tenant no debe poder usarse para resolver el nombre de esa persona.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, string>> ObtenerNombresVisiblesAsync(
        IReadOnlyCollection<Guid> usuarioIds, CancellationToken cancellationToken = default);

    /// <summary>
    /// El tenant al que pertenece un usuario, o <c>null</c> si no existe.
    ///
    /// Existe para poder imponer el invariante de la cadena de autorización —
    /// el usuario de una cartera pertenece al tenant que opera — que
    /// <see cref="EsVisibleEnTenantActualAsync"/> <b>no</b> puede imponer:
    /// ese predicado da por buenos también a los usuarios del tenant
    /// propietario, así que aceptaría como operador delegado a alguien de la
    /// casa. Concederle entonces una cartera externa le daría, dentro de su
    /// PROPIO workspace, el alcance de esa cartera.
    /// </summary>
    Task<Guid?> ObtenerTenantDeUsuarioAsync(Guid usuarioId, CancellationToken cancellationToken = default);
}
