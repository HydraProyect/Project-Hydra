using System.Text.RegularExpressions;
using CaeManager.Infrastructure.Auditing;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// Cierra el hallazgo de CIERRE-TURNO-NOCTURNO-2026-09-18.md § 12 por el lado
/// del <c>ChangeTracker</c>: <c>AuditoriaInterceptor</c> audita
/// <c>ApplicationUser</c> (alta, edición, baja, activación),
/// <c>IdentityUserRole&lt;Guid&gt;</c> (conceder/revocar un rol — el caso más
/// grave, "quién hizo Administrador a quién"), y desde la misión N6/V4
/// también <c>IdentityUserLogin&lt;Guid&gt;</c> (vincular un login externo) e
/// <c>IdentityUserToken&lt;Guid&gt;</c> (el secreto del segundo factor).
/// Cualquier escritor que pase por <c>UserManager</c>/<c>RoleManager</c> queda
/// cubierto automáticamente (comparten el mismo
/// <c>CaeManagerDbContext.SaveChanges</c>, ver
/// <c>InfrastructureServiceCollectionExtensions.AddEntityFrameworkStores</c>)
/// SIN necesidad de inventariar cada llamada — esa es la ventaja de auditar
/// por interceptor y no por comando.
///
/// <para>
/// Lo que ese mecanismo NO alcanza son las tablas de Identity que siguen
/// fuera de <see cref="AuditoriaInterceptor.TiposDeIdentidadAuditados"/>
/// (hoy AspNetUserClaims y AspNetRoleClaims): una escritura sobre cualquiera
/// de ellas sigue siendo invisible, exactamente el mismo agujero por omisión
/// que tenían <c>ApplicationUser</c>, <c>AspNetUserLogins</c> y
/// <c>AspNetUserTokens</c> antes de auditarlas. Este test inventaria esas
/// llamadas (por texto, mismo motivo y mismo patrón que
/// <see cref="ProhibicionSqlCrudoYFiltrosIgnoradosTests"/>: son llamadas a
/// método, no dependencias de tipo) para que una NUEVA no entre en silencio.
/// </para>
///
/// <para>
/// <b>Qué decide si una llamada necesita lista blanca</b>: no una lista
/// escrita a mano de "lo que falta", sino la tabla que esa llamada escribe
/// (<see cref="EscriturasDeIdentityPorTabla"/>) cruzada con la cobertura
/// REAL del interceptor, leída de su propio diccionario
/// (<see cref="EstaAuditada"/>). Por eso auditar una tabla nueva no consiste
/// en tachar entradas de aquí a mano, y —lo que importa— quitar un tipo del
/// interceptor pone esto en rojo por dos caminos a la vez: el conjunto
/// declarado deja de casar (<see cref="Las_tablas_auditadas_son_exactamente_las_que_cubre_el_interceptor"/>)
/// y sus llamadas vuelven a exigir lista blanca que no tienen.
/// </para>
///
/// <para>
/// <b>Límite estructural declarado</b> (hallazgo M1 de sesión coordinadora,
/// revisión de <c>8982cdc8</c>): este ratchet vigila por NOMBRE DE MÉTODO
/// conocido, no exhaustivamente cualquier escritura sobre las tablas sin
/// auditar. Una escritura directa por <c>DbSet&lt;IdentityUserClaim{Guid}&gt;</c>
/// (sin pasar por ninguno de los métodos de alto nivel de
/// <c>UserManager</c>/<c>RoleManager</c> listados), o un método nuevo que la
/// próxima versión de ASP.NET Core añada, seguiría siendo invisible para este
/// test hasta que se nombre aquí. No es una propiedad más débil de lo que el
/// resto de esta suite promete —<see cref="ProhibicionSqlCrudoYFiltrosIgnoradosTests"/>
/// tiene la misma limitación por el mismo motivo (grep de texto, no análisis
/// semántico)— pero conviene decirlo explícitamente en vez de dejar que el
/// nombre del test sugiera más de lo que mide.
/// </para>
/// </summary>
public class EscritorasDeIdentitySinAuditarTests
{
    /// <summary>
    /// Las tablas de Identity que <b>no</b> son <c>AspNetUsers</c> ni
    /// <c>AspNetUserRoles</c> (esas dos las cubre el interceptor por tipo
    /// desde #704), con el tipo de entidad de EF que las mapea — la clave
    /// para preguntarle al interceptor si hoy las audita.
    /// </summary>
    private enum TablaDeIdentity
    {
        UserClaims,
        UserLogins,
        UserTokens,
        RoleClaims,
    }

