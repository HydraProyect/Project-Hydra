using CaeManager.Application.Common;
using CaeManager.Application.VistaDemo;
using CaeManager.Web.Components.Account;
using CaeManager.Web.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;

namespace CaeManager.Web.Features.Tenants;

/// <summary>
/// Cambia la vista de demo de la cuenta (selector de la cabecera). Endpoint HTTP + cookie +
/// recarga completa, por el mismo motivo que <see cref="ClienteActivoEndpoints"/> y
/// <see cref="VistaVocabularioPreviewEndpoints"/>: el alcance de datos se memoiza por circuito de
/// Blazor, y solo un circuito nuevo lo recalcula.
///
/// Solo ESCRIBE una petición: quien decide después si la lente vale es
/// <see cref="IVistaDemoActual"/>. Lo que sí impide este endpoint es guardar peticiones que ya
/// sabe inválidas — cuenta no elegible (sin activación, sin rol, Tenant real) o un Gestor que no
/// es de su Operador CAE — para que un valor imposible no llegue ni a la cookie.
///
/// El camino de vuelta no depende de ninguna vista: «Dirección» (o vacío) borra la cookie, es
/// decir, la autorización real sin acotar, y este endpoint se autoriza con la identidad real,
/// nunca con la vista aplicada.
/// </summary>
public static class VistaDemoEndpoints
{
    public const string OpcionDireccion = "direccion";
    public const string OpcionCoordinador = "coordinador";
    public const string PrefijoOpcionGestor = "gestor:";

    public static IEndpointRouteBuilder MapVistaDemoEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // POST, no GET: cambia estado. UseAntiforgery valida el token del formulario (ver Program.cs).
        endpoints.MapPost("/cuenta/vista-demo", async (
            HttpContext httpContext,
            IVistaDemoActual vistaDemo,
            ICurrentUserService currentUserService,
            IDataProtectionProvider dataProtectionProvider,
            [FromForm] string? opcion,
            [FromForm] string? returnUrl,
            CancellationToken cancellationToken) =>
        {
            if (!await vistaDemo.EstaDisponibleAsync(cancellationToken)
                || await currentUserService.ObtenerUsuarioActualIdAsync() is not { } usuarioId)
                return Results.Forbid();

            var valor = ResolverCookie(opcion, usuarioId, dataProtectionProvider,
                await vistaDemo.ObtenerGestoresElegiblesAsync(cancellationToken));

            if (valor is null)
            {
                // Dirección, vacío o cualquier valor no reconocido: la autorización real, sin acotar.
                httpContext.Response.Cookies.Delete(VistaDemoCookie.NombreCookie);
            }
            else
            {
                httpContext.Response.Cookies.Append(VistaDemoCookie.NombreCookie, valor, new CookieOptions
                {
                    HttpOnly = true,
                    Secure = httpContext.Request.IsHttps,
                    SameSite = SameSiteMode.Lax,
                    MaxAge = VistaDemoCookie.Vigencia,
                });
            }

            return Results.LocalRedirect(RedireccionLocal.Sanear(returnUrl));
        });

        return endpoints;
    }

    private static string? ResolverCookie(
        string? opcion, Guid usuarioId, IDataProtectionProvider proveedor, IReadOnlyList<GestorDeVistaDemo> gestoresElegibles)
    {
        if (string.Equals(opcion, OpcionCoordinador, StringComparison.Ordinal))
            return VistaDemoCookie.Proteger(proveedor, usuarioId, VistaDemo.CoordinadorCae, null);

        if (opcion is not null && opcion.StartsWith(PrefijoOpcionGestor, StringComparison.Ordinal)
            && Guid.TryParseExact(opcion[PrefijoOpcionGestor.Length..], "N", out var gestorId)
            && gestoresElegibles.Any(g => g.UsuarioId == gestorId))
            return VistaDemoCookie.Proteger(proveedor, usuarioId, VistaDemo.GestorCae, gestorId);

        return null;
    }
}
