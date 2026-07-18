using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CaeManager.Infrastructure.Identity;

/// <summary>
/// Crea el primer usuario Administrador si todavía no existe ninguno. Se
/// invoca una vez al arrancar la aplicación (ver Program.cs) — usa
/// UserManager en vez de HasData porque el hash de contraseña de Identity
/// no es determinista y requiere las APIs reales para calcularse bien.
///
/// Email/contraseña son configurables (AdministradorInicial:Email /
/// AdministradorInicial:Contrasena) para que un despliegue compartido (ver
/// DEPLOY.md) no arranque con las credenciales por defecto, públicas en
/// este mismo archivo — en desarrollo local, sin configurar nada, se usan
/// esos valores por defecto tal cual siempre.
/// </summary>
public static class IdentitySeeder
{
    public const string EmailAdministradorInicial = "admin@caemanager.local";
    public const string ContrasenaAdministradorInicial = "CaeManager#2026";

    public static async Task SeedAsync(
        UserManager<ApplicationUser> userManager,
        RoleManager<IdentityRole<Guid>> roleManager,
        ILogger logger,
        IConfiguration configuration)
    {
        foreach (var rol in Identity.Roles.Todos)
        {
            if (!await roleManager.RoleExistsAsync(rol))
                await roleManager.CreateAsync(new IdentityRole<Guid>(rol));
        }

        var email = configuration["AdministradorInicial:Email"] ?? EmailAdministradorInicial;
        var contrasena = configuration["AdministradorInicial:Contrasena"] ?? ContrasenaAdministradorInicial;

        if (await userManager.FindByEmailAsync(email) is not null)
            return;

        var administrador = new ApplicationUser
        {
            UserName = email,
            Email = email,
            NombreCompleto = "Administrador",
            EmailConfirmed = true,
            // No es una contraseña temporal: el despliegue la eligió a
            // propósito (o acepta el default documentado en DEPLOY.md), no
            // hay ningún tercero esperando cambiarla en su primer acceso.
            DebeCambiarContrasena = false
        };

        var resultado = await userManager.CreateAsync(administrador, contrasena);
        if (!resultado.Succeeded)
        {
            logger.LogError(
                "No se pudo crear el usuario administrador inicial: {Errores}",
                string.Join(", ", resultado.Errors.Select(e => e.Description)));
            return;
        }

        await userManager.AddToRoleAsync(administrador, Identity.Roles.Administrador);
    }
}
