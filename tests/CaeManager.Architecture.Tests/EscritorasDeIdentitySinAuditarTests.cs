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
/// </summary>
public class EscritorasDeIdentitySinAuditarTests
{
    private static readonly Regex EscrituraDeIdentityNoCubierta = new(
        @"\.(?:AddClaimAsync|AddClaimsAsync|RemoveClaimAsync|RemoveClaimsAsync|ReplaceClaimAsync|" +
        @"AddLoginAsync|RemoveLoginAsync|SetAuthenticationTokenAsync|RemoveAuthenticationTokenAsync|" +
        @"RedeemTwoFactorRecoveryCodeAsync|GenerateNewTwoFactorRecoveryCodesAsync|AddClaimToRoleAsync|" +
        @"RemoveClaimFromRoleAsync)\s*\(",
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
