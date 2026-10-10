using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Playwright;

namespace CaeManager.E2ETests;

/// <summary>
/// Sesión reutilizada por cuenta y por fixture: la vía rápida de
/// <see cref="Ayudas.IniciarSesionAsync"/>. El primer inicio de sesión de una
/// cuenta desde un fichero de <see cref="FicherosQueReutilizan"/> pasa por la
/// interfaz y deja guardada su sesión; los siguientes la ponen en su contexto de
/// navegador y piden la misma página que pide el formulario de acceso al
/// terminar, sin volver a teclear credenciales ni segundo factor (medido en CI:
/// 84 inicios de sesión por pasada de AppCollection, 94 s, el 32 % del tiempo de
/// tests de la colección).
///
/// <para>
/// <b>Quién reutiliza</b>: lo decide el fichero que llama, no el test. La lista
/// es de inclusión —un fichero nuevo o desconocido inicia sesión por la interfaz
/// hasta que alguien lo añada— porque para una clase de autorización, sesión,
/// segundo factor, activación de cuenta, multi-Tenant, sesión privilegiada de
/// soporte, auditoría, idioma o tema la sesión reutilizada cambiaría lo que se
/// prueba. Esas clases no leen ni escriben aquí: siguen exactamente como antes.
/// </para>
///
/// <para>
/// <b>Qué se guarda</b>: solo la cookie de sesión de Identity y la de cultura, las
/// dos que escribe el propio inicio de sesión (<c>Login.razor</c>,
/// <c>LoginCon2fa.razor</c>). Nada más. El Tenant beneficiario activo
/// (<c>cae_cliente_activo</c>, <c>cae_defecto_evaluado</c>) lo fija
/// <c>TenantBeneficiarioPorDefectoMiddleware</c> en la primera página que se pide
/// con sesión, y el tema lo escribe <c>tema.js</c> desde la cuenta: los dos se
/// vuelven a derivar contra la base en cada reutilización, igual que tras un
/// inicio de sesión de verdad, y por eso no pueden venir de una caché.
/// </para>
///
/// <para>
/// <b>Qué se pide después</b>: la URL a la que redirige el formulario de acceso
/// (<c>RedireccionLocal.DestinoTrasLogin</c>, hoy <c>/?desde=login</c>), leída de
/// la navegación real del primer inicio de sesión y no copiada aquí: es
/// <c>Inicio.razor</c> quien decide el aterrizaje a partir de ella.
/// </para>
///
/// <para>
/// <b>Por qué una sesión guardada vale lo que una recién iniciada</b>: solo se
/// reutiliza cuando ya venció el intervalo de revalidación de la aplicación
/// (<c>Sesion:IntervaloRevalidacionSegundos</c>, 60 s). Con la cookie vencida,
/// <c>SecurityStampValidator</c> comprueba en esa primera petición el sello de
/// seguridad y la cuenta contra la base, reconstruye el principal con
/// <c>TenantClaimsPrincipalFactory</c> —la misma fábrica del inicio de sesión— y
/// emite una cookie nueva. Esa cookie nueva es la prueba, y aquí se exige: si la
/// de sesión no cambió tras la petición, la aplicación no revalidó y se va por la
/// interfaz. Así una cuenta desactivada, una contraseña cambiada o un rol
/// retirado entre dos tests se notan igual por las dos vías. Lo único que la vía
/// rápida no vuelve a ejercer es la comprobación de la contraseña y del segundo
/// factor, que es justo el ahorro.
/// </para>
///
/// <para>
/// <b>Barrera de circuito</b>: la vía rápida termina cuando <c>.nav-principal</c>
/// es visible, la misma condición final de la vía de la interfaz, pero no espera
/// <c>NetworkIdle</c> en la página de aterrizaje. Un fichero solo entra en la
/// lista si cada llamada navega a continuación con
/// <see cref="Ayudas.NavegarYEsperarAsync"/> (que trae su propia barrera) o no usa
/// la página; el que interactúa con la página de aterrizaje tal cual queda (como
/// <c>CabeceraTests</c>) se queda fuera.
/// </para>
/// </summary>
internal static class SesionReutilizada
{
    /// <summary>
    /// Ficheros, relativos a la carpeta de este proyecto, cuyas llamadas
    /// reutilizan sesión. <c>null</c>: todas las del fichero. Una lista: solo las
    /// hechas desde esos miembros, para los ficheros que mezclan tests de
    /// pantalla con tests de autorización por rol —un test nuevo en ellos va por
    /// la interfaz hasta que se nombre aquí—.
    /// </summary>
    private static readonly Dictionary<string, string[]?> FicherosQueReutilizan = new(StringComparer.Ordinal)
    {
        ["AltaGuiadaTests.cs"] = null,
        ["BucleCorreccionPlataformaTests.cs"] = null,
        ["CentrosCabeceraAlineadaE2ETests.cs"] = null,
        ["CentrosFilaSinMenuE2ETests.cs"] = null,
        ["CentrosGestionarEnVivoE2ETests.cs"] = null,
        ["ClientesFilaSinMenuE2ETests.cs"] = null,
        ["ClientesListaLeeEmpresasE2ETests.cs"] = null,
        ["DeepLinksTests.cs"] = null,
        ["DocumentosFilaSinMenuE2ETests.cs"] = null,
        ["DocumentosTablaDesplazableE2ETests.cs"] = null,
        ["EmpresasCabeceraAlineadaE2ETests.cs"] = null,
        ["ExportarEstaVistaE2ETests.cs"] =
        [
            "Exportar_esta_vista_respeta_la_busqueda_y_exportar_todo_no",
            "Exportar_esta_vista_devuelve_el_subconjunto_que_contiene_lo_buscado",
        ],
        ["F3bSubcontrataSmokeTests.cs"] = null,
        ["FilaSeRefrescaTrasEditarEnPanelE2ETests.cs"] = null,
        ["FiltrosEnLaUrlTests.cs"] = null,
        ["FlujoBandejaPriorizadaTests.cs"] = null,
        ["FlujoCicloDocumentalTests.cs"] = null,
        ["FlujoCriticoTests.cs"] = null,
        ["GestionesFase1TecladoTests.cs"] = null,
        ["GestionesFilaSinMenuE2ETests.cs"] = null,
        ["ImportacionTests.cs"] = null,
        ["ImportarClientesTests.cs"] = null,
        ["ImportarCombinadoTests.cs"] = null,
        ["ImportarDocumentosTests.cs"] = null,
        ["KeyTipsEnListadoTests.cs"] = null,
        ["ListadosFase1VehiculosDocumentosTests.cs"] = null,
        ["P331TecladoLoteFiltrosGuardadosTests.cs"] = null,
        ["PestanaUrlDurableTests.cs"] = null,
        ["ProyectosFase1SelectorTests.cs"] = null,
        ["ProyectosFilaSinMenuE2ETests.cs"] = null,
        ["RegresionesTests.cs"] = null,
        ["SubidaMasivaTests.cs"] =
        [
            "Subida_multiple_deja_el_archivo_pendiente_de_confirmar_y_crea_el_Documento_al_confirmar",
            "Un_archivo_de_mas_de_10_MB_se_avisa_y_el_valido_del_mismo_lote_sigue_adelante",
        ],
        ["TrabajadoresFilaSinMenuE2ETests.cs"] = null,
        ["VehiculosFilaSinMenuE2ETests.cs"] = null,
        ["VisitaCentroGestionadoPorCorreoE2ETests.cs"] = null,
        ["VisitasFilaSinMenuE2ETests.cs"] = null,
    };

