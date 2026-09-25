using System.Security.Cryptography;
using System.Text;
using CaeManager.Application.Common;
using CaeManager.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Identity;

/// <summary>
/// El almacén de usuarios de Identity con una sola diferencia: los códigos de
/// recuperación de la verificación en dos pasos se guardan con hash, no en claro
/// (P0-8 del plan de madurez 09-24, hallazgo FS-01).
///
/// <para>
/// <b>Por qué hace falta.</b> El <c>UserStoreBase</c> de ASP.NET Core Identity
/// guarda los códigos tal cual, unidos por <c>;</c>, en la fila
/// <c>[AspNetUserStore]/RecoveryCodes</c> de <c>AspNetUserTokens</c>. Un código de
/// recuperación sustituye a la aplicación autenticadora: quien lea esa fila
/// (una copia de seguridad, un volcado, una consulta con el rol de mantenimiento)
/// tendría el segundo factor de cualquier cuenta. La auditoría ya enmascara el
/// valor (<c>AuditoriaInterceptor.PropiedadesSensiblesPorTipo</c>), pero eso cubre
/// el historial, no la fila viva.
/// </para>
///
/// <para>
/// <b>Qué cambia.</b> Solo los tres métodos de <c>IUserTwoFactorRecoveryCodeStore</c>.
/// <c>UserManager.GenerateNewTwoFactorRecoveryCodesAsync</c> genera los códigos y
/// se los devuelve al llamante una sola vez; aquí llegan en claro y se guardan
/// como PBKDF2 con sal propia por código. Canjear uno lo compara con cada hash y
/// lo quita de la lista: sirve una sola vez. La fila sigue en la misma tabla y
/// con el mismo nombre, así que el enmascarado de la auditoría y el borrado al
/// restablecer (<c>RemoveAuthenticationTokenAsync</c>) siguen valiendo igual.
/// </para>
///
/// <para>
/// Una fila con el formato antiguo (en claro) no se acepta: no se contó ningún
/// código de recuperación antes de este almacén (FS-01 midió cero usos de
/// <c>GenerateNewTwoFactorRecoveryCodesAsync</c> en <c>src</c>), y aceptar el
/// formato en claro sería mantener viva justo la debilidad que esto cierra.
/// </para>
///
/// <para>
/// <b>Cuentas antes de que exista Tenant (P1-M1).</b> <c>AspNetUsers</c> tiene RLS
/// por Tenant (migración <c>RlsAspNetUsers</c>): sin Tenant en el contexto no se ve
/// ninguna fila. Dentro de <see cref="AmbitoIdentificacionSinTenant"/>, y solo
/// mientras <see cref="ITenantActual"/> no tenga Tenant, las búsquedas por clave
/// (Id, nombre, correo) resuelven primero el Tenant de la cuenta con una función
/// <c>SECURITY DEFINER</c> que devuelve ese dato y nada más, y leen la fila dentro de
/// <see cref="AmbitoTenantExplicito"/>: bajo la política de su propio Tenant, igual
/// que <c>ApiKeyAuthenticationHandler</c> con <c>app_tenant_de_clave_api</c>. Las
/// escrituras de esos caminos (el contador de intentos fallidos, el restablecimiento
/// de la contraseña, el alta por SSO) corren en el Tenant de la cuenta que se
/// escribe. Fuera de ese ámbito, o con Tenant, el almacén se comporta como el de
/// Identity y la política decide.
/// </para>
/// </summary>
public class AlmacenUsuarios(
    CaeManagerDbContext context, ITenantActual tenantActual, IdentityErrorDescriber? describer = null)
    : UserStore<ApplicationUser, IdentityRole<Guid>, CaeManagerDbContext, Guid>(context, describer)
{
    // Los mismos valores que las constantes privadas de UserStoreBase: la fila
    // es la misma que usaría Identity sin este almacén.
    internal const string ProveedorInterno = "[AspNetUserStore]";
    internal const string NombreTokenCodigosRecuperacion = "RecoveryCodes";

    private const string PrefijoFormato = "pbkdf2-sha256$";
    private const int Iteraciones = 100_000;
    private const int BytesSal = 16;
    private const int BytesHash = 32;

    // ── Cuentas antes de que exista Tenant (P1-M1) ─────────────────────────

    private bool ResuelveSinTenant => tenantActual.TenantId is null && AmbitoIdentificacionSinTenant.Abierto;

    public override async Task<ApplicationUser?> FindByIdAsync(string userId, CancellationToken cancellationToken = default)
    {
        if (!ResuelveSinTenant || !Guid.TryParse(userId, out var id))
            return await base.FindByIdAsync(userId, cancellationToken);

        return await EnTenantAsync(await ResolverTenantPorIdAsync(id, cancellationToken),
            () => base.FindByIdAsync(userId, cancellationToken));
    }

    protected override async Task<ApplicationUser?> FindUserAsync(Guid userId, CancellationToken cancellationToken)
    {
        if (!ResuelveSinTenant)
            return await base.FindUserAsync(userId, cancellationToken);

        return await EnTenantAsync(await ResolverTenantPorIdAsync(userId, cancellationToken),
            () => base.FindUserAsync(userId, cancellationToken));
    }

    public override async Task<ApplicationUser?> FindByNameAsync(
        string normalizedUserName, CancellationToken cancellationToken = default)
    {
        if (!ResuelveSinTenant)
            return await base.FindByNameAsync(normalizedUserName, cancellationToken);

        var cuentas = await CuentasPorNombreNormalizadoAsync(Context, normalizedUserName, cancellationToken);
        return await EnTenantAsync(TenantUnico(cuentas),
            () => base.FindByNameAsync(normalizedUserName, cancellationToken));
    }

    public override async Task<ApplicationUser?> FindByEmailAsync(
        string normalizedEmail, CancellationToken cancellationToken = default)
    {
        if (!ResuelveSinTenant)
            return await base.FindByEmailAsync(normalizedEmail, cancellationToken);

        var cuentas = await CuentasPorEmailNormalizadoAsync(Context, normalizedEmail, cancellationToken);
        return await EnTenantAsync(TenantUnico(cuentas),
            () => base.FindByEmailAsync(normalizedEmail, cancellationToken));
    }

    public override Task<IdentityResult> CreateAsync(ApplicationUser user, CancellationToken cancellationToken = default) =>
        EnTenantDeLaCuentaAsync(user, () => base.CreateAsync(user, cancellationToken));

    public override Task<IdentityResult> UpdateAsync(ApplicationUser user, CancellationToken cancellationToken = default) =>
        EnTenantDeLaCuentaAsync(user, () => base.UpdateAsync(user, cancellationToken));

    public override Task<IdentityResult> DeleteAsync(ApplicationUser user, CancellationToken cancellationToken = default) =>
        EnTenantDeLaCuentaAsync(user, () => base.DeleteAsync(user, cancellationToken));

    /// <summary>
    /// Una cuenta que un camino sin Tenant ya tiene delante (la cargó por su clave
    /// dentro del mismo ámbito, o la está dando de alta el SSO) se escribe en su
    /// propio Tenant. Con Tenant en el contexto, o fuera del ámbito, decide la
    /// política con el contexto que haya.
    /// </summary>
    private async Task<IdentityResult> EnTenantDeLaCuentaAsync(ApplicationUser user, Func<Task<IdentityResult>> escribir)
    {
        ArgumentNullException.ThrowIfNull(user);
        if (!ResuelveSinTenant || user.TenantId == Guid.Empty) return await escribir();

        using (AmbitoTenantExplicito.Establecer(user.TenantId))
            return await escribir();
    }

    private static async Task<ApplicationUser?> EnTenantAsync(Guid? tenantId, Func<Task<ApplicationUser?>> leer)
    {
        if (tenantId is not { } tenant) return null;

        using (AmbitoTenantExplicito.Establecer(tenant))
            return await leer();
    }

    /// <summary>
    /// El índice de <c>NormalizedEmail</c> no es único: dos cuentas con el mismo correo
    /// en Tenants distintos harían que la búsqueda de Identity fallara igual
    /// (<c>SingleOrDefault</c>). Se falla aquí, antes de elegir un Tenant al azar.
    /// </summary>
    private static Guid? TenantUnico(IReadOnlyList<CuentaResuelta> cuentas)
    {
        var tenants = cuentas.Select(c => c.TenantId).Distinct().ToList();
        return tenants.Count switch
        {
            0 => null,
            1 => tenants[0],
            _ => throw new InvalidOperationException(
                "Hay más de una cuenta con esa clave en Tenants distintos; la búsqueda sin Tenant no puede elegir."),
        };
    }

    private Task<Guid?> ResolverTenantPorIdAsync(Guid id, CancellationToken cancellationToken) =>
        TenantDeCuentaAsync(Context, id, cancellationToken);

    // Las tres funciones SECURITY DEFINER de RlsAspNetUsers. Estáticas e internas: las
    // usan también ValidadorUnicidadGlobalCuenta y DirectorioUsuariosTenant, y un test
    // de arquitectura acota quién más puede nombrarlas.

    internal static Task<Guid?> TenantDeCuentaAsync(CaeManagerDbContext db, Guid cuentaId, CancellationToken cancellationToken) =>
        db.Database.SqlQuery<Guid?>($"SELECT app_tenant_de_cuenta({cuentaId}) AS \"Value\"")
            .SingleAsync(cancellationToken);

    internal static async Task<IReadOnlyList<CuentaResuelta>> CuentasPorNombreNormalizadoAsync(
        CaeManagerDbContext db, string nombreNormalizado, CancellationToken cancellationToken) =>
        await db.Database.SqlQuery<CuentaResuelta>(
                $"SELECT cuenta_id AS \"CuentaId\", tenant_id AS \"TenantId\" FROM app_cuenta_por_nombre_normalizado({nombreNormalizado})")
            .ToListAsync(cancellationToken);

    internal static async Task<IReadOnlyList<CuentaResuelta>> CuentasPorEmailNormalizadoAsync(
        CaeManagerDbContext db, string emailNormalizado, CancellationToken cancellationToken) =>
        await db.Database.SqlQuery<CuentaResuelta>(
                $"SELECT cuenta_id AS \"CuentaId\", tenant_id AS \"TenantId\" FROM app_cuentas_por_email_normalizado({emailNormalizado})")
            .ToListAsync(cancellationToken);

    // ── Códigos de recuperación de la 2FA (P0-8) ───────────────────────────

    public override Task ReplaceCodesAsync(
        ApplicationUser user, IEnumerable<string> recoveryCodes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(recoveryCodes);

        var hashes = recoveryCodes.Select(CalcularHash);
        return GuardarHashesAsync(user, hashes, cancellationToken);
    }

    public override async Task<bool> RedeemCodeAsync(
        ApplicationUser user, string code, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(code);

        var hashes = await LeerHashesAsync(user, cancellationToken);
        var normalizado = Normalizar(code);
        if (normalizado.Length == 0) return false;

        var coincidente = hashes.FirstOrDefault(h => Coincide(h, normalizado));
        if (coincidente is null) return false;

        await GuardarHashesAsync(user, hashes.Where(h => !ReferenceEquals(h, coincidente)), cancellationToken);
        return true;
    }

    public override async Task<int> CountCodesAsync(ApplicationUser user, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        return (await LeerHashesAsync(user, cancellationToken)).Count;
    }

    /// <summary>
    /// Mayúsculas, sin espacios ni guiones: los códigos que genera Identity son
    /// <c>XXXXX-XXXXX</c> sobre un alfabeto sin ambigüedades, y quien los copia a
    /// mano o desde un fichero no debe fallar por el guion o por la caja.
    /// </summary>
    internal static string Normalizar(string codigo) =>
        new(codigo.Where(c => !char.IsWhiteSpace(c) && c != '-').Select(char.ToUpperInvariant).ToArray());

    private async Task<List<string>> LeerHashesAsync(ApplicationUser user, CancellationToken cancellationToken)
    {
        var valor = await GetTokenAsync(user, ProveedorInterno, NombreTokenCodigosRecuperacion, cancellationToken);
        if (string.IsNullOrEmpty(valor)) return [];

        return valor.Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Where(h => h.StartsWith(PrefijoFormato, StringComparison.Ordinal))
            .ToList();
    }

    private Task GuardarHashesAsync(ApplicationUser user, IEnumerable<string> hashes, CancellationToken cancellationToken) =>
        SetTokenAsync(user, ProveedorInterno, NombreTokenCodigosRecuperacion, string.Join(";", hashes), cancellationToken);

    private static string CalcularHash(string codigo)
    {
        var sal = RandomNumberGenerator.GetBytes(BytesSal);
        var hash = Derivar(Normalizar(codigo), sal);
        return $"{PrefijoFormato}{Convert.ToBase64String(sal)}${Convert.ToBase64String(hash)}";
    }

    private static bool Coincide(string almacenado, string codigoNormalizado)
    {
        var partes = almacenado[PrefijoFormato.Length..].Split('$');
        if (partes.Length != 2) return false;

        byte[] sal, esperado;
        try
        {
            sal = Convert.FromBase64String(partes[0]);
            esperado = Convert.FromBase64String(partes[1]);
        }
        catch (FormatException)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(Derivar(codigoNormalizado, sal), esperado);
    }

    private static byte[] Derivar(string codigoNormalizado, byte[] sal) =>
        Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(codigoNormalizado), sal, Iteraciones, HashAlgorithmName.SHA256, BytesHash);
}

/// <summary>Una fila de las funciones de resolución de cuenta de <c>RlsAspNetUsers</c>.</summary>
public sealed record CuentaResuelta
{
    public Guid CuentaId { get; init; }
    public Guid TenantId { get; init; }
}
