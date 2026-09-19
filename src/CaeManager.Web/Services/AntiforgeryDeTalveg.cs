using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http;

namespace CaeManager.Web.Services;

/// <summary>
/// Configuración de <c>AddAntiforgery</c>. Sin ella rige el valor por defecto
/// del framework, <c>CookieSecurePolicy.None</c>: la cookie de antiforgery se
/// emitía por HTTPS sin la marca <c>Secure</c> (medido en producción el
/// 2026-09-19).
///
/// <para>
/// <b>Por qué <c>SameAsRequest</c> y no <c>Always</c>.</b> Con <c>Always</c>, en
/// local sobre HTTP el navegador descartaría la cookie sin avisar y todo POST
/// de formulario fallaría la validación. <c>SameAsRequest</c> marca
/// <c>Secure</c> exactamente cuando <c>Request.IsHttps</c>, y detrás del proxy
/// de despliegue <c>UseForwardedHeaders</c> (ver
/// <see cref="CabecerasDeProxyDeBorde"/>) ya hace que <c>IsHttps</c> refleje el
/// esquema original. Es el mismo criterio de <c>ClienteActivoEndpoints</c>.
/// </para>
///
/// <para>
/// Función con nombre, y no una lambda en <c>Program.cs</c>, para que un test
/// pueda observarla (mismo motivo que <see cref="CabecerasDeProxyDeBorde"/>).
/// </para>
/// </summary>
public static class AntiforgeryDeTalveg
{
    public static void Configurar(AntiforgeryOptions opciones) =>
        opciones.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
}
