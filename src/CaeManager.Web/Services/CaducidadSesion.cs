using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace CaeManager.Web.Services;

/// <summary>
/// Caducidad de la sesión de la cookie de aplicación (decisión fijada): 8 horas
/// de inactividad, deslizante, y como máximo 12 horas desde el inicio de sesión
/// aunque haya actividad. Antes la cookie de Identity era la de por defecto:
/// 14 días deslizante, sin tope absoluto.
///
/// <para>
/// <b>Hora de inicio.</b> La primera emisión de la sesión (contraseña, segundo
/// factor, callback de Microsoft) la guarda en
/// <see cref="AuthenticationProperties.Items"/> y como claim del principal, ambos
/// dentro del ticket cifrado. Una reemisión que conserva las propiedades del
/// ticket —el refresco deslizante del manejador de cookies, el del
/// <c>SecurityStampValidator</c> y <c>RefreshSignInAsync</c> al cambiar la
/// contraseña o activar el segundo factor— conserva la hora original: deslizar
/// no reinicia el tope. «Recordarme» solo hace persistente la cookie en el
/// navegador; el tope se comprueba en el servidor igual.
/// </para>
///
/// <para>
/// <b>Fallo cerrado.</b> La cookie sin hora de inicio legible (también la de una
/// sesión emitida antes de este cambio) o con más de 12 horas se rechaza y se
/// borra. El circuito de Blazor no hace peticiones HTTP:
/// <see cref="ProveedorAutenticacionRevalidada"/> mira el claim en cada ciclo de
/// revalidación y pasa a anónimo sin consultar la base.
/// </para>
///
/// <para>
/// <b>Cookie Secure.</b> Siempre, salvo en Development: el arnés E2E de CI sirve
/// por http://127.0.0.1 en Development, y una cookie Secure no viaja por http.
/// </para>
/// </summary>
public static class CaducidadSesion
{
    /// <summary>Clave en las propiedades del ticket y tipo del claim con la hora de inicio (segundos Unix).</summary>
    public const string MarcaInicio = "talveg:inicio-sesion";

    public static readonly TimeSpan Inactividad = TimeSpan.FromHours(8);
    public static readonly TimeSpan DuracionMaxima = TimeSpan.FromHours(12);

    public static void ConfigurarHsts(IServiceCollection servicios) =>
        servicios.AddHsts(opciones => opciones.MaxAge = TimeSpan.FromDays(365));

    public static void Configurar(CookieAuthenticationOptions opciones, IHostEnvironment entorno)
    {
        opciones.ExpireTimeSpan = Inactividad;
        opciones.SlidingExpiration = true;
        opciones.Cookie.SecurePolicy = entorno.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
        opciones.Cookie.HttpOnly = true;
        if (opciones.Cookie.SameSite == SameSiteMode.None)
            opciones.Cookie.SameSite = SameSiteMode.Lax;

        var firmarOriginal = opciones.Events.OnSigningIn;
        opciones.Events.OnSigningIn = async contexto =>
        {
            // Solo una emisión con propiedades nuevas fija la hora; una que trae las
            // del ticket vigente (aunque ya no valga) la conserva tal cual.
            if (!contexto.Properties.Items.TryGetValue(MarcaInicio, out var inicio) || inicio is null)
            {
                inicio = Reloj(contexto.HttpContext).GetUtcNow().ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
                contexto.Properties.Items[MarcaInicio] = inicio;
            }
            PonerMarca(contexto.Principal, inicio);
            await firmarOriginal(contexto);
        };

        // Envuelve al SecurityStampValidator (y a OmitirRevalidacionDeStampEnRuta):
        // el tope se mira antes de consultar la base, también en /Error.
        var validarOriginal = opciones.Events.OnValidatePrincipal;
        opciones.Events.OnValidatePrincipal = async contexto =>
        {
            if (!contexto.Properties.Items.TryGetValue(MarcaInicio, out var inicio)
                || inicio is null
                || !EsVigente(inicio, Reloj(contexto.HttpContext).GetUtcNow()))
            {
                contexto.RejectPrincipal();
                await contexto.HttpContext.SignOutAsync(contexto.Scheme.Name);
                return;
            }
            await validarOriginal(contexto);
            // El SecurityStampValidator reemplaza el principal por uno recién
            // construido desde la base, sin la marca: se le vuelve a poner.
            PonerMarca(contexto.Principal, inicio);
        };
    }

    /// <summary>El principal lleva una hora de inicio legible y de hace menos de <see cref="DuracionMaxima"/>.</summary>
    public static bool EsVigente(ClaimsPrincipal principal, DateTimeOffset ahora) =>
        EsVigente(principal.FindFirst(MarcaInicio)?.Value, ahora);

    private static bool EsVigente(string? marca, DateTimeOffset ahora) =>
        long.TryParse(marca, NumberStyles.None, CultureInfo.InvariantCulture, out var segundos)
        && segundos <= ahora.ToUnixTimeSeconds()
        && ahora.ToUnixTimeSeconds() - segundos < DuracionMaxima.TotalSeconds;

    private static TimeProvider Reloj(HttpContext contexto) =>
        contexto.RequestServices.GetService<TimeProvider>() ?? TimeProvider.System;

    private static void PonerMarca(ClaimsPrincipal? principal, string inicio)
    {
        if (principal?.Identity is not ClaimsIdentity identidad) return;
        foreach (var claim in identidad.FindAll(MarcaInicio).ToArray()) identidad.RemoveClaim(claim);
        identidad.AddClaim(new Claim(MarcaInicio, inicio));
    }
}
