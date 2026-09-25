using CaeManager.Domain.Common;

namespace CaeManager.Application.Usuarios;

/// <summary>
/// El segundo factor de una cuenta de Identity, visto desde Application: su estado,
/// sus códigos de recuperación y su restablecimiento (P0-8 del plan de madurez
/// 09-24, hallazgo FS-01).
///
/// Es un puerto y no una consulta directa por el mismo motivo que
/// <see cref="Common.IDirectorioUsuariosService"/>: <c>ApplicationUser</c> vive en
/// Infrastructure.Identity, que Application no puede referenciar. El puerto
/// <b>no autoriza nada</b>: ejecuta sobre el usuario que le digan. Quién puede
/// pedir qué lo deciden los comandos que lo usan
/// (<c>GenerarCodigosRecuperacionCommand</c>, <c>RestablecerSegundoFactorCommand</c>).
/// </summary>
public interface ISegundoFactorDeCuentas
{
    /// <summary>
    /// Estado del segundo factor de la cuenta, o <c>null</c> si no existe. Sin
    /// filtro de Tenant a propósito: devuelve el Tenant propietario de la cuenta
    /// para que el comando decida con él.
    /// </summary>
    Task<EstadoSegundoFactor?> ObtenerEstadoAsync(Guid usuarioId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sustituye los códigos de recuperación de la cuenta por
    /// <paramref name="cantidad"/> nuevos y los devuelve en claro. Es la única vez
    /// que existen en claro: se guardan con hash, y los anteriores dejan de valer.
    /// </summary>
    Task<Result<IReadOnlyList<string>>> GenerarCodigosRecuperacionAsync(
        Guid usuarioId, int cantidad, CancellationToken cancellationToken = default);

    /// <summary>
    /// Desactiva el segundo factor de la cuenta y borra su clave de autenticador y
    /// sus códigos de recuperación, en una sola escritura, y renueva su sello de
    /// seguridad para que se cierren las sesiones abiertas de esa cuenta. Quien
    /// tenga el rol Administrador tendrá que volver a configurarlo en su
    /// siguiente inicio de sesión (<c>TenantClaimsPrincipalFactory</c>).
    /// </summary>
    Task<Result> RestablecerAsync(Guid usuarioId, CancellationToken cancellationToken = default);
}

/// <param name="TenantId">Tenant propietario de la cuenta.</param>
/// <param name="Activo">Si la verificación en dos pasos está activada.</param>
/// <param name="CodigosRecuperacionRestantes">Códigos sin usar.</param>
public record EstadoSegundoFactor(Guid TenantId, bool Activo, int CodigosRecuperacionRestantes);
