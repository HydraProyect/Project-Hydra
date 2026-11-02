using CaeManager.Application.Common;
using CaeManager.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Infrastructure.Identity;

/// <summary>
/// Lo que la emisión de una cookie necesita saber del sello de seguridad para no dejar en ella uno
/// que no es el de la base (ver <see cref="SignInManagerCuentaDesactivada.SignInWithClaimsAsync"/>).
/// </summary>
public interface ISelloSeguridadCuenta
{
    /// <summary>
    /// El sello que hay ahora en la base, leído sin pasar por la entidad rastreada de la petición, que
    /// pudo cargarse antes de un cambio de credencial. Nulo si la cuenta no se encuentra.
    /// </summary>
    Task<string?> VigenteAsync(Guid usuarioId, Guid tenantPropietarioId, CancellationToken ct = default);

    /// <summary>Anota que esta petición rota el sello de la cuenta, que hasta ahora era <paramref name="selloAnterior"/>.</summary>
    void AnotarRotacion(Guid usuarioId, string? selloAnterior);

    /// <summary>
    /// ¿Rotó esta petición el sello de la cuenta? Con <see langword="true"/>,
    /// <paramref name="selloAnterior"/> es el que tenía antes de la primera rotación.
    /// </summary>
    bool RotadoEnEstaPeticion(Guid usuarioId, out string? selloAnterior);
}

/// <summary>
/// Implementación de <see cref="ISelloSeguridadCuenta"/>, con la vida de la petición.
///
/// <para>
/// <b>Por qué se anota la rotación.</b> Activar el segundo factor o cambiar la contraseña rotan el sello
/// y después refrescan la cookie, así que en ese refresco el sello de la cookie ya nunca coincide con el
/// de la base. Comparar solo esos dos cerraría la sesión de quien acaba de cambiar su credencial, y no
/// comparar nada deja que una cookie obsoleta salga con el sello nuevo. Lo que distingue a una de otra
/// es si la cookie llevaba el sello que esta misma petición sustituyó: lo anota
/// <see cref="AlmacenUsuarios.SetSecurityStampAsync"/>, por donde pasa toda rotación de Identity, y una
/// escritura sobre una entidad obsoleta no llega a la base por el <c>ConcurrencyStamp</c>.
/// </para>
///
/// <para>
/// <b>Lectura.</b> En un ámbito propio bajo <see cref="AmbitoTenantExplicito"/> con el Tenant propietario
/// de la cuenta, como <see cref="RegistroSesionesUsuario.RegistrarAsync"/>: se llama desde el inicio de
/// sesión, sin Tenant en el contexto, y desde un refresco, que puede tener abierto otro workspace.
/// </para>
/// </summary>
public sealed class SelloSeguridadCuenta(IServiceScopeFactory ambitos) : ISelloSeguridadCuenta
{
    private readonly Dictionary<Guid, string?> _anteriores = [];

    public async Task<string?> VigenteAsync(Guid usuarioId, Guid tenantPropietarioId, CancellationToken ct = default)
    {
        using var ambito = ambitos.CreateScope();
        using var _ = AmbitoTenantExplicito.Establecer(tenantPropietarioId);
        var contexto = ambito.ServiceProvider.GetRequiredService<CaeManagerDbContext>();
        return await contexto.Users
            .AsNoTracking()
            .Where(u => u.Id == usuarioId)
            .Select(u => u.SecurityStamp)
            .SingleOrDefaultAsync(ct);
    }

    // Se conserva el primero: una petición puede rotar el sello más de una vez, y el de la cookie es el
    // que había al empezar.
    public void AnotarRotacion(Guid usuarioId, string? selloAnterior) =>
        _anteriores.TryAdd(usuarioId, selloAnterior);

    public bool RotadoEnEstaPeticion(Guid usuarioId, out string? selloAnterior) =>
        _anteriores.TryGetValue(usuarioId, out selloAnterior);
}
