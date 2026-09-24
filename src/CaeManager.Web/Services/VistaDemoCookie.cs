using System.Security.Claims;
using CaeManager.Application.VistaDemo;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;

namespace CaeManager.Web.Services;

/// <summary>
/// Lee de una cookie la vista de demo que pide el selector de la cabecera (ver
/// <see cref="IVistaDemoActual"/>). La cookie es PETICIÓN, no autoridad: quien decide si vale es
/// <c>VistaDemoActual</c>, que la valida contra la identidad, el Tenant y las carteras reales, y
/// aun cuando vale solo estrecha. Por eso este servicio no comprueba nada de eso: solo descarta
/// lo que no es un valor bien formado emitido para ESTA cuenta.
///
/// <para>
/// A diferencia de la cookie de vocabulario (texto plano, sin efecto sobre datos), esta va
/// protegida con Data Protection y ligada al usuario, como la de Tenant activo: la vista Gestor
/// lleva un Id de usuario y cambia qué filas se ven, así que un valor copiado a la sesión de otra
/// cuenta, manipulado o caducado no debe llegar siquiera a validarse. Cualquier fallo devuelve
/// «sin petición» — nunca una excepción ni un valor por defecto que amplíe algo.
/// </para>
/// </summary>
public class VistaDemoCookie(
    AuthenticationStateProvider authenticationStateProvider,
    IHttpContextAccessor httpContextAccessor,
    IDataProtectionProvider dataProtectionProvider) : ISolicitudVistaDemo
{
    public const string NombreCookie = "cae_vista_demo";

    private const string PropositoProtector = "CaeManager.Web.VistaDemoCookie.v1";

    public static readonly TimeSpan Vigencia = TimeSpan.FromHours(8);

    private PeticionVistaDemo? _peticion;

    public async Task<PeticionVistaDemo> ObtenerAsync()
    {
        if (_peticion is not null) return _peticion;

        var usuario = await ObtenerUsuarioAsync();
        var (vista, gestor) = LeerCargaUtil(
            dataProtectionProvider,
            httpContextAccessor.HttpContext?.Request.Cookies[NombreCookie],
            usuario is null ? null : LeerUsuarioId(usuario));

        return _peticion = new PeticionVistaDemo(vista, gestor);
    }

    private static Guid? LeerUsuarioId(ClaimsPrincipal usuario) =>
        Guid.TryParse(usuario.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var id) ? id : null;

    // Mismo criterio que CurrentUserService: dentro de un circuito manda el estado de autenticación
    // del circuito; fuera (endpoints), el HttpContext ya autenticado por la cookie de Identity.
    private async Task<ClaimsPrincipal?> ObtenerUsuarioAsync()
    {
        try
        {
            var estado = await authenticationStateProvider.GetAuthenticationStateAsync();
            if (estado.User.Identity?.IsAuthenticated == true) return estado.User;
        }
        catch (InvalidOperationException)
        {
            // sin circuito de Blazor — se prueba el HttpContext.
        }

        // Un circuito cuya sesión ya no vale no recupera identidad por el HttpContext heredado.
        if (authenticationStateProvider is ISesionDeCircuitoInvalidable { SesionInvalidada: true })
            return null;

        var http = httpContextAccessor.HttpContext?.User;
        return http?.Identity?.IsAuthenticated == true ? http : null;
    }

    public static string Proteger(IDataProtectionProvider dataProtectionProvider, Guid usuarioId, VistaDemo vista, Guid? gestorUsuarioId) =>
        CrearProtector(dataProtectionProvider).Protect(
            $"{usuarioId:N}|{(int)vista}|{gestorUsuarioId ?? Guid.Empty:N}", Vigencia);

    public static (VistaDemo? Vista, Guid? GestorUsuarioId) LeerCargaUtil(
        IDataProtectionProvider dataProtectionProvider, string? valorCookie, Guid? usuarioActual)
    {
        // Sin usuario autenticado no hay a quién ligar el valor.
        if (string.IsNullOrEmpty(valorCookie) || usuarioActual is null)
            return (null, null);

        string cargaUtil;
        try
        {
            cargaUtil = CrearProtector(dataProtectionProvider).Unprotect(valorCookie);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            // Ajeno, manipulado, caducado o de un llavero que ya no existe: se ignora.
            return (null, null);
        }

        var partes = cargaUtil.Split('|');
        if (partes.Length != 3
            || !Guid.TryParseExact(partes[0], "N", out var usuarioDelValor)
            || !int.TryParse(partes[1], out var vistaCruda)
            || !Enum.IsDefined(typeof(VistaDemo), vistaCruda)
            || !Guid.TryParseExact(partes[2], "N", out var gestorCrudo))
            return (null, null);

        // Ligado al usuario: el valor de otra cuenta no vale.
        if (usuarioDelValor != usuarioActual.Value)
            return (null, null);

        var vista = (VistaDemo)vistaCruda;
        var gestor = gestorCrudo == Guid.Empty ? (Guid?)null : gestorCrudo;

        // El Gestor solo tiene sentido con la vista Gestor, y la vista Gestor exige Gestor.
        if ((vista == VistaDemo.GestorCae) != (gestor is not null))
            return (null, null);

        return (vista, gestor);
    }

    private static ITimeLimitedDataProtector CrearProtector(IDataProtectionProvider dataProtectionProvider) =>
        dataProtectionProvider.CreateProtector(PropositoProtector).ToTimeLimitedDataProtector();
}
