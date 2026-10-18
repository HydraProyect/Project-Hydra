using CaeManager.Domain.Common;

namespace CaeManager.Application.Common;

/// <summary>
/// Alta y reintento de activación del usuario Administrador inicial de un
/// Tenant propietario recién aprovisionado (PD-A8). Es una abstracción y no
/// una dependencia directa de Identity porque <c>ApplicationUser</c> y
/// <c>UserManager</c> viven en Infrastructure.Identity, que Application no
/// puede referenciar — mismo motivo que <see cref="IDirectorioUsuariosService"/>.
///
/// <para>
/// Reutiliza el mismo mecanismo que <c>Usuarios.razor.cs</c> ya tiene probado
/// para el alta ordinaria (token de un solo uso de
/// <c>DataProtectorTokenProvider</c>, correo best-effort, enlace de
/// contingencia si el correo falla): no se inventa un camino nuevo, se abre
/// el mismo para que TALVEG pueda ejecutarlo bajo capacidad
/// <c>Aprovisionamiento</c> cuando el Tenant todavía no tiene ningún
/// Administrador que lo invite.
/// </para>
///
/// <para>
/// "Activación pendiente durable" (decisión del propietario, 2026-09-23): a
/// diferencia del flujo ordinario, aquí el estado se persiste explícitamente
/// —<c>ApplicationUser.IntentosActivacion</c>/<c>UltimoIntentoActivacionUtc</c>—
/// para que TALVEG pueda saber que un Tenant aprovisionado sigue sin
/// Administrador activo sin tener que abrirlo y mirar su lista de usuarios.
/// </para>
/// </summary>
public interface IAprovisionamientoUsuariosService
{
    /// <summary>
    /// Crea el Administrador inicial de <paramref name="tenantId"/>: cuenta
    /// sin contraseña, rol Administrador, enlace de activación de un solo uso
    /// y correo best-effort. Falla si ya existe una cuenta con ese correo.
    /// </summary>
    /// <param name="urlBaseAbsoluta">
    /// Origen absoluto de la aplicación (p. ej. <c>https://app.talveg.es</c>),
    /// resuelto por quien llama con <c>NavigationManager.BaseUri</c> —
    /// Application e Infrastructure no tienen acceso a la petición HTTP en
    /// curso, así que no pueden construir la URL del enlace por sí solas.
    /// </param>
    Task<Result<ActivacionUsuarioInicialDto>> CrearUsuarioInicialAsync(
        Guid tenantId, string email, string nombreCompleto, string urlBaseAbsoluta,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reintento idempotente: revalida que la cuenta sigue pendiente de
    /// activación (nunca sobre una cuenta que ya tiene contraseña o inició
    /// sesión por SSO), regenera el enlace, reenvía el correo y registra el
    /// intento. Falla con <c>UsuarioInicial.YaActivado</c> si la cuenta dejó
    /// de estar pendiente entre que se listó y se pulsó reintentar.
    /// </summary>
    Task<Result<ActivacionUsuarioInicialDto>> ReintentarActivacionAsync(
        Guid usuarioId, string urlBaseAbsoluta, CancellationToken cancellationToken = default);
}

/// <param name="CorreoEnviado">
/// False si el envío falló — el enlace sigue siendo válido y
/// <paramref name="EnlaceActivacion"/> es la contingencia manual para
/// entregarlo por otra vía, no un indicio de que el alta o el reintento
/// fallaran.
/// </param>
public record ActivacionUsuarioInicialDto(
    Guid UsuarioId, string EnlaceActivacion, bool CorreoEnviado, int IntentosActivacion);
