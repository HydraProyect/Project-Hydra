using System.Text.RegularExpressions;
using FluentAssertions;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// Cierra el hallazgo de CIERRE-TURNO-NOCTURNO-2026-09-18.md § 12 por el lado
/// del <c>ChangeTracker</c>: <c>AuditoriaInterceptor</c> ahora audita
/// <c>ApplicationUser</c> (alta, edición, baja, activación) e
/// <c>IdentityUserRole&lt;Guid&gt;</c> (conceder/revocar un rol — el caso más
/// grave, "quién hizo Administrador a quién"). Cualquier escritor que pase
/// por <c>UserManager</c>/<c>RoleManager</c> queda cubierto automáticamente
/// (comparten el mismo <c>CaeManagerDbContext.SaveChanges</c>, ver
/// <c>InfrastructureServiceCollectionExtensions.AddEntityFrameworkStores</c>)
/// SIN necesidad de inventariar cada llamada — esa es la ventaja de auditar
/// por interceptor y no por comando.
///
/// <para>
/// Lo que ese mecanismo NO alcanza son las OTRAS tablas de Identity
/// (AspNetUserClaims, AspNetUserLogins, AspNetUserTokens, AspNetRoleClaims):
/// <c>AuditoriaInterceptor.ConstruirRegistros</c> solo reconoce
/// <c>ApplicationUser</c> e <c>IdentityUserRole&lt;Guid&gt;</c> por tipo — una
/// escritura sobre cualquier otra sigue siendo invisible, exactamente el
/// mismo agujero por omisión que tenía <c>ApplicationUser</c> antes de este
/// cambio. Este test inventaria esas llamadas (por texto, mismo motivo y
/// mismo patrón que <see cref="ProhibicionSqlCrudoYFiltrosIgnoradosTests"/>:
/// son llamadas a método, no dependencias de tipo) para que una NUEVA no
/// entre en silencio.
/// </para>
///
/// <para>
/// <b>Límite estructural declarado</b> (hallazgo M1 de sesión coordinadora,
/// revisión de <c>8982cdc8</c>): este ratchet vigila por NOMBRE DE MÉTODO
/// conocido (la lista de <see cref="EscrituraDeIdentityNoCubierta"/>), no
/// exhaustivamente cualquier escritura sobre las cuatro tablas sin auditar.
/// Una escritura directa por <c>DbSet&lt;IdentityUserToken{Guid}&gt;</c>/
/// <c>IdentityUserClaim{Guid}</c>/etc. (sin pasar por ninguno de los métodos
/// de alto nivel de <c>UserManager</c>/<c>RoleManager</c> listados), o un
/// método nuevo de Identity que la próxima versión de ASP.NET Core añada,
/// seguiría siendo invisible para este test hasta que se nombre aquí. No es
/// una propiedad más débil de lo que el resto de esta suite promete —
/// <see cref="ProhibicionSqlCrudoYFiltrosIgnoradosTests"/> tiene la misma
/// limitación por el mismo motivo (grep de texto, no análisis semántico) —
/// pero conviene decirlo explícitamente en vez de dejar que el nombre del
/// test ("Ninguna escritura nueva... queda fuera") sugiera más de lo que mide.
/// </para>
/// </summary>
public class EscritorasDeIdentitySinAuditarTests
{
    private static readonly Regex EscrituraDeIdentityNoCubierta = new(
        @"\.(?:AddClaimAsync|AddClaimsAsync|RemoveClaimAsync|RemoveClaimsAsync|ReplaceClaimAsync|" +
        @"AddLoginAsync|RemoveLoginAsync|SetAuthenticationTokenAsync|RemoveAuthenticationTokenAsync|" +
        @"RedeemTwoFactorRecoveryCodeAsync|GenerateNewTwoFactorRecoveryCodesAsync|AddClaimToRoleAsync|" +
        @"RemoveClaimFromRoleAsync|ResetAuthenticatorKeyAsync|SetAuthenticatorKeyAsync)\s*\(",
        RegexOptions.Compiled);

