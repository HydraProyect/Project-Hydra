using CaeManager.Domain.Common;

namespace CaeManager.Application.Usuarios;

/// <summary>
/// Las cuentas de Identity del Tenant propietario, vistas desde Application: alta,
/// edición, rol, activación, baja de una cuenta pendiente y enlace de activación
/// (P1-I2 del plan de madurez 09-24: <c>UserManager</c> solo desde Commands).
///
/// Es un puerto por el mismo motivo que <see cref="ISegundoFactorDeCuentas"/>:
/// <c>ApplicationUser</c> vive en Infrastructure.Identity, que Application no
/// puede referenciar. <b>No autoriza nada</b>: ejecuta sobre la cuenta que le
/// digan. Quién puede pedir qué lo deciden los Commands de
/// <c>Usuarios/Commands</c> que lo usan, y ninguna página de Web vuelve a llamar
/// a <c>UserManager</c> para esto (<c>UserManagerSoloDesdeInfraestructuraDeLoginTests</c>).
///
/// Los fallos de Identity (<c>IdentityResult</c> sin éxito) vuelven como
/// <see cref="Result"/> fallido con el motivo que da Identity en el mensaje:
/// quien administra necesita el motivo real para reintentar, nunca uno genérico.
/// </summary>
public interface IGestionCuentasUsuario
{
    /// <summary>
    /// La cuenta, o <c>null</c> si no existe. Sin filtro de Tenant a propósito:
    /// <see cref="CuentaUsuario.EsPropiaDelTenantActual"/> le dice al Command si
    /// puede tocarla.
    /// </summary>
    Task<CuentaUsuario?> ObtenerAsync(Guid usuarioId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Si la cuenta pertenece al Tenant activo. Propiedad, no visibilidad: un
    /// Operador CAE externo delegado se ve en la lista del Tenant propietario, pero
    /// su cuenta y su rol se gobiernan en su organización. Toda operación que
    /// modifique una cuenta pregunta por esto.
    /// </summary>
    Task<bool> EsPropiaDelTenantActualAsync(Guid usuarioId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Si la cuenta tiene una Asignación de Cartera vigente u otro vínculo
    /// operativo que la baja dejaría apuntando a una cuenta inexistente.
    /// </summary>
    Task<bool> TieneVinculoOperativoAsync(Guid usuarioId, CancellationToken cancellationToken = default);

    /// <summary>Crea la cuenta sin contraseña y sin rol, y devuelve su Id.</summary>
    Task<Result<Guid>> CrearAsync(NuevaCuentaUsuario cuenta, CancellationToken cancellationToken = default);

    /// <summary>Añade el rol a la cuenta, sin quitar ninguno.</summary>
    Task<Result> AsignarRolAsync(Guid usuarioId, string rol, CancellationToken cancellationToken = default);

    /// <summary>Guarda nombre, Coordinador CAE, Cliente vinculado y permiso sensible.</summary>
    Task<Result> ActualizarDatosAsync(Guid usuarioId, DatosCuentaUsuario datos, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sustituye todos los roles de la cuenta por <paramref name="rolNuevo"/>.
    /// Identity no lo hace en una transacción: quitar y poner son dos escrituras, y
    /// el resultado dice cuál falló.
    /// </summary>
    Task<ResultadoCambioRol> CambiarRolAsync(Guid usuarioId, string rolNuevo, CancellationToken cancellationToken = default);

    /// <summary>
    /// Desactiva (bloqueo y sello de seguridad nuevo, en la misma escritura: ver
    /// <c>ApplicationUser.Desactivar</c>) o reactiva la cuenta.
    /// </summary>
    Task<Result> CambiarActivacionAsync(Guid usuarioId, bool activar, CancellationToken cancellationToken = default);

    /// <summary>
    /// Borra la cuenta de Identity <b>solo si sigue pendiente de activación</b>,
    /// comprobado sobre la misma lectura que se borra; si no, falla con
    /// <see cref="AutoridadSobreCuentas.YaNoPendiente"/>.
    /// </summary>
    Task<Result> EliminarAsync(Guid usuarioId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Token de un solo uso para establecer la contraseña (el mismo proveedor que
    /// «olvidé mi contraseña»), ya codificado para ir en una URL, <b>solo si la cuenta
    /// sigue pendiente de activación</b>, comprobado sobre la misma lectura cuyo sello
    /// lleva el token; si no, falla con <see cref="AutoridadSobreCuentas.YaNoPendiente"/>.
    /// </summary>
    Task<Result<string>> GenerarTokenActivacionAsync(Guid usuarioId, CancellationToken cancellationToken = default);
}

/// <param name="PendienteActivacion">Sin contraseña y sin login externo: la persona nunca entró.</param>
public record CuentaUsuario(
    Guid Id,
    string Email,
    string NombreCompleto,
    bool EsPropiaDelTenantActual,
    IReadOnlyList<string> Roles,
    bool PendienteActivacion,
    bool Activa,
    bool PermisoConsultarAccesoDocumentosSensibles,
    Guid? CoordinadorUsuarioId = null,
    Guid? ClienteId = null);

public record NuevaCuentaUsuario(
    string Email,
    string NombreCompleto,
    Guid TenantId,
    Guid? CoordinadorUsuarioId,
    Guid? ClienteId,
    bool PermisoConsultarAccesoDocumentosSensibles);

public record DatosCuentaUsuario(
    string NombreCompleto,
    Guid? CoordinadorUsuarioId,
    Guid? ClienteId,
    bool PermisoConsultarAccesoDocumentosSensibles);

public enum DesenlaceCambioRol
{
    Cambiado,
    NoEncontrada,

    /// <summary>Quitar los roles anteriores falló: la cuenta conserva el suyo.</summary>
    FalloAlQuitarConservaElAnterior,

    /// <summary>Se quitaron los anteriores pero no se pudo poner el nuevo: la cuenta queda sin rol.</summary>
    FalloAlPonerQuedaSinRol,
}

public record ResultadoCambioRol(DesenlaceCambioRol Desenlace, string? MotivoIdentity = null);
