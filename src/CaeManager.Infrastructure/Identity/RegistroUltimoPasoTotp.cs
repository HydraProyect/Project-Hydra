using CaeManager.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Infrastructure.Identity;

/// <summary>
/// Dónde guarda <see cref="ProveedorTotpDeUnSoloUso"/> el último paso TOTP aceptado de cada cuenta.
/// </summary>
public interface IRegistroUltimoPasoTotp
{
    /// <summary>El valor guardado para la cuenta, o nulo si no hay ninguno.</summary>
    Task<string?> LeerAsync(Guid usuarioId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Guarda <paramref name="nuevo"/> solo si el valor guardado sigue siendo <paramref name="anterior"/>
    /// (nulo: que no haya fila). Devuelve si guardó; de dos llamadas con el mismo
    /// <paramref name="anterior"/>, aunque sean simultáneas o de réplicas distintas, solo una lo consigue.
    /// </summary>
    Task<bool> SustituirAsync(Guid usuarioId, string? anterior, string nuevo, CancellationToken cancellationToken = default);
}

/// <summary>
/// El registro en <c>AspNetUserTokens</c> (proveedor <see cref="AlmacenUsuarios.ProveedorInterno"/>, nombre
/// <see cref="ProveedorTotpDeUnSoloUso.NombreTokenUltimoPaso"/>), la misma tabla que la clave TOTP y los
/// códigos de recuperación: sin migración y compartido por todas las réplicas.
///
/// <para>
/// <b>Por qué SQL y no <c>UserManager.SetAuthenticationTokenAsync</c>.</b> Ese camino guarda la cuenta
/// entera (rota su <c>ConcurrencyStamp</c>) y deja, en cada inicio de sesión con 2FA correcto, una fila
/// «Usuario Modificado» y otra «TokenDeUsuario» en <c>RegistrosAuditoria</c>, que no se purga: la marca de
/// tiempo de cada acceso guardada para siempre, cuando el inicio de sesión ya tiene su registro con
/// retención propia (<c>RegistrosInicioSesion</c>, 12 meses). Además, rotar el sello invalidaría el
/// guardado de quien estuviera editando esa cuenta. Aquí la comparación y la escritura son una sola
/// sentencia atómica de PostgreSQL, sin tocar <c>AspNetUsers</c>. El valor no es un secreto (un número de
/// paso y una huella de la clave que no la revela).
/// </para>
///
/// <para>
/// El <c>CaeManagerDbContext</c> (el mismo del ámbito) se resuelve al usarlo, no al construir:
/// <c>UserManager</c> crea todos sus proveedores de token al crearse, y quien solo lo quiere para validar
/// una contraseña no tiene por qué arrastrar el contexto de datos.
/// </para>
/// </summary>
public class RegistroUltimoPasoTotpPostgres(IServiceProvider servicios) : IRegistroUltimoPasoTotp
{
    private CaeManagerDbContext Contexto => servicios.GetRequiredService<CaeManagerDbContext>();

    public Task<string?> LeerAsync(Guid usuarioId, CancellationToken cancellationToken = default) =>
        Contexto.UserTokens.AsNoTracking()
            .Where(t => t.UserId == usuarioId
                        && t.LoginProvider == AlmacenUsuarios.ProveedorInterno
                        && t.Name == ProveedorTotpDeUnSoloUso.NombreTokenUltimoPaso)
            .Select(t => t.Value)
            .SingleOrDefaultAsync(cancellationToken);

    public async Task<bool> SustituirAsync(
        Guid usuarioId, string? anterior, string nuevo, CancellationToken cancellationToken = default) =>
        // ON CONFLICT … DO UPDATE … WHERE bloquea la fila y evalúa la condición sobre su última versión
        // confirmada: de dos sentencias con el mismo «anterior», la segunda no actualiza nada (0 filas).
        await Contexto.Database.ExecuteSqlAsync($"""
            INSERT INTO "AspNetUserTokens" ("UserId", "LoginProvider", "Name", "Value")
            VALUES ({usuarioId}, {AlmacenUsuarios.ProveedorInterno}, {ProveedorTotpDeUnSoloUso.NombreTokenUltimoPaso}, {nuevo})
            ON CONFLICT ("UserId", "LoginProvider", "Name") DO UPDATE SET "Value" = EXCLUDED."Value"
            WHERE "AspNetUserTokens"."Value" IS NOT DISTINCT FROM CAST({anterior} AS text)
            """, cancellationToken) == 1;
}
