using CaeManager.Application.Common;
using System.Security.Claims;
using Microsoft.AspNetCore.DataProtection;

namespace CaeManager.Web.Services;

/// <summary>
/// Lee el Delegated Workspace activo desde una cookie en vez de guardarlo
/// solo en memoria — necesario porque cambiar de cliente activo requiere un
/// reload completo del navegador (ver <c>SelectorClienteActivo.razor.cs</c>
/// y el endpoint <c>/cuenta/cliente-activo</c>): un valor
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
    /// v2: la carga útil pasó de <c>usuario|tenant</c> a
    /// <c>usuario|tenant|asignacionOperacion</c>. El cambio de purpose invalida
    /// de golpe los tokens v1 en circulación, que es lo correcto — no se pueden
    /// reinterpretar sin inventar el tercer campo. El coste es que quien tuviera
    /// un workspace abierto vuelve a elegirlo; la vigencia era de 8 h.
    ///
    /// El tenant sigue viajando en el token, y no se deriva de la operación, por
    /// una razón dura: <c>ITenantActual</c> se evalúa dentro del filtro global
    /// de consultas y es síncrono y sin acceso a datos. Resolver ahí el
    /// propietario a partir del Id de la operación exigiría una consulta que ese
    /// contrato no admite. La coherencia entre los dos campos la comprueba
    /// <see cref="RevalidacionClienteActivoMiddleware"/>.
    private const string PropositoProtector = "CaeManager.Web.ClienteActivoSeleccionado.v2";

    /// <summary>
    /// Vigencia del token en servidor. Coincide con el <c>MaxAge</c> de la
    /// cookie, pero esa es una indicación al navegador que un atacante ignora
    /// reenviando el valor; esta la comprueba el servidor al descifrar.
    ///
    /// Una jornada, no las 12 h de antes: es la ventana en la que el valor
    /// sigue sirviendo si se extrae del navegador, y no hay ningún flujo
    /// legítimo que necesite operar el mismo workspace delegado más allá de
    /// un día de trabajo. Lo que cierra la revocación no es esto sino
    /// <see cref="RevalidacionClienteActivoMiddleware"/>, que comprueba la
    /// delegación viva en cada petición (hallazgo N-6 de
    /// INFORME-AUDITORIA-2.md); acortarla a minutos habría obligado a
    /// reelegir cliente a mitad de jornada sin cerrar nada que el middleware
    /// no cierre ya.
    /// </summary>
    public static readonly TimeSpan Vigencia = TimeSpan.FromHours(8);

    private Guid? _tenantIdSeleccionado;
    private Guid? _asignacionOperacionIdSeleccionada;
    private bool _leidoDeCookie;

    /// <summary>
    /// Emite el token para la cookie. Solo debe llamarlo el endpoint
    /// <c>/cuenta/cliente-activo</c>, y solo después de haber
    /// comprobado que <paramref name="usuarioId"/> tiene autorización viva
    /// sobre <paramref name="tenantId"/>.
    /// </summary>
    /// <param name="asignacionOperacionId">
    /// La operación por la que se abre el workspace, o <c>null</c> para la vía
    /// heredada (delegación de soporte), que todavía no tiene operación propia.
    /// Se serializa como Guid vacío para que la carga útil tenga siempre tres
    /// campos y el parseo no dependa de contar separadores.
    /// </param>
    public static string Proteger(
        IDataProtectionProvider dataProtectionProvider, Guid usuarioId, Guid tenantId, Guid? asignacionOperacionId) =>
        CrearProtector(dataProtectionProvider).Protect(
            $"{usuarioId:N}|{tenantId:N}|{asignacionOperacionId ?? Guid.Empty:N}", Vigencia);

    public Guid? TenantIdSeleccionado
    {
        get
        {
            AsegurarLeidoDeCookie();
            return _tenantIdSeleccionado;
        }
    }

    public Guid? AsignacionOperacionIdSeleccionada
    {
        get
        {
            AsegurarLeidoDeCookie();
            return _asignacionOperacionIdSeleccionada;
        }
    }

    private void AsegurarLeidoDeCookie()
    {
        if (_leidoDeCookie) return;

        var (tenantId, asignacionOperacionId) = LeerDeCookie();
        _tenantIdSeleccionado = tenantId;
        _asignacionOperacionIdSeleccionada = asignacionOperacionId;
        _leidoDeCookie = true;
    }

    /// <summary>
    /// Descarta la selección para el resto de esta petición. Lo llama
    /// <see cref="RevalidacionClienteActivoMiddleware"/> cuando comprueba que
    /// la delegación ya no está viva: borrar la cookie de la respuesta no
    /// basta, porque <c>Request.Cookies</c> sigue trayéndola en la petición
    /// que se está sirviendo y el tenant se resolvería una vez más al
    /// workspace retirado.
    /// </summary>
    public void Invalidar()
    {
        _tenantIdSeleccionado = null;
        _asignacionOperacionIdSeleccionada = null;
        _leidoDeCookie = true;
    }

    private (Guid? TenantId, Guid? AsignacionOperacionId) LeerDeCookie()
    {
        var httpContext = httpContextAccessor.HttpContext;
        var valorCookie = httpContext?.Request.Cookies[NombreCookie];
        if (string.IsNullOrEmpty(valorCookie))
            return (null, null);

        // Sin usuario autenticado no hay a quién ligar el token: una cookie
        // suelta nunca debe abrir un contexto de tenant por sí sola.
        var usuarioActual = LeerUsuarioActual(httpContext!.User);
        if (usuarioActual is null)
            return (null, null);

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
            return (null, null);
        }

        var partes = cargaUtil.Split('|');
        if (partes.Length != 3
            || !Guid.TryParseExact(partes[0], "N", out var usuarioDelToken)
            || !Guid.TryParseExact(partes[1], "N", out var tenantId)
            || !Guid.TryParseExact(partes[2], "N", out var asignacionOperacionId))
            return (null, null);

        // Ligado al usuario: un token legítimo copiado a la sesión de otro no
        // le transfiere el acceso al workspace.
        if (usuarioDelToken != usuarioActual.Value)
            return (null, null);

        // Guid vacío = vía heredada (soporte). Se distingue de "no hay
        // operación" para que quien lo consume sepa por qué camino validar.
        return (tenantId, asignacionOperacionId == Guid.Empty ? null : asignacionOperacionId);
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