    /// <summary>
    /// Nombre por defecto de la cookie de sesión de Identity (la aplicación no lo
    /// cambia). Por prefijo: una cookie grande se parte en trozos
    /// <c>…ApplicationC1</c>, <c>…ApplicationC2</c>. Si el nombre cambiara, no se
    /// guardaría ninguna sesión y todo iría por la interfaz: lento, nunca falso.
    /// </summary>
    private const string PrefijoCookieDeSesion = ".AspNetCore.Identity.Application";

    /// <summary>Proyección de <c>ApplicationUser.Idioma</c> que reescribe cada inicio de sesión (<c>CulturaUsuarioCookie</c>).</summary>
    private const string CookieDeCultura = ".AspNetCore.Culture";

    /// <summary>
    /// Por encima de los 60 s por defecto de <c>Sesion:IntervaloRevalidacionSegundos</c>
    /// (<c>Program.cs</c>). No es la garantía, solo evita intentos perdidos: la
    /// garantía es que la cookie de sesión cambie (<see cref="IntentarAsync"/>).
    /// Mientras una sesión guardada es más joven que esto, la cuenta sigue
    /// iniciando sesión por la interfaz.
    /// </summary>
    private static readonly TimeSpan EdadMinima = TimeSpan.FromSeconds(65);

    /// <summary>
    /// Una tabla por navegador: cada fixture lanza el suyo, así que una sesión
    /// nunca cruza a otra colección ni a otra base aunque el sistema operativo
    /// repita el puerto de <c>BaseUrl</c>, y se suelta sola cuando la fixture
    /// cierra el navegador. Dentro, por (BaseUrl, correo). Concurrente: hoy no
    /// hay paralelismo (<c>ConfiguracionXunit.cs</c>), pero no depende de ello.
    /// </summary>
    private static readonly ConditionalWeakTable<IBrowser, ConcurrentDictionary<(string BaseUrl, string Email), Sesion>> SesionesPorNavegador = new();

    private static readonly Lock CandadoRegistro = new();