    private static readonly Dictionary<TablaDeIdentity, Type> TipoEfPorTabla = new()
    {
        [TablaDeIdentity.UserClaims] = typeof(IdentityUserClaim<Guid>),
        [TablaDeIdentity.UserLogins] = typeof(IdentityUserLogin<Guid>),
        [TablaDeIdentity.UserTokens] = typeof(IdentityUserToken<Guid>),
        [TablaDeIdentity.RoleClaims] = typeof(IdentityRoleClaim<Guid>),
    };

    /// <summary>
    /// Qué tabla escribe cada método de alto nivel de
    /// <c>UserManager</c>/<c>RoleManager</c>/<c>IUserAuthenticatorKeyStore</c>.
    /// </summary>
    private static readonly Dictionary<string, TablaDeIdentity> EscriturasDeIdentityPorTabla = new(StringComparer.Ordinal)
    {
        ["AddClaimAsync"] = TablaDeIdentity.UserClaims,
        ["AddClaimsAsync"] = TablaDeIdentity.UserClaims,
        ["RemoveClaimAsync"] = TablaDeIdentity.UserClaims,
        ["RemoveClaimsAsync"] = TablaDeIdentity.UserClaims,
        ["ReplaceClaimAsync"] = TablaDeIdentity.UserClaims,
        ["AddLoginAsync"] = TablaDeIdentity.UserLogins,
        ["RemoveLoginAsync"] = TablaDeIdentity.UserLogins,
        ["SetAuthenticationTokenAsync"] = TablaDeIdentity.UserTokens,
        ["RemoveAuthenticationTokenAsync"] = TablaDeIdentity.UserTokens,
        ["RedeemTwoFactorRecoveryCodeAsync"] = TablaDeIdentity.UserTokens,
        ["GenerateNewTwoFactorRecoveryCodesAsync"] = TablaDeIdentity.UserTokens,
        ["ResetAuthenticatorKeyAsync"] = TablaDeIdentity.UserTokens,
        ["SetAuthenticatorKeyAsync"] = TablaDeIdentity.UserTokens,
        ["AddClaimToRoleAsync"] = TablaDeIdentity.RoleClaims,
        ["RemoveClaimFromRoleAsync"] = TablaDeIdentity.RoleClaims,
    };

    private static readonly Regex EscrituraDeIdentity = new(
        @"\.(" + string.Join("|", EscriturasDeIdentityPorTabla.Keys) + @")\s*\(",
        RegexOptions.Compiled);

    /// <summary>
    /// <b>Qué tabla se da hoy por auditada</b>, congelado aquí y contrastado
    /// contra el interceptor real en
    /// <see cref="Las_tablas_auditadas_son_exactamente_las_que_cubre_el_interceptor"/>.
    /// Escribirlo dos veces es deliberado: si estuviera solo leído del
    /// interceptor, quitar un tipo de allí no rompería nada — el ratchet
    /// simplemente empezaría a exigir menos, en silencio.
    /// </summary>
    private static readonly IReadOnlySet<TablaDeIdentity> TablasAuditadasCongeladas =
        new HashSet<TablaDeIdentity> { TablaDeIdentity.UserLogins, TablaDeIdentity.UserTokens };

    private static bool EstaAuditada(TablaDeIdentity tabla) =>
        AuditoriaInterceptor.TiposDeIdentidadAuditados.ContainsKey(TipoEfPorTabla[tabla]);

