using System.Security.Claims;
using CaeManager.Application.Common;
using CaeManager.Domain.Seguridad;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;

namespace CaeManager.Web.Components.Account;

public static class IdentityEndpointsExtensions
{
    /// <summary>Nombre del esquema de autenticación de Entra ID — usado tanto al registrarlo (Program.cs) como al retarlo (aquí).</summary>
    public const string EsquemaMicrosoft = "Microsoft";

    public static IEndpointRouteBuilder MapIdentityEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/cuenta/cerrar-sesion", async (
            IFormCollection formulario, SignInManager<ApplicationUser> signInManager, HttpContext httpContext, ILoggerFactory loggerFactory) =>
        {
            var usuarioId = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier);
            loggerFactory.CreateLogger(AuditoriaAutenticacion.CategoriaLog)
                .LogInformation("Cierre de sesión: {UsuarioId}", usuarioId);

            await signInManager.SignOutAsync();

            // La cookie de Delegated Workspace no la borra SignOutAsync: es
            // nuestra, no de Identity. Sin esto sobrevivía al cierre de
            // sesión y al volver a entrar se reanudaba el workspace anterior
            // —incluido uno cuya delegación se hubiera revocado entretanto—
            // en lugar de empezar en el tenant de origen (hallazgo N-6 de
            // Project-Hydra-Negocio/seguridad/INFORME-AUDITORIA-2.md).
            httpContext.Response.Cookies.Delete(ClienteActivoSeleccionado.NombreCookie);

            // Tampoco la de cultura: el siguiente que entre en este navegador
            // no hereda el idioma de quien sale (su login la reescribe desde
            // su propia cuenta; hasta entonces, la pantalla de acceso en es-ES).
            CulturaUsuarioCookie.Eliminar(httpContext);

            return Results.LocalRedirect("/cuenta/iniciar-sesion");
        });

        // Punto de entrada del botón "Iniciar sesión con Microsoft" (Login.razor)
        // — solo se muestra si AzureAdOptions.EstaConfigurado. El propio
        // middleware de autenticación intercepta la vuelta desde Microsoft en
        // CallbackPath (InicioSesionMicrosoft.RutaRespuesta) antes de que
        // llegue aquí ningún código nuestro; RedirectUri es a dónde se manda
        // al navegador después de que ese middleware ya haya firmado la
        // cookie externa temporal.
        endpoints.MapGet("/cuenta/iniciar-sesion-microsoft", (string? returnUrl) => InicioSesionMicrosoft.Desafiar(returnUrl))
            .AllowAnonymous();

        // A dónde vuelve el navegador ya con la cookie externa temporal
        // firmada (IdentityConstants.ExternalScheme) tras un login de
        // Microsoft correcto. Ver RestriccionLoginLocalClaimsTransformation
        // para el resto del diseño de esta funcionalidad. No exige
        // DebeCambiarContrasena — ese flujo es solo para la contraseña
        // local, que un usuario SSO puede no usar nunca.
        endpoints.MapGet("/cuenta/microsoft-callback", async (
            SignInManager<ApplicationUser> signInManager,
            HttpContext httpContext,
            UserManager<ApplicationUser> userManager,
            IEmailService emailService,
            ILogger<Program> logger,
            ILoggerFactory loggerFactory,
            Microsoft.Extensions.Options.IOptions<AzureAdOptions> opcionesAzureAd,
            // [FromServices] explícito: sin él, un host que no registra la
            // vigilancia (arneses de Web.Tests) infiere el parámetro como cuerpo
            // y la construcción de TODOS los endpoints falla, no solo este.
            [Microsoft.AspNetCore.Mvc.FromServices] CaeManager.Infrastructure.Seguridad.IVigilanciaInicioSesion vigilancia,
            string? returnUrl) =>
        {
            var infoExterna = await signInManager.GetExternalLoginInfoAsync();
            if (infoExterna is null)
                return Results.LocalRedirect("/cuenta/iniciar-sesion?errorSso=fallo");

            var email = infoExterna.Principal.FindFirstValue(ClaimTypes.Email)
                ?? infoExterna.Principal.FindFirstValue("preferred_username");

            if (string.IsNullOrWhiteSpace(email))
            {
                logger.LogWarning("Login de Microsoft rechazado: la cuenta no trajo un email utilizable.");
                await signInManager.SignOutAsync();
                return Results.LocalRedirect("/cuenta/iniciar-sesion?errorSso=fallo");
            }

            // Vuelta del SSO: todavía no hay sesión ni Tenant, y AspNetUsers tiene
            // RLS (P1-M1). Búsqueda, alta y vinculación del login pasan por el
            // camino declarado de identificación, que las acota al Tenant de la
            // propia cuenta.
            using var identificacion = AmbitoIdentificacionSinTenant.Abrir();

            // Con qué cuenta entra esta identidad: ver InicioSesionMicrosoft.DecidirCuenta.
            // Primero el vínculo guardado (no cambia con el correo); el correo, que en
            // Entra es un atributo editable, solo sin vínculo y solo dentro del Tenant
            // destino. Antes se buscaba solo por correo y en TODOS los Tenants (la
            // búsqueda sin Tenant resuelve el de la cuenta): una identidad del
            // directorio configurado con el correo de una cuenta de otra organización,
            // o de Soporte TALVEG, se vinculaba a ella y entraba sin su contraseña ni
            // su segundo factor.
            var tenantDestino = opcionesAzureAd.Value.TenantDestino;
            var (decision, usuario) = await InicioSesionMicrosoft.ResolverCuentaAsync(userManager, infoExterna, email, tenantDestino);

            switch (decision)
            {
                case DecisionCuentaMicrosoft.RechazarSinTenantDestino:
                    logger.LogError(
                        "Login de Microsoft rechazado: falta AzureAd:TenantHydraId, así que no se puede " +
                        "determinar a qué tenant pertenecería la cuenta.");
                    await signInManager.SignOutAsync();
                    return Results.LocalRedirect("/cuenta/iniciar-sesion?errorSso=fallo");

                case DecisionCuentaMicrosoft.RechazarCuentaDeOtroTenant:
                case DecisionCuentaMicrosoft.RechazarCuentaVinculadaAOtraIdentidad:
                {
                    // Queda en la vigilancia del Tenant de la cuenta que se intentó
                    // abrir: es a quien le interesa saber que alguien lo intentó.
                    // Mismo mensaje genérico que el resto: no se confirma que exista.
                    // Solo deja la fila Rechazado: la vigilancia no avisa por ella.
                    logger.LogWarning("Login de Microsoft rechazado ({Decision}) para la cuenta {UsuarioId}.", decision, usuario?.Id);
                    if (usuario is not null)
                        await vigilancia.RegistrarAsync(usuario.Id, usuario.TenantId, MetodoInicioSesion.Microsoft, ResultadoInicioSesion.Rechazado);
                    await signInManager.SignOutAsync();
                    return Results.LocalRedirect("/cuenta/iniciar-sesion?errorSso=fallo");
                }
            }

            var esUsuarioNuevo = decision == DecisionCuentaMicrosoft.CrearCuenta;

            if (esUsuarioNuevo)
            {
                // Auto-provisión sin rol: cualquier cuenta del tenant de la
                // empresa (ya restringido por AzureAdOptions.TenantId, ver
                // InicioSesionMicrosoft.Configurar) puede iniciar sesión, pero queda
                // en la sala de espera ("Pendientes de asignar", ver Roles.razor)
                // hasta que un Administrador le asigna un rol — nunca entra con
                // acceso real solo por tener cuenta de Microsoft de la empresa.
                // Nace en el Tenant destino declarado (TenantHydraId), nunca en
                // Guid.Empty: una cuenta que no se sabe de quién es no se arregla
                // más tarde.
                var nombreCompleto = infoExterna.Principal.FindFirstValue(ClaimTypes.Name) ?? email;
                usuario = new ApplicationUser
                {
                    UserName = email,
                    Email = email,
                    NombreCompleto = nombreCompleto,
                    EmailConfirmed = true,
                    DebeCambiarContrasena = false,
                    TenantId = tenantDestino,
                };

                var resultadoCreacion = await userManager.CreateAsync(usuario);
                if (!resultadoCreacion.Succeeded)
                {
                    logger.LogError(
                        "No se pudo auto-provisionar la cuenta de {Email} desde el login de Microsoft: {Errores}",
                        email, string.Join(" ", resultadoCreacion.Errors.Select(e => e.Description)));
                    await signInManager.SignOutAsync();
                    return Results.LocalRedirect("/cuenta/iniciar-sesion?errorSso=fallo");
                }
            }

            if (usuario is null)
            {
                // Inalcanzable con DecidirCuenta: toda decisión que no rechaza deja cuenta.
                await signInManager.SignOutAsync();
                return Results.LocalRedirect("/cuenta/iniciar-sesion?errorSso=fallo");
            }

            // SignInWithClaimsAsync no comprueba bloqueo (a diferencia de
            // PasswordSignInAsync): sin esto, una cuenta desactivada volvía a
            // entrar por Microsoft sin necesitar su contraseña (auditoría de
            // seguridad 2026-09-20). Mismo mensaje genérico que el resto de
            // rechazos: no se confirma a un tercero que la cuenta existe.
            if (usuario.EstaDesactivada(DateTimeOffset.UtcNow))
            {
                logger.LogWarning("Login de Microsoft rechazado: la cuenta {UsuarioId} está desactivada.", usuario.Id);
                await vigilancia.RegistrarAsync(usuario.Id, usuario.TenantId, MetodoInicioSesion.Microsoft, ResultadoInicioSesion.Rechazado);
                await signInManager.SignOutAsync();
                return Results.LocalRedirect("/cuenta/iniciar-sesion?errorSso=fallo");
            }

            // Bloqueo por país del Tenant propietario de la cuenta (desactivado
            // por defecto). Mismo mensaje genérico que el resto de rechazos.
            if (!await vigilancia.PermiteOrigenAsync(usuario.Id, usuario.TenantId))
            {
                await vigilancia.RegistrarAsync(usuario.Id, usuario.TenantId, MetodoInicioSesion.Microsoft,
                    ResultadoInicioSesion.Rechazado, SenalesInicioSesion.PaisNoPermitido);
                await signInManager.SignOutAsync();
                return Results.LocalRedirect("/cuenta/iniciar-sesion?errorSso=fallo");
            }

            // El vínculo se guarda antes de entrar y su resultado se mira: antes se
            // ignoraba, y una identidad que no quedaba vinculada entraba igual.
            if (decision is DecisionCuentaMicrosoft.VincularCuentaExistente or DecisionCuentaMicrosoft.CrearCuenta)
            {
                var resultadoVinculo = await userManager.AddLoginAsync(usuario, infoExterna);
                if (!resultadoVinculo.Succeeded)
                {
                    logger.LogError(
                        "No se pudo vincular la identidad de Microsoft a la cuenta {UsuarioId}: {Errores}",
                        usuario.Id, string.Join(" ", resultadoVinculo.Errors.Select(e => e.Description)));
                    await signInManager.SignOutAsync();
                    return Results.LocalRedirect("/cuenta/iniciar-sesion?errorSso=fallo");
                }
            }

            await signInManager.SignInWithClaimsAsync(usuario, isPersistent: true,
                additionalClaims: [new Claim(RestriccionLoginLocalClaimsTransformation.TipoClaimMetodoLogin, RestriccionLoginLocalClaimsTransformation.MetodoLoginSso)]);

            // La cookie externa temporal ya está consumida: se borra, como hace
            // ExternalLoginSignInAsync y no SignInWithClaimsAsync, para que no quede
            // en el navegador un principal de Microsoft reutilizable en este callback.
            await httpContext.SignOutAsync(IdentityConstants.ExternalScheme);

            loggerFactory.CreateLogger(AuditoriaAutenticacion.CategoriaLog)
                .LogInformation("Login SSO correcto: {UsuarioId}", usuario.Id);
            await vigilancia.RegistrarAsync(usuario.Id, usuario.TenantId, MetodoInicioSesion.Microsoft, ResultadoInicioSesion.Correcto);

            // Mismo criterio que Login.razor/LoginCon2fa.razor: la cookie de
            // cultura se reconstruye desde ApplicationUser.Idioma en cada
            // inicio de sesión. Antes de las dos salidas (sala de espera y
            // returnUrl).
            CulturaUsuarioCookie.Escribir(httpContext, usuario.Idioma);

            var roles = await userManager.GetRolesAsync(usuario);
            if (roles.Count == 0)
            {
                // Aviso a los Administradores solo la primera vez (al crear
                // la cuenta) — no en cada login mientras siga pendiente, para
                // no generar correo repetido. Best-effort: un fallo de envío
                // nunca debe impedir que el usuario entre a la sala de espera.
                if (esUsuarioNuevo)
                    await NotificarAdministradoresUsuarioPendienteAsync(userManager, emailService, logger, usuario);

                return Results.LocalRedirect("/cuenta/pendiente-de-rol");
            }

            // Sanear en vez de confiar en que LocalRedirect lance: con un
            // returnUrl malicioso ("//atacante.com") LocalRedirect responde
            // 500; saneado, el usuario aterriza en "/" y sigue trabajando.
            return Results.LocalRedirect(RedireccionLocal.DestinoTrasLogin(returnUrl));
        }).AllowAnonymous();

        return endpoints;
    }

    /// <summary>
    /// Avisa <b>solo</b> a los Administradores del tenant al que pertenece la
    /// cuenta nueva.
    ///
    /// <para>
    /// <c>GetUsersInRoleAsync</c> no filtra por Tenant, y antes de P1-M1
    /// <c>AspNetUsers</c> no tenía RLS, así que este correo enviaba el nombre y
    /// la dirección de la persona recién registrada a los Administradores de
    /// <b>todas</b> las organizaciones del SaaS. Ahora la lectura corre dentro
    /// de <see cref="AmbitoTenantExplicito"/> con el Tenant de la cuenta nueva
    /// —aquí todavía no hay sesión de la que tirar— y la RLS la acota. El
    /// filtro en memoria se conserva: la política deja ver también a quien
    /// opera ese Tenant desde otro (Operador Delegado, Gestor CAE con cartera,
    /// actores de su auditoría), y el aviso es solo para los Administradores
    /// del Tenant propietario de la cuenta.
    /// </para>
    /// </summary>
    private static async Task NotificarAdministradoresUsuarioPendienteAsync(
        UserManager<ApplicationUser> userManager, IEmailService emailService, ILogger logger, ApplicationUser usuarioPendiente)
    {
        List<ApplicationUser> administradores;
        using (AmbitoTenantExplicito.Establecer(usuarioPendiente.TenantId))
        {
            administradores = (await userManager.GetUsersInRoleAsync(Roles.Administrador))
                .Where(a => a.TenantId == usuarioPendiente.TenantId)
                .ToList();
        }
        var cuerpo = $"""
            <p>{System.Net.WebUtility.HtmlEncode(usuarioPendiente.NombreCompleto)} ({System.Net.WebUtility.HtmlEncode(usuarioPendiente.Email)}) inició sesión con su cuenta de Microsoft y está a la espera de que le asignes un rol.</p>
            <p>Puedes hacerlo desde la pestaña "Pendientes de asignar" en Roles.</p>
            """;

        foreach (var administrador in administradores)
        {
            if (string.IsNullOrWhiteSpace(administrador.Email)) continue;

            var resultado = await emailService.EnviarAsync(
                administrador.Email, $"Nuevo usuario pendiente de asignar rol — {Marca.Nombre}", cuerpo, TipoAvisoCorreo.Informativo);
            if (resultado.EsFallido)
                logger.LogWarning("No se pudo notificar a {UsuarioId} sobre un usuario pendiente de rol.", administrador.Id);
        }
    }
}
