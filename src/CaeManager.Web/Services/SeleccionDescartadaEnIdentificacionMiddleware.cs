using CaeManager.Application.Common;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace CaeManager.Web.Services;

/// <summary>
/// Las rutas que identifican a una persona con sus propias credenciales o con el token de su buzón no corren bajo la
/// selección del navegador (OWASP ASVS 4.0.3 V4.1.3): con una Sesión Privilegiada seleccionada, una cookie de otra pestaña
/// haría que <c>TenantRlsConnectionInterceptor</c> adoptara <c>cae_app_soporte</c>, que no lee las columnas de credenciales
/// de <c>AspNetUsers</c> ni escribe.
///
/// <para>
/// No es una guarda: estas páginas siguen sin barrera propia, porque son anónimas o su autoridad es el token del buzón. Solo
/// quita una coordenada, el mismo gesto que <c>CierreSesionEnServidor</c> hace con su ámbito sin selección: no da rol, ni
/// contexto elevado, ni acceso al Tenant seleccionado, y la petición corre como cualquier sesión sin selección. La cookie no
/// se borra, porque la Sesión Privilegiada sigue abierta en las demás pestañas. Va detrás de
/// <see cref="RevalidacionClienteActivoMiddleware"/>, que borraría esa cookie si viera una selección nula.
/// </para>
/// </summary>
public class SeleccionDescartadaEnIdentificacionMiddleware(RequestDelegate siguiente)
{
    private static readonly PathString[] RutasDeIdentificacion =
    [
        "/cuenta/iniciar-sesion",
        "/cuenta/verificar-2fa",
        "/cuenta/microsoft-callback",
        "/cuenta/no-he-sido-yo",
    ];

    public async Task InvokeAsync(HttpContext contexto, IClienteActivoSeleccionado seleccion)
    {
        if (EsRutaDeIdentificacion(contexto.Request.Path))
            (seleccion as ClienteActivoSeleccionado)?.DescartarEnEstaPeticion();

        await siguiente(contexto);
    }

    /// <summary>Por segmentos: <c>/cuenta/iniciar-sesion-microsoft</c> no está aquí, porque solo redirige al proveedor.</summary>
    public static bool EsRutaDeIdentificacion(PathString ruta) =>
        RutasDeIdentificacion.Any(identificacion => ruta.StartsWithSegments(identificacion));
}

public static class SeleccionDescartadaEnIdentificacionMiddlewareExtensions
{
    public static IApplicationBuilder UseSeleccionDescartadaEnIdentificacion(this IApplicationBuilder app) =>
        app.UseMiddleware<SeleccionDescartadaEnIdentificacionMiddleware>();
}