    /// <summary>
    /// <b>Control positivo</b> del escaneo: (ruta relativa, línea exacta
    /// recortada) → apariciones observadas, congelado desde el escaneo real a
    /// 2026-09-19. Si una línea congelada deja de observarse, el instrumento
    /// dejó de mirar donde cree que mira y
    /// <see cref="Cada_uso_congelado_sigue_observandose_donde_dice_la_lista"/>
    /// lo dice.
    ///
    /// <para>
    /// No es una lista blanca — esa es <see cref="GapsConocidas"/>, y hoy está
    /// vacía. Separarlas importa: si una sola lista hiciera los dos papeles,
    /// quitar una tabla de la cobertura del interceptor dejaría sus llamadas
    /// tapadas por su propia entrada de control positivo, y la única alarma
    /// sería <see cref="Las_tablas_auditadas_son_exactamente_las_que_cubre_el_interceptor"/>.
    /// Así saltan las dos.
    /// </para>
    /// </summary>
    private static readonly Dictionary<(string Ruta, string Linea), int> UsosObservadosCongelados = new()
    {
        // Vincula un login externo (Microsoft 365 / SSO) a una cuenta
        // auto-provisionada — escribe AspNetUserLogins. AUDITADA desde la
        // misión N6/V4: deja fila "LoginExterno / Creado" contra el Tenant
        // propietario de la cuenta (ver AuditoriaDeIdentidadRestanteTests).
        [("src/CaeManager.Web/Components/Account/IdentityEndpointsExtensions.cs",
            "await userManager.AddLoginAsync(usuario, infoExterna);")] = 1,

        // Hallazgo M1 de sesión coordinadora (revisión de `8982cdc8`): el
        // secreto TOTP (AspNetUserTokens). AUDITADA desde la misión N6/V4 —
        // deja fila "TokenDeUsuario" con el Value enmascarado, que es la
        // única forma de registrar un cambio de segundo factor sin copiar el
        // secreto al historial.
        [("src/CaeManager.Web/Components/Account/Pages/ConfigurarAutenticadorDosFactores.razor",
            "var resultadoReset = await UserManager.ResetAuthenticatorKeyAsync(usuario);")] = 1,

        // Los seis usos de SetAuthenticatorKeyAsync de abajo son SIEMBRA
        // (IdentitySeeder, DatosPruebaSeeder, DelegacionDemoSeeder ×4,
        // SegundoTenantSeeder): fijan la clave TOTP de cuentas demo/de
        // prueba al arrancar, nunca en respuesta a una acción de un usuario
        // real. Ahora también dejan fila, como cualquier otra escritura de
        // esa tabla; su AmbitoTenantExplicito ya lo establece cada seeder
        // (ver DelegacionDemoSeeder tras #704).
        [("src/CaeManager.Infrastructure/Identity/IdentitySeeder.cs",
            "await claveStore.SetAuthenticatorKeyAsync(administrador, ClaveTotpAdministradorInicial, CancellationToken.None);")] = 1,
        [("src/CaeManager.Infrastructure/Persistence/Seed/DatosPruebaSeeder.cs",
            "await claveStore.SetAuthenticatorKeyAsync(")] = 1,
        [("src/CaeManager.Infrastructure/Persistence/Seed/DelegacionDemoSeeder.cs",
            "await claveStore.SetAuthenticatorKeyAsync(")] = 3,
        [("src/CaeManager.Infrastructure/Persistence/Seed/DelegacionDemoSeeder.cs",
            "await claveStore.SetAuthenticatorKeyAsync(administrador, IdentitySeeder.ClaveTotpAdministradorInicial, cancellationToken);")] = 1,
        [("src/CaeManager.Infrastructure/Persistence/Seed/SegundoTenantSeeder.cs",
            "await claveStore.SetAuthenticatorKeyAsync(")] = 1,
    };

    /// <summary>
    /// <b>Lista blanca</b>: escrituras sobre una tabla que el interceptor NO
    /// audita y que se aceptan igualmente, con su motivo. Cada entrada es una
    /// GAP CONOCIDA, no una autorización permanente: quien la cierre (auditando
    /// la tabla correspondiente en AuditoriaInterceptor, con el enmascarado que
    /// le corresponda) la quita de aquí en el mismo commit.
    ///
    /// <para>
    /// <b>Hoy está vacía</b>, y eso es el estado bueno: las dos GAPs que #704
    /// registró (AddLoginAsync sobre AspNetUserLogins, ResetAuthenticatorKeyAsync
    /// sobre AspNetUserTokens) se cerraron auditando ambas tablas en la misión
    /// N6/V4. Quedan sin auditar AspNetUserClaims y AspNetRoleClaims, pero
    /// <b>nadie escribe en ellas hoy</b> (medido: cero usos de AddClaimAsync y
    /// familia en src/), así que el test de abajo pasa sin nada que perdonar —
    /// no por vacuidad del escaneo, que el control positivo de arriba cubre con
    /// nueve líneas reales.
    /// </para>
    /// </summary>
    private static readonly Dictionary<(string Ruta, string Linea), int> GapsConocidas = new();

    private static readonly string[] CarpetasEscaneadas =
    [
        "src/CaeManager.Application",
        "src/CaeManager.Infrastructure",
        "src/CaeManager.Web",
    ];

    [Fact]
    public void Ninguna_escritura_de_una_tabla_sin_auditar_queda_fuera_de_la_lista_blanca()
    {
        var infractores = EscanearUsos()
            .Where(kv => !EstaAuditada(kv.Key.Tabla))
            .Where(kv => kv.Value > GapsConocidas.GetValueOrDefault((kv.Key.Ruta, kv.Key.Linea)))
            .Select(kv => $"{kv.Key.Ruta} [{kv.Key.Tabla}]: \"{kv.Key.Linea}\" ({kv.Value} apariciones, " +
                          $"{GapsConocidas.GetValueOrDefault((kv.Key.Ruta, kv.Key.Linea))} perdonadas)")
            .OrderBy(x => x)
            .ToList();

        string.Join("\n", infractores).Should().BeEmpty(
            "una escritura sobre una tabla de Identity que AuditoriaInterceptor no reconoce (ver " +
            "TiposDeIdentidadAuditados) no deja ningún rastro — es una decisión consciente: o se extiende el " +
            "interceptor para cubrirla (con su propio enmascarado si hace falta), o se añade a GapsConocidas " +
            "explicando por qué queda fuera");
    }

