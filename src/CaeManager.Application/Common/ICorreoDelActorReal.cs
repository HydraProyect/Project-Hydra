namespace CaeManager.Application.Common;

/// <summary>
/// El correo de la cuenta de quien está detrás del teclado: el
/// <c>ActorRealUsuarioId</c> de <see cref="IActorAuditoria"/>, nunca el
/// usuario simulado de una impersonación.
///
/// <para>
/// Existe por la decisión D3 (2026-09-19): cuando una reclamación de
/// documentación sale por SMTP —sin Conexión Microsoft 365—, la respuesta de
/// la Empresa contraparte tiene que volver al Gestor CAE que la emitió, y eso
/// se hace con un <c>Reply-To</c>. Hace falta su correo, y
/// <c>ApplicationUser</c> vive en Infrastructure.Identity, que Application no
/// puede referenciar — mismo motivo por el que existe
/// <see cref="IDirectorioUsuariosService"/>.
/// </para>
///
/// <para>
/// <b>No toma un Id por parámetro, y eso es el contrato, no una comodidad.</b>
/// Un <c>ObtenerCorreoDeUsuarioAsync(Guid)</c> genérico habría abierto en
/// Application la posibilidad de resolver el correo de cualquier cuenta del
/// sistema —dato personal, y de cualquier Tenant propietario— a partir de un
/// Guid que el llamador puede haber recibido de fuera. Aquí no hay nada que
/// elegir: la identidad la pone la sesión.
/// </para>
/// </summary>
public interface ICorreoDelActorReal
{
    /// <summary>
    /// El correo del actor real, o <c>null</c> si no hay identidad resuelta
    /// (jobs de fondo, seeders), si la cuenta ya no existe o si no tiene
    /// correo. Quien lo use debe seguir adelante sin él —fallo cerrado hacia
    /// "no hay a quién responder"—, nunca sustituirlo por otro buzón.
    /// </summary>
    Task<string?> ObtenerAsync(CancellationToken cancellationToken = default);
}
