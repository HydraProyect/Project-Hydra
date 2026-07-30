using System.Security.Claims;
using CaeManager.Application.Common;
using Microsoft.AspNetCore.DataProtection;

namespace CaeManager.Web.Services;

/// <summary>
/// Lee el Delegated Workspace activo desde una cookie en vez de guardarlo
/// solo en memoria — necesario porque cambiar de cliente activo requiere un
/// reload completo del navegador (ver <c>SelectorClienteActivo.razor.cs</c>
/// y el endpoint <c>/cuenta/cliente-activo/{tenantId}</c>): un valor
/// puramente en memoria vive en el scope de DI del circuito de Blazor
/// Server, y ese scope se destruye por completo en cada reload — con solo
/// memoria, la elección nunca sobreviviría al propio mecanismo que la
/// aplica (bug real encontrado al probar el selector: cambiar de cliente
/// con <c>forceLoad: true</c> revertía siempre al tenant de origen). La
/// cookie sí sobrevive al reload, así que <c>ITenantActual</c> puede
/// resolver el nuevo tenant desde la primera petición del circuito nuevo.
///
/// La cookie es un <b>token protegido</b>, no un GUID en claro. Antes lo era,
/// y eso convertía el valor en autoridad falsificable: <c>HttpOnly</c> impide
/// que lo lea JavaScript de terceros, no que el propio usuario autenticado lo
/// escriba a mano con devtools o <c>curl</c> sobre su sesión legítima. Como
/// <c>ITenantActual</c> es la única fuente del filtro global de EF Core, del
/// <c>TenantSelladoInterceptor</c> y del particionado de almacenamiento, una
/// cookie fabricada bastaba para tomar el control de cualquier tenant —
/// incluido el #1, cuyo Id es determinista y público en el código (hallazgo
/// C-1 de INFORME-AUDITORIA-TECNICA.md; regresión cubierta en
/// <c>SecuestroTenantPorCookieTests</c>).
///
/// El token lo emite <see cref="Proteger"/> únicamente desde el endpoint, que
/// es quien revalida la delegación contra
/// <c>DelegacionTenant</c>/<c>AsignacionOperadorDelegado</c>. Va firmado y
/// cifrado con Data Protection, <b>ligado al usuario</b> para el que se emitió
/// y con caducidad propia en servidor. Así la lectura sigue siendo síncrona y
/// sin I/O — <c>ITenantActual.TenantId</c> se evalúa dentro de
/// <c>HasQueryFilter</c> y consultar la base de datos ahí sería una regresión
/// de rendimiento severa —, pero un valor que no emitió este sistema, que fue
/// manipulado, que caducó o que pertenece a otro usuario, simplemente no se
/// descifra y se descarta (fallo cerrado: se cae al claim firmado de sesión).
/// </summary>
public class ClienteActivoSeleccionado(
    IHttpContextAccessor httpContextAccessor,
    IDataProtectionProvider dataProtectionProvider) : IClienteActivoSeleccionado
{
    public const string NombreCookie = "cae_cliente_activo";

    /// <summary>
    /// Purpose string de Data Protection: aísla criptográficamente este token
    /// de los demás protectores del sistema (credenciales de portales
    /// externos, ver <c>CaeManagerDbContext</c>), de modo que un valor de uno
    /// nunca es válido en el otro. Versionado: cambiarlo invalida de golpe
    /// todos los tokens en circulación.
    /// </summary>
    private const string PropositoProtector = "CaeManager.Web.ClienteActivoSeleccionado.v1";

    /// <summary>
    /// Vigencia del token en servidor. Coincide con el <c>MaxAge</c> de la
    /// cookie, pero esa es una indicación al navegador que un atacante ignora
    /// reenviando el valor; esta la comprueba el servidor al descifrar.
    /// </summary>
    public static readonly TimeSpan Vigencia = TimeSpan.FromHours(12);

    private Guid? _tenantIdSeleccionado;
    private bool _leidoDeCookie;

    /// <summary>
    /// Emite el token para la cookie. Solo debe llamarlo el endpoint
    /// <c>/cuenta/cliente-activo/{tenantId}</c>, y solo después de haber
    /// comprobado que <paramref name="usuarioId"/> tiene delegación activa
    /// sobre <paramref name="tenantId"/>.
    /// </summary>
    public static string Proteger(IDataProtectionProvider dataProtectionProvider, Guid usuarioId, Guid tenantId) =>
        CrearProtector(dataProtectionProvider).Protect($"{usuarioId:N}|{tenantId:N}", Vigencia);

    public Guid? TenantIdSeleccionado
    {
        get
        {
            if (!_leidoDeCookie)
            {
                _tenantIdSeleccionado = LeerDeCookie();
                _leidoDeCookie = true;
            }

            return _tenantIdSeleccionado;
        }
    }

    private Guid? LeerDeCookie()
    {
        var httpContext = httpContextAccessor.HttpContext;
        var valorCookie = httpContext?.Request.Cookies[NombreCookie];
        if (string.IsNullOrEmpty(valorCookie))
            return null;

        // Sin usuario autenticado no hay a quién ligar el token: una cookie
        // suelta nunca debe abrir un contexto de tenant por sí sola.
        var usuarioActual = LeerUsuarioActual(httpContext!.User);
        if (usuarioActual is null)
            return null;

        string cargaUtil;
        try
        {
            cargaUtil = CrearProtector(dataProtectionProvider).Unprotect(valorCookie);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            // Token ajeno, manipulado, caducado o emitido con un llavero que
            // ya no existe. No es un caso excepcional que haya que registrar
            // como error: es exactamente lo que se espera de un valor no
            // válido, y la respuesta correcta es ignorarlo.
            return null;
        }

        var separador = cargaUtil.IndexOf('|');
        if (separador < 0
            || !Guid.TryParseExact(cargaUtil[..separador], "N", out var usuarioDelToken)
            || !Guid.TryParseExact(cargaUtil[(separador + 1)..], "N", out var tenantId))
            return null;

        // Ligado al usuario: un token legítimo copiado a la sesión de otro no
        // le transfiere el acceso al workspace.
        return usuarioDelToken == usuarioActual.Value ? tenantId : null;
    }

    private static Guid? LeerUsuarioActual(ClaimsPrincipal usuario)
    {
        if (usuario.Identity?.IsAuthenticated != true)
            return null;

        var valorClaim = usuario.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return Guid.TryParse(valorClaim, out var usuarioId) ? usuarioId : null;
    }

    private static ITimeLimitedDataProtector CrearProtector(IDataProtectionProvider dataProtectionProvider) =>
        dataProtectionProvider.CreateProtector(PropositoProtector).ToTimeLimitedDataProtector();
}
