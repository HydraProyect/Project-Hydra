using CaeManager.Application.VistaDemo;
using CaeManager.Infrastructure.Identity;

namespace CaeManager.Web.Components.Layout;

/// <summary>
/// Cómo la lente de demo reetiqueta el MENÚ. Puramente presentacional: el menú solo decide qué
/// enlaces existen, nunca a qué se puede acceder (eso lo autoriza cada pantalla y cada comando con
/// la autorización real). Solo puede OCULTAR: una lista de roles que la vista no contiene se
/// sustituye por un rol inexistente, así que la <c>AuthorizeView</c> real (que sigue exigiendo el
/// rol real de la cuenta) nunca muestra algo que sin la lente no se mostraría.
/// </summary>
public static class MenuPorVista
{
    /// <summary>Rol que ningún usuario tiene: la <c>AuthorizeView</c> que lo exige queda oculta.</summary>
    public const string RolInexistente = "__oculto_por_la_vista__";

    /// <summary>El rol de menú de una vista, o null si la vista no acota el menú (Dirección o sin lente).</summary>
    public static string? RolDeMenu(VistaDemo? vista) => vista switch
    {
        VistaDemo.CoordinadorCae => Roles.CoordinadorCae,
        VistaDemo.GestorCae => Roles.GestorCae,
        _ => null,
    };

    /// <summary>
    /// <paramref name="roles"/> tal cual si la vista no acota el menú o el rol de la vista está entre
    /// ellos; en otro caso <see cref="RolInexistente"/>.
    /// </summary>
    public static string Acotar(VistaDemo? vista, string roles)
    {
        var rolDeMenu = RolDeMenu(vista);
        if (rolDeMenu is null) return roles;

        return roles.Split(',', StringSplitOptions.TrimEntries).Contains(rolDeMenu, StringComparer.Ordinal)
            ? roles
            : RolInexistente;
    }
}
