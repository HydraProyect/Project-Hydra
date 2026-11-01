using CaeManager.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;

namespace CaeManager.Infrastructure.Identity;

/// <summary>
/// El nombre de usuario y el correo siguen siendo únicos entre Tenants aunque la otra
/// cuenta no sea visible (P1-M1).
///
/// <para>
/// El <c>UserValidator</c> de Identity comprueba la unicidad buscando la cuenta con
/// <c>FindByNameAsync</c>/<c>FindByEmailAsync</c>, y esas búsquedas pasan por la RLS
/// de <c>AspNetUsers</c>: un Administrador que da de alta una cuenta en su Tenant no
/// ve las de los demás, así que el validador de serie aceptaría un correo que ya usa
/// otro Tenant. El nombre acabaría chocando con el índice único
/// <c>UserNameIndex</c> (un 500 en vez de un error de validación) y el correo —cuyo
/// índice no es único— quedaría duplicado, y el login por correo dejaría de poder
/// elegir cuenta.
/// </para>
///
/// <para>
/// Este validador pregunta a <c>app_nombre_en_uso_por_otra_cuenta</c> y
/// <c>app_correo_en_uso_por_otra_cuenta</c>, funciones <c>SECURITY DEFINER</c> que solo
/// dicen si otra cuenta usa ese valor: ni su <c>Id</c> ni su Tenant salen de la base.
/// Solo informa cuando la cuenta en conflicto NO es visible: si lo es, el validador de
/// serie ya da el mismo error y no se duplica. El mensaje es el
/// mismo que el de serie, así que no revela más de lo que Identity ya revelaba antes
/// de la política: que ese nombre o correo está en uso.
/// </para>
/// </summary>
public class ValidadorUnicidadGlobalCuenta(CaeManagerDbContext db) : IUserValidator<ApplicationUser>
{
    public async Task<IdentityResult> ValidateAsync(UserManager<ApplicationUser> manager, ApplicationUser user)
    {
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentNullException.ThrowIfNull(user);

        var errores = new List<IdentityError>();

        var nombre = await manager.GetUserNameAsync(user);
        if (!string.IsNullOrWhiteSpace(nombre)
            && await manager.FindByNameAsync(nombre) is null
            && await AlmacenUsuarios.NombreEnUsoPorOtraCuentaAsync(db, manager.NormalizeName(nombre), user.Id, default))
        {
            errores.Add(manager.ErrorDescriber.DuplicateUserName(nombre));
        }

        if (manager.Options.User.RequireUniqueEmail)
        {
            var email = await manager.GetEmailAsync(user);
            if (!string.IsNullOrWhiteSpace(email)
                && await manager.FindByEmailAsync(email) is null
                && await AlmacenUsuarios.CorreoEnUsoPorOtraCuentaAsync(db, manager.NormalizeEmail(email), user.Id, default))
            {
                errores.Add(manager.ErrorDescriber.DuplicateEmail(email));
            }
        }

        return errores.Count == 0 ? IdentityResult.Success : IdentityResult.Failed([.. errores]);
    }
}
