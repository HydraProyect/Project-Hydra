namespace CaeManager.Web.Components.Account;

/// <summary>
/// A dónde lleva «Mi cuenta» en el menú de usuario (<c>MenuUsuario</c>, FS-16): las
/// páginas de cuenta que ya existían y a las que solo se llegaba forzado por un guard
/// (cambio obligatorio de contraseña, 2FA obligatoria del Administrador) o al intentar
/// leer una credencial sin 2FA. No decide quién está obligado a nada: solo es la puerta
/// voluntaria. Las dos páginas son estáticas y con otro layout, así que el enlace va con
/// <c>data-enhance-nav="false"</c>.
/// </summary>
public static class RutasMiCuenta
{
    /// <summary>
    /// Marca de que se llega a cambiar la contraseña por voluntad propia. Igual que
    /// <c>motivo=credenciales</c> en la página de 2FA, solo decide un texto y si se pinta
    /// «Volver»: quien obliga al cambio sigue siendo el guard de <c>MainLayout</c>, que
    /// relee <c>DebeCambiarContrasena</c> de la base en cada navegación.
    /// </summary>
    public const string MotivoMiCuenta = "mi-cuenta";

    /// <summary>Alta del segundo factor y códigos de recuperación de la propia cuenta.</summary>
    public const string VerificacionDosPasos = "/cuenta/configurar-2fa";

    public const string CambiarContrasena = "/cuenta/cambiar-contrasena?motivo=" + MotivoMiCuenta;
}
