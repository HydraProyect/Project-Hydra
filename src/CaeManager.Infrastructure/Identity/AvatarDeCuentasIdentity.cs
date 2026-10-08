using CaeManager.Application.Common;
using CaeManager.Application.Usuarios;
using CaeManager.Domain.Common;
using CaeManager.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Identity;

/// <inheritdoc cref="IAvatarDeCuentas" />
/// <remarks>
/// Pasa por <see cref="PuertaAccesoDatos"/> como <see cref="SegundoFactorDeCuentasIdentity"/>:
/// el menú de cuenta lee el avatar desde el layout, en paralelo con el resto de
/// componentes sobre el mismo <c>CaeManagerDbContext</c>.
///
/// <para>
/// <b>La escritura es un <c>UPDATE</c> directo del avatar, sin pasar por
/// <c>UserManager.UpdateAsync</c>.</b> Esa vía compara el <c>ConcurrencyStamp</c> de la
/// cuenta, que cambia en cada navegación (<c>ActividadUsuarioService</c> escribe la
/// misma fila): elegir un avatar fallaría por concurrencia sin que nadie más hubiera
/// tocado el avatar. Aquí no hay nada que perder —la columna solo la escribe su dueño—
/// y la política RLS <c>cuentas_modificacion</c> sigue decidiendo qué fila se puede tocar.
/// </para>
///
/// <para>
/// <b>El mismo <c>UPDATE</c> renueva el <c>ConcurrencyStamp</c>.</b> Quien conserve la
/// cuenta cargada de antes (<c>SelectorTema</c> la guarda en memoria durante horas) y la
/// escriba entera con <c>UserManager.UpdateAsync</c> devolvería el avatar anterior sin
/// que nadie lo notase; con el sello renovado, esa escritura falla por concurrencia y
/// su dueño recarga la fila antes de reintentar.
/// </para>
/// </remarks>
public class AvatarDeCuentasIdentity(CaeManagerDbContext contexto, PuertaAccesoDatos puertaAccesoDatos)
    : IAvatarDeCuentas
{
    public Task<string?> ObtenerAsync(Guid usuarioId, CancellationToken cancellationToken = default) =>
        puertaAccesoDatos.EjecutarAsync(
            () => contexto.Users
                .AsNoTracking()
                .Where(u => u.Id == usuarioId)
                .Select(u => u.Avatar)
                .FirstOrDefaultAsync(cancellationToken),
            cancellationToken);

    public Task<Result> GuardarAsync(Guid usuarioId, string? clave, CancellationToken cancellationToken = default) =>
        puertaAccesoDatos.EjecutarAsync(async () =>
        {
            var filas = await contexto.Users
                .Where(u => u.Id == usuarioId)
                .ExecuteUpdateAsync(
                    cambios => cambios
                        .SetProperty(u => u.Avatar, clave)
                        .SetProperty(u => u.ConcurrencyStamp, Guid.NewGuid().ToString()),
                    cancellationToken);

            return filas == 1
                ? Result.Exito()
                : Result.Fallo(Error.Crear("Avatar.NoGuardado", "No pudimos guardar tu avatar. Inténtalo de nuevo."));
        }, cancellationToken);
}