    private static readonly string CarpetaDelProyecto = CarpetaDe(RutaDeEsteFichero());

    private sealed record Sesion(string Contrasena, IReadOnlyList<Cookie> Cookies, string HuellaDeSesion, string DestinoTrasLogin, long CapturadaEn);

    /// <summary>
    /// ¿Reutiliza sesión una llamada hecha desde <paramref name="ficheroLlamante"/>
    /// (<c>[CallerFilePath]</c>) y <paramref name="miembroLlamante"/>
    /// (<c>[CallerMemberName]</c>)?
    /// </summary>
    public static bool Aplica(string ficheroLlamante, string miembroLlamante)
    {
        if (!ficheroLlamante.StartsWith(CarpetaDelProyecto, StringComparison.Ordinal))
            return false;

        var relativo = ficheroLlamante[CarpetaDelProyecto.Length..].Replace('\\', '/');
        return FicherosQueReutilizan.TryGetValue(relativo, out var miembros)
               && (miembros is null || miembros.Contains(miembroLlamante, StringComparer.Ordinal));
    }

    /// <summary>
    /// Pone en el contexto de <paramref name="page"/> la sesión guardada de la
    /// cuenta y pide la página de después del acceso. Si no hay sesión que sirva
    /// —ninguna guardada, otra contraseña, demasiado joven, la aplicación la
    /// rechazó o no la revalidó— el contexto vuelve a las cookies que tenía y
    /// quien llama inicia sesión por la interfaz. <c>Via</c> dice cuál fue el caso.
    /// </summary>
    public static async Task<(bool Reutilizada, string Via)> IntentarAsync(IPage page, string baseUrl, string email, string password)
    {
        if (page.Context.Browser is not { } navegador
            || !SesionesPorNavegador.TryGetValue(navegador, out var sesiones)
            || !sesiones.TryGetValue((baseUrl, email), out var sesion))
            return (false, "interfaz-primera");

        // La contraseña es parte de la clave de hecho: una llamada con otra (un
        // test de credenciales erróneas o cambiadas) no se sirve de la caché.
        if (!string.Equals(sesion.Contrasena, password, StringComparison.Ordinal))
            return (false, "interfaz-otra-contrasena");

        if (Stopwatch.GetElapsedTime(sesion.CapturadaEn) < EdadMinima)
            return (false, "interfaz-sesion-joven");

        var contexto = page.Context;
        var cookiesPrevias = (await contexto.CookiesAsync()).Select(ACookie).ToList();

        await contexto.AddCookiesAsync(sesion.Cookies);
        await page.GotoAsync(sesion.DestinoTrasLogin);

        // Sello rotado, cuenta desactivada, contraseña por cambiar, segundo factor
        // pendiente: todo acaba en una página de /cuenta. La sesión ya no vale.
        if (EsRutaDeCuenta(page.Url))
        {
            sesiones.TryRemove(KeyValuePair.Create((baseUrl, email), sesion));
            await RestaurarCookiesAsync(contexto, cookiesPrevias);
            return (false, "interfaz-sesion-rechazada");
        }

        // La aplicación sirvió la página sin revalidar la sesión contra la base
        // (su intervalo es mayor que EdadMinima): no hay prueba de que valga lo
        // que una recién iniciada. Se conserva: seguirá envejeciendo.
        if (HuellaDeSesion(await contexto.CookiesAsync([baseUrl])) == sesion.HuellaDeSesion)
        {
            await RestaurarCookiesAsync(contexto, cookiesPrevias);
            return (false, "interfaz-sin-revalidar");
        }

        await Ayudas.EsperarNavegacionPrincipalAsync(page);
        return (true, "reutilizada");
    }

