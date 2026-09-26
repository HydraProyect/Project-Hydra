using Microsoft.AspNetCore.DataProtection;

namespace CaeManager.Web.Services;

/// <summary>
/// Escritura de la cookie de selección de contexto —el Tenant beneficiario
/// activo— y de la preferencia «volvió a su Tenant de origen». Una sola
/// definición de sus atributos para <c>/cuenta/cliente-activo</c> y para
/// <see cref="TenantBeneficiarioPorDefectoMiddleware"/>. El formato del token y
/// su lectura siguen en <see cref="ClienteActivoSeleccionado"/>.
/// </summary>
public static class CookieDeContextoTenant
{
    private const string NombreSeleccion = ClienteActivoSeleccionado.NombreCookie;

    /// <summary>
    /// Preferencia: el usuario volvió a su Tenant de origen a propósito (POST a
    /// <c>/cuenta/cliente-activo</c> con su propio Tenant), así que
    /// <see cref="TenantBeneficiarioPorDefectoMiddleware"/> no vuelve a activarle
    /// el Tenant por defecto de su cartera. <b>No tiene valor de seguridad</b>: su
    /// único efecto es no seleccionar nada, y quedarse en el Tenant de origen
    /// siempre está autorizado. Guarda el Id del usuario en claro solo para no
    /// aplicarse a otra cuenta en el mismo navegador.
    /// </summary>
    public const string NombreOrigenElegido = "cae_origen_elegido";

    public static bool HaySeleccion(HttpRequest peticion) =>
        !string.IsNullOrEmpty(peticion.Cookies[NombreSeleccion]);

    /// <summary>
    /// Emite la selección de <paramref name="tenantId"/>. Solo tras haber
    /// comprobado con <c>TenantsBeneficiariosAutorizados</c> que el usuario tiene
    /// autorización viva sobre él. Token protegido y ligado al usuario, no el
    /// GUID en claro: esta autorización se comprueba al escribir, y el sellado
    /// criptográfico es lo que hace que siga valiendo al leer. Sin él, la
    /// comprobación era trivialmente esquivable escribiendo la cookie a mano (C-1).
    /// </summary>
    public static void EmitirSeleccion(
        HttpContext httpContext, IDataProtectionProvider dataProtectionProvider,
        Guid usuarioId, Guid tenantId, Guid? asignacionOperacionId)
    {
        httpContext.Response.Cookies.Append(
            NombreSeleccion,
            ClienteActivoSeleccionado.Proteger(dataProtectionProvider, usuarioId, tenantId, asignacionOperacionId),
            Opciones(httpContext));
        httpContext.Response.Cookies.Delete(NombreOrigenElegido);
    }

    /// <summary>
    /// Vuelta al Tenant de origen: basta con borrar la selección, no hace falta
    /// que «seleccione explícitamente» su propio Tenant — así el claim de sesión
    /// vuelve a mandar. Y se recuerda que fue a propósito (decisión 5).
    /// </summary>
    public static void VolverAlOrigen(HttpContext httpContext, Guid usuarioId)
    {
        httpContext.Response.Cookies.Delete(NombreSeleccion);
        httpContext.Response.Cookies.Append(NombreOrigenElegido, usuarioId.ToString("N"), Opciones(httpContext));
    }

    public static bool OrigenElegido(HttpRequest peticion, Guid usuarioId) =>
        peticion.Cookies[NombreOrigenElegido] == usuarioId.ToString("N");

    private static CookieOptions Opciones(HttpContext httpContext) => new()
    {
        HttpOnly = true,
        // Igual que la política por defecto de la cookie de Identity
        // (SameAsRequest): en local sobre HTTP la marca Secure haría
        // que el navegador descartara la cookie sin avisar. Detrás
        // del proxy de despliegue, UseForwardedHeaders ya hace que
        // IsHttps refleje el esquema original, no el interno.
        Secure = httpContext.Request.IsHttps,
        SameSite = SameSiteMode.Lax,
        // Misma vigencia que la del propio token, que es la que
        // de verdad se comprueba en servidor al descifrarlo.
        MaxAge = ClienteActivoSeleccionado.Vigencia,
    };
}
