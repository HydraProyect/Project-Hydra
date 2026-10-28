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
    public const string EsquemaMicrosoft = AzureAdOptions.Esquema;

    public static IEndpointRouteBuilder MapIdentityEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/cuenta/cerrar-sesion", async (
            IFormCollection formulario, SignInManager<ApplicationUser> signInManager, HttpContext httpContext, ILoggerFactory loggerFactory) =>
        {
            var usuarioId = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier);
            var logger = loggerFactory.CreateLogger(AuditoriaAutenticacion.CategoriaLog);
            logger.LogInformation("Cierre de sesión: {UsuarioId}", usuarioId);

            // Revoca en el servidor solo esta sesión (una copia de la cookie deja de valer) y
            // cierra la Sesión Privilegiada que la petición tenga seleccionada. Ver CierreSesionEnServidor.
            await CierreSesionEnServidor.CerrarEstaSesionAsync(httpContext, logger);

            return await SalirAsync(signInManager, httpContext);
        });

        // Cerrar sesión en todos los dispositivos: rota el sello de seguridad y revoca todas las
        // sesiones de la cuenta, esta incluida. Formulario POST con antiforgery, como el de arriba.
        endpoints.MapPost("/cuenta/cerrar-sesion-en-todos-los-dispositivos", async (
            IFormCollection formulario, SignInManager<ApplicationUser> signInManager, HttpContext httpContext, ILoggerFactory loggerFactory) =>
        {
            var logger = loggerFactory.CreateLogger(AuditoriaAutenticacion.CategoriaLog);
            logger.LogInformation("Cierre de sesión en todos los dispositivos: {UsuarioId}",
                httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier));

            await CierreSesionEnServidor.CerrarTodasLasSesionesAsync(httpContext, logger);

            return await SalirAsync(signInManager, httpContext);
        }).RequireAuthorization();

        static async Task<IResult> SalirAsync(SignInManager<ApplicationUser> signInManager, HttpContext httpContext)
        {
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
        }

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

            // Cuenta invitada B2B del directorio (acct = 1): se rechaza salvo
            // AzureAd:PermitirInvitados, antes de buscar la cuenta, así que el
            // rechazo no depende de si existe en TALVEG ni lo revela. Sin cuenta
            // resuelta, se registra como «sin cuenta previa» (D-3): Fallido para
            // la ráfaga por IP, y en el log de auditoría con la clave de la
            // identidad de Microsoft (un identificador, no el correo).
            if (!InicioSesionMicrosoft.PermiteCuenta(infoExterna.Principal, opcionesAzureAd.Value))
            {
                loggerFactory.CreateLogger(AuditoriaAutenticacion.CategoriaLog)
                    .LogWarning("Login de Microsoft rechazado: la identidad {ClaveIdentidad} es una cuenta invitada (B2B) y AzureAd:PermitirInvitados está desactivada.",
                        infoExterna.ProviderKey);
                await vigilancia.RegistrarAsync(null, opcionesAzureAd.Value.TenantDestino,
                    MetodoInicioSesion.Microsoft, ResultadoInicioSesion.Fallido);
                await signInManager.SignOutAsync();
                return Results.LocalRedirect("/cuenta/iniciar-sesion?errorSso=fallo");
            }

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

                case DecisionCuentaMicrosoft.RechazarSinCuentaPrevia:
                    // Invitación previa (D-3): el inicio con Microsoft no crea cuentas.
                    // Sin cuenta no hay Tenant propietario ni persona a la que avisar, así
                    // que la vigilancia no deja fila: como un correo inexistente con
                    // contraseña, cuenta como Fallido para la ráfaga por IP. Queda en el
                    // log de auditoría de autenticación con la clave de la identidad de
                    // Microsoft (un identificador, no el correo). Mensaje propio: quien
                    // llega aquí ya se autenticó en el directorio configurado, y lo que
                    // necesita es que le den de alta.
                    loggerFactory.CreateLogger(AuditoriaAutenticacion.CategoriaLog)
                        .LogWarning("Login de Microsoft rechazado: ninguna cuenta del Tenant {TenantId} tiene el correo de la identidad {ClaveIdentidad}.",
                            tenantDestino, infoExterna.ProviderKey);
                    await vigilancia.RegistrarAsync(null, tenantDestino, MetodoInicioSesion.Microsoft, ResultadoInicioSesion.Fallido);
                    await signInManager.SignOutAsync();
                    return Results.LocalRedirect("/cuenta/iniciar-sesion?errorSso=sin-cuenta");

                case DecisionCuentaMicrosoft.RechazarCuentaDeOtroTenant:
                case DecisionCuentaMicrosoft.RechazarCuentaVinculadaAOtraIdentidad:
                case DecisionCuentaMicrosoft.RechazarCuentaDesactivada:
                    {
                        // Queda en la vigilancia del Tenant de la cuenta que se intentó
                        // abrir: es a quien le interesa saber que alguien lo intentó.
                        // No se confirma que exista: los tres reciben el mismo mensaje
                        // que «sin cuenta». En el Tenant destino no hay cuenta que abrir
                        // (la de otro Tenant no cuenta), y la desactivada o ya vinculada
                        // a otra identidad también la tiene que resolver un Administrador.
                        // Solo deja la fila Rechazado: la vigilancia no avisa por ella.
                        logger.LogWarning("Login de Microsoft rechazado ({Decision}) para la cuenta {UsuarioId}.", decision, usuario?.Id);
                        if (usuario is not null)
                            await vigilancia.RegistrarAsync(usuario.Id, usuario.TenantId, MetodoInicioSesion.Microsoft, ResultadoInicioSesion.Rechazado);
                        await signInManager.SignOutAsync();
                        return Results.LocalRedirect("/cuenta/iniciar-sesion?errorSso=sin-cuenta");
                    }
            }

            if (usuario is null)
            {
                // Inalcanzable con DecidirCuenta: toda decisión que no rechaza deja cuenta.
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

            // Segundo factor (D-2), antes de vincular: una identidad que no lo supera
            // no deja vínculo. Si Microsoft no acreditó la MFA, el TOTP de TALVEG por
            // el mismo paso que el inicio con contraseña (LoginCon2fa). Sin TOTP
            // configurado no se emite sesión: el alta del TOTP exige una sesión ya
            // emitida (MainLayout la fuerza solo a Administradores, y después). Un
            // vínculo nuevo a una cuenta con TOTP pasa siempre por el TOTP, también
            // con MFA de Microsoft, y se guarda solo al superarlo (ver DecidirSegundoFactor).
            var vinculoNuevo = decision == DecisionCuentaMicrosoft.VincularCuentaExistente;
            var segundoFactor = InicioSesionMicrosoft.DecidirSegundoFactor(
                InicioSesionMicrosoft.MfaAcreditadaPorMicrosoft(infoExterna.Principal, opcionesAzureAd.Value.ContextoAutenticacionMfa),
                await userManager.GetTwoFactorEnabledAsync(usuario),
                vinculoNuevo);

            if (segundoFactor == SegundoFactorMicrosoft.RechazarSinSegundoFactor)
            {
                loggerFactory.CreateLogger(AuditoriaAutenticacion.CategoriaLog)
                    .LogWarning("Login de Microsoft rechazado: sin MFA de Microsoft ni TOTP de TALVEG configurado: {UsuarioId}", usuario.Id);
                await vigilancia.RegistrarAsync(usuario.Id, usuario.TenantId, MetodoInicioSesion.Microsoft, ResultadoInicioSesion.Rechazado);
                await signInManager.SignOutAsync();
                return Results.LocalRedirect("/cuenta/iniciar-sesion?errorSso=sin-mfa");
            }

            if (segundoFactor == SegundoFactorMicrosoft.PedirTotp)
            {
                // Sin vincular todavía: un vínculo nuevo viaja en la cookie de dos
                // pasos, con el Id de la cuenta, y LoginCon2fa lo guarda solo al
                // superar el TOTP. La sesión la emite Identity al verificar el código,
                // con el proveedor externo, y SignInManagerCuentaDesactivada le pone la
                // marca de inicio con SSO.
                await httpContext.SignInAsync(IdentityConstants.TwoFactorUserIdScheme,
                    InicioSesionMicrosoft.PrincipalSegundoFactorPendiente(usuario.Id, vinculoNuevo ? infoExterna : null));
                await httpContext.SignOutAsync(IdentityConstants.ExternalScheme);
                loggerFactory.CreateLogger(AuditoriaAutenticacion.CategoriaLog)
                    .LogInformation("Login SSO correcto sin MFA de Microsoft, pendiente del TOTP de TALVEG: {UsuarioId}", usuario.Id);
                await vigilancia.RegistrarAsync(usuario.Id, usuario.TenantId, MetodoInicioSesion.Microsoft, ResultadoInicioSesion.SegundoFactorRequerido);

                var verificacion = "/cuenta/verificar-2fa";
                if (!string.IsNullOrWhiteSpace(returnUrl))
                    verificacion += $"?ReturnUrl={Uri.EscapeDataString(returnUrl)}";
                return Results.LocalRedirect(verificacion);
            }

            // El vínculo se guarda antes de entrar y su resultado se mira: antes se
            // ignoraba, y una identidad que no quedaba vinculada entraba igual.
            if (decision == DecisionCuentaMicrosoft.VincularCuentaExistente)
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
                // Cuenta creada antes sin rol: sala de espera hasta que un
                // Administrador se lo asigne.
                return Results.LocalRedirect("/cuenta/pendiente-de-rol");
            }

            // Sanear en vez de confiar en que LocalRedirect lance: con un
            // returnUrl malicioso ("//atacante.com") LocalRedirect responde
            // 500; saneado, el usuario aterriza en "/" y sigue trabajando.
            return Results.LocalRedirect(RedireccionLocal.DestinoTrasLogin(returnUrl));
        }).AllowAnonymous();

        return endpoints;
    }
}
