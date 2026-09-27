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
    /// el Tenant por defecto de su cartera mientras dure (8 h, la vigencia de una
    /// selección; después se vuelve a evaluar). <b>No tiene valor de
    /// seguridad</b>: su único efecto es no seleccionar nada, y quedarse en el
    /// Tenant de origen siempre está autorizado. Guarda el Id del usuario en claro
    /// solo para no aplicarse a otra cuenta en el mismo navegador.
    /// </summary>
    public const string NombreOrigenElegido = "cae_origen_elegido";

    /// <summary>
    /// Marca «Tenant por defecto ya evaluado, sin resultado»: evita que
    /// <see cref="TenantBeneficiarioPorDefectoMiddleware"/> repita la consulta de
    /// Tenants autorizados en cada navegación de página de un usuario que no tiene
    /// Tenant por defecto (la inmensa mayoría, mono-Tenant). Mismo carácter que
    /// <see cref="NombreOrigenElegido"/>: sin valor de seguridad, ligada al usuario.
    /// Una cartera nueva se nota como mucho <see cref="VigenciaDefectoEvaluado"/>
    /// después.
    /// </summary>
    public const string NombreDefectoEvaluado = "cae_defecto_evaluado";

    public static readonly TimeSpan VigenciaDefectoEvaluado = TimeSpan.FromMinutes(15);

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

    public static void RecordarDefectoEvaluado(HttpContext httpContext, Guid usuarioId) =>
        httpContext.Response.Cookies.Append(
            NombreDefectoEvaluado, usuarioId.ToString("N"), Opciones(httpContext, VigenciaDefectoEvaluado));

    public static bool DefectoEvaluado(HttpRequest peticion, Guid usuarioId) =>
        peticion.Cookies[NombreDefectoEvaluado] == usuarioId.ToString("N");

    private static CookieOptions Opciones(HttpContext httpContext, TimeSpan? vigencia = null) => new()
    {
        HttpOnly = true,
        // Toda la aplicación, explícito: las marcas se leen en cualquier página,
        // no solo bajo el directorio de la primera que las emitió.
        Path = "/",
        // Igual que la política por defecto de la cookie de Identity
        // (SameAsRequest): en local sobre HTTP la marca Secure haría
        // que el navegador descartara la cookie sin avisar. Detrás
        // del proxy de despliegue, UseForwardedHeaders ya hace que
        // IsHttps refleje el esquema original, no el interno.
        Secure = httpContext.Request.IsHttps,
        SameSite = SameSiteMode.Lax,
        // Misma vigencia que la del propio token, que es la que
        // de verdad se comprueba en servidor al descifrarlo.
        MaxAge = vigencia ?? ClienteActivoSeleccionado.Vigencia,
    };
}