    /// <summary>Misma guarda del ratchet que <see cref="ProhibicionSqlCrudoYFiltrosIgnoradosTests"/>.</summary>
    [Fact]
    public void Cada_uso_congelado_sigue_observandose_donde_dice_la_lista()
    {
        var observado = EscanearUsos()
            .GroupBy(kv => (kv.Key.Ruta, kv.Key.Linea))
            .ToDictionary(g => g.Key, g => g.Sum(kv => kv.Value));

        var discrepancias = UsosObservadosCongelados
            .Where(kv => observado.GetValueOrDefault(kv.Key) != kv.Value)
            .Select(kv => $"{kv.Key.Ruta}: \"{kv.Key.Linea}\" (lista: {kv.Value}, observadas: {observado.GetValueOrDefault(kv.Key)})")
            .OrderBy(x => x)
            .ToList();

        string.Join(Environment.NewLine, discrepancias).Should().BeEmpty(
            "si una línea congelada ya no se observa, el escaneo dejó de mirar donde cree que mira —o el uso " +
            "desapareció sin actualizar la lista—; en ambos casos el ratchet estaría dando verde sobre un " +
            "conjunto más pequeño del que cree");
    }

    /// <summary>
    /// El cierre del hueco M1 por el lado que importa: que las llamadas estén
    /// inventariadas no prueba que se auditen. Esto compara la cobertura
    /// declarada aquí contra la que <see cref="AuditoriaInterceptor"/> tiene
    /// de verdad, así que quitar <c>IdentityUserToken&lt;Guid&gt;</c> o
    /// <c>IdentityUserLogin&lt;Guid&gt;</c> de su diccionario pone esto en
    /// rojo aunque el resto del ratchet siguiera contento.
    /// </summary>
    [Fact]
    public void Las_tablas_auditadas_son_exactamente_las_que_cubre_el_interceptor()
    {
        var auditadasSegunElInterceptor = TipoEfPorTabla.Keys.Where(EstaAuditada).ToList();

        auditadasSegunElInterceptor.Should().BeEquivalentTo(TablasAuditadasCongeladas,
            "AuditoriaInterceptor.TiposDeIdentidadAuditados es lo que decide si una escritura deja rastro; si " +
            "cambia, esta lista y la lista blanca de arriba cambian con él en el mismo commit — y si se ENCOGE, " +
            "es que una tabla dejó de auditarse");
    }

    private static Dictionary<(string Ruta, string Linea, TablaDeIdentity Tabla), int> EscanearUsos()
    {
        var raiz = RaizDelRepositorio();
        var apariciones = new Dictionary<(string Ruta, string Linea, TablaDeIdentity Tabla), int>();

        foreach (var carpeta in CarpetasEscaneadas)
        {
            var directorio = Path.Combine(raiz, carpeta.Replace('/', Path.DirectorySeparatorChar));

            var archivos = Directory
                .EnumerateFiles(directorio, "*", SearchOption.AllDirectories)
                .Where(a => a.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                            || a.EndsWith(".razor", StringComparison.OrdinalIgnoreCase))
                .Where(a => !a.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                            && !a.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));

            foreach (var archivo in archivos)
            {
                var rutaRelativa = Path.GetRelativePath(raiz, archivo).Replace(Path.DirectorySeparatorChar, '/');

                foreach (var lineaCruda in File.ReadLines(archivo))
                {
                    var linea = lineaCruda.Trim();
                    var coincidencia = EscrituraDeIdentity.Match(linea);
                    if (!coincidencia.Success) continue;

                    var entrada = (rutaRelativa, linea, EscriturasDeIdentityPorTabla[coincidencia.Groups[1].Value]);
                    apariciones[entrada] = apariciones.GetValueOrDefault(entrada) + 1;
                }
            }
        }

        return apariciones;
    }

    private static string RaizDelRepositorio()
    {
        var actual = new DirectoryInfo(AppContext.BaseDirectory);

        while (actual is not null && !File.Exists(Path.Combine(actual.FullName, "CaeManager.slnx")))
            actual = actual.Parent;

        if (actual is null)
            throw new InvalidOperationException(
                "No se encontró CaeManager.slnx subiendo desde " + AppContext.BaseDirectory);

        return actual.FullName;
    }
}