    /// <summary>
    /// (ruta relativa, línea exacta recortada) → apariciones permitidas.
    /// Congelado desde el escaneo real a 2026-09-18. Cada entrada es una
    /// GAP CONOCIDA, no una autorización permanente: quien la cierre (auditando
    /// la tabla correspondiente en AuditoriaInterceptor, con el enmascarado
    /// que le corresponda) la quita de aquí en el mismo commit.
    /// </summary>
    private static readonly Dictionary<(string Ruta, string Linea), int> UsosSinAuditarConocidos = new()
    {
        // Vincula un login externo (Microsoft 365 / SSO) a una cuenta
        // auto-provisionada — escribe AspNetUserLogins. Menos grave que un
        // cambio de rol (no concede autoridad nueva, solo un segundo camino
        // de entrada a una cuenta que ya existe), pero sigue siendo "qué
        // pasó con esta cuenta" sin rastro. Fuera de alcance de este
        // incremento (la misión pedía altas, roles, baja, activación,
        // credenciales) — registrado, no resuelto.
        [("src/CaeManager.Web/Components/Account/IdentityEndpointsExtensions.cs",
            "await userManager.AddLoginAsync(usuario, infoExterna);")] = 1,

        // Hallazgo M1 de sesión coordinadora (revisión de `8982cdc8`): el
        // secreto TOTP (AspNetUserTokens) tampoco lo audita
        // AuditoriaInterceptor. Activar/resetear el autenticador de dos
        // factores es una escritura de seguridad real —cambia qué código
        // acepta el sistema como segundo factor de esa cuenta— sin rastro de
        // quién lo hizo ni cuándo. Fuera de alcance de este incremento (la
        // misión pedía altas, roles, baja, activación, credenciales de
        // contraseña) — registrado, no resuelto, igual que AddLoginAsync de
        // arriba.
        [("src/CaeManager.Web/Components/Account/Pages/ConfigurarAutenticadorDosFactores.razor",
            "var resultadoReset = await UserManager.ResetAuthenticatorKeyAsync(usuario);")] = 1,

        // Los seis usos de SetAuthenticatorKeyAsync de abajo son SIEMBRA
        // (IdentitySeeder, DatosPruebaSeeder, DelegacionDemoSeeder ×4,
        // SegundoTenantSeeder): fijan la clave TOTP de cuentas demo/de
        // prueba al arrancar, nunca en respuesta a una acción de un usuario
        // real — mismo criterio que el resto de la siembra, que tampoco
        // pasa por auditoría (ver AmbitoTenantExplicito). No es el mismo
        // caso que el reset de arriba, pero comparte la misma GAP de fondo:
        // AspNetUserTokens sin auditar.
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

    private static readonly string[] CarpetasEscaneadas =
    [
        "src/CaeManager.Application",
        "src/CaeManager.Infrastructure",
        "src/CaeManager.Web",
    ];

    [Fact]
    public void Ninguna_escritura_nueva_de_identity_sin_auditar_queda_fuera_de_la_lista_blanca()
    {
        var infractores = EscanearUsos()
            .Where(kv => kv.Value > UsosSinAuditarConocidos.GetValueOrDefault(kv.Key))
            .Select(kv => $"{kv.Key.Ruta}: \"{kv.Key.Linea}\" ({kv.Value} apariciones, {UsosSinAuditarConocidos.GetValueOrDefault(kv.Key)} conocidas)")
            .OrderBy(x => x)
            .ToList();

        string.Join("\n", infractores).Should().BeEmpty(
            "una escritura nueva sobre AspNetUserClaims/AspNetUserLogins/AspNetUserTokens/AspNetRoleClaims no la ve " +
            "AuditoriaInterceptor (solo reconoce ApplicationUser e IdentityUserRole<Guid>, ver ConstruirRegistros) — " +
            "es una decisión consciente: o se extiende el interceptor para cubrirla (con su propio enmascarado si " +
            "hace falta), o se añade aquí a UsosSinAuditarConocidos explicando por qué queda fuera");
    }

    /// <summary>Misma guarda del ratchet que <see cref="ProhibicionSqlCrudoYFiltrosIgnoradosTests"/>.</summary>
    [Fact]
    public void Cada_uso_congelado_sigue_observandose_donde_dice_la_lista()
    {
        var observado = EscanearUsos();

        var discrepancias = UsosSinAuditarConocidos
            .Where(kv => observado.GetValueOrDefault(kv.Key) != kv.Value)
            .Select(kv => $"{kv.Key.Ruta}: \"{kv.Key.Linea}\" (lista: {kv.Value}, observadas: {observado.GetValueOrDefault(kv.Key)})")
            .OrderBy(x => x)
            .ToList();

        string.Join(Environment.NewLine, discrepancias).Should().BeEmpty(
            "si una línea congelada ya no se observa, el escaneo dejó de mirar donde cree que mira —o el uso se " +
            "cerró (auditado) sin actualizar la lista—; en ambos casos el ratchet estaría dando verde sobre un " +
            "conjunto más pequeño del que cree");
    }

    private static Dictionary<(string Ruta, string Linea), int> EscanearUsos()
    {
        var raiz = RaizDelRepositorio();
        var apariciones = new Dictionary<(string Ruta, string Linea), int>();

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
                    if (!EscrituraDeIdentityNoCubierta.IsMatch(linea)) continue;

                    var entrada = (rutaRelativa, linea);
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