    /// <summary>
    /// Ejecuta <paramref name="iniciarPorInterfaz"/> y, si la cuenta no tiene ya
    /// una sesión guardada con esa contraseña, guarda la que deja: las cookies
    /// del instante en que termina el inicio de sesión, antes de que el test haga
    /// nada, y la URL a la que redirigió el formulario. No se actualiza después:
    /// una sesión guardada solo se sustituye cuando la aplicación la rechaza o
    /// cuando la contraseña con la que se entra es otra.
    /// </summary>
    public static async Task CapturarAsync(IPage page, string baseUrl, string email, string password, Func<Task> iniciarPorInterfaz)
    {
        if (page.Context.Browser is not { } navegador)
        {
            await iniciarPorInterfaz();
            return;
        }

        var sesiones = SesionesPorNavegador.GetOrCreateValue(navegador);
        if (sesiones.TryGetValue((baseUrl, email), out var guardada)
            && string.Equals(guardada.Contrasena, password, StringComparison.Ordinal))
        {
            await iniciarPorInterfaz();
            return;
        }

        string? destinoTrasLogin = null;

        // La primera navegación del marco principal fuera de /cuenta es la
        // redirección del formulario de acceso (o del de segundo factor).
        void AlPedir(object? _, IRequest peticion)
        {
            if (peticion.IsNavigationRequest && peticion.Method == "GET"
                && EsDelMarcoPrincipal(peticion, page) && !EsRutaDeCuenta(peticion.Url))
                Interlocked.CompareExchange(ref destinoTrasLogin, peticion.Url, null);
        }

        page.Request += AlPedir;
        try
        {
            await iniciarPorInterfaz();
        }
        finally
        {
            page.Request -= AlPedir;
        }

        var cookies = (await page.Context.CookiesAsync([baseUrl])).Where(c => SeGuarda(c.Name)).ToList();
        var destino = Volatile.Read(ref destinoTrasLogin);

        // Sin cookie de sesión reconocible o sin destino observado no hay nada
        // fiable que reutilizar: la cuenta seguirá por la interfaz.
        if (destino is null || !destino.StartsWith(baseUrl, StringComparison.Ordinal)
            || !cookies.Exists(c => c.Name.StartsWith(PrefijoCookieDeSesion, StringComparison.Ordinal)))
            return;

        sesiones[(baseUrl, email)] = new Sesion(
            password, cookies.Select(ACookie).ToList(), HuellaDeSesion(cookies), destino, Stopwatch.GetTimestamp());
    }

    /// <summary>
    /// Control positivo y medición: con la variable de entorno
    /// <c>E2E_REGISTRO_SESIONES</c> apuntando a un fichero, cada inicio de sesión
    /// añade una línea «instante, vía, milisegundos, BaseUrl, cuenta, llamante».
    /// xUnit 2 no muestra la salida estándar de los tests, así que el recuento
    /// por vía se saca de ese fichero. Sin la variable no se escribe nada.
    /// </summary>
    public static void Registrar(string via, TimeSpan duracion, string baseUrl, string email, string ficheroLlamante, string miembroLlamante)
    {
        if (Environment.GetEnvironmentVariable("E2E_REGISTRO_SESIONES") is not { Length: > 0 } ruta)
            return;

        var linea = string.Join('\t',
            DateTime.UtcNow.ToString("O"), via, ((long)duracion.TotalMilliseconds).ToString(), baseUrl, email,
            $"{Path.GetFileName(ficheroLlamante.Replace('\\', '/'))}::{miembroLlamante}");

        lock (CandadoRegistro)
            File.AppendAllText(ruta, linea + Environment.NewLine);
    }

    private static string RutaDeEsteFichero([CallerFilePath] string ruta = "") => ruta;

    /// <summary>Con su separador final, para que <c>…/CaeManager.E2ETests</c> no case con <c>…/CaeManager.E2ETestsOtro</c>.</summary>
    private static string CarpetaDe(string rutaDeFichero) =>
        rutaDeFichero[..(rutaDeFichero.LastIndexOfAny(['/', '\\']) + 1)];

    private static bool SeGuarda(string nombreCookie) =>
        nombreCookie.StartsWith(PrefijoCookieDeSesion, StringComparison.Ordinal)
        || string.Equals(nombreCookie, CookieDeCultura, StringComparison.Ordinal);

    private static string HuellaDeSesion(IEnumerable<BrowserContextCookiesResult> cookies) =>
        string.Join('\n', cookies
            .Where(c => c.Name.StartsWith(PrefijoCookieDeSesion, StringComparison.Ordinal))
            .OrderBy(c => c.Name, StringComparer.Ordinal)
            .Select(c => $"{c.Name}={c.Value}"));

    private static bool EsRutaDeCuenta(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.AbsolutePath.StartsWith("/cuenta/", StringComparison.OrdinalIgnoreCase);

    private static bool EsDelMarcoPrincipal(IRequest peticion, IPage page)
    {
        try
        {
            return peticion.Frame == page.MainFrame;
        }
        catch (PlaywrightException)
        {
            // Una petición sin marco asociado no es la navegación que se busca.
            return false;
        }
    }

    private static async Task RestaurarCookiesAsync(IBrowserContext contexto, List<Cookie> cookiesPrevias)
    {
        await contexto.ClearCookiesAsync();
        if (cookiesPrevias.Count > 0)
            await contexto.AddCookiesAsync(cookiesPrevias);
    }

    private static Cookie ACookie(BrowserContextCookiesResult cookie) => new()
    {
        Name = cookie.Name,
        Value = cookie.Value,
        Domain = cookie.Domain,
        Path = cookie.Path,
        // -1 es «cookie de sesión de navegador»: se deja sin caducidad.
        Expires = cookie.Expires < 0 ? null : cookie.Expires,
        HttpOnly = cookie.HttpOnly,
        Secure = cookie.Secure,
        SameSite = cookie.SameSite,
    };
}
