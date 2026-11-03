using System.Security.Claims;
using System.Text.Json;
using CaeManager.Application.Common;
using CaeManager.Application.Operaciones;
using CaeManager.Application.Seguridad.Incidentes;
using CaeManager.Application.Tenants;
using CaeManager.Infrastructure.Autenticacion;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Primitives;

namespace CaeManager.Web.Services;

/// <summary>
/// Contexto de una petición de la extensión de navegador: quién es, sobre qué Tenant opera y por qué Asignación de
/// Operación. Se decide aquí, una vez y antes de todo lo que dependa de ello.
///
/// <para>
/// <b>Por qué tan pronto.</b> El esquema predeterminado de la aplicación es la cookie, y los endpoints de la extensión
/// eligen su esquema en <c>UseAuthorization</c>: para entonces ya han corrido el ajuste del rol efectivo y la firma del
/// contexto RLS de las primeras consultas. Este middleware va tras <c>UseAuthentication</c> y <c>UseRateLimiter</c>, que
/// gasta el cupo antes de la primera consulta; solo actúa en <c>/extension/*</c> con el token de extensión, y a partir de
/// ahí esa petición tiene una sola identidad.
/// </para>
///
/// <para>
/// <b>La cookie no entra.</b> En una petición con token, la cookie de selección del Workspace operativo derivado se
/// descarta entera, también su Sesión Privilegiada: el token es la única credencial y la cabecera la única solicitud.
/// Sin token nada cambia: una sesión interactiva en <c>/extension/*</c> sigue por el camino de la cookie.
/// </para>
///
/// <para>
/// <b>El Tenant de la cabecera es una solicitud, nunca autoridad</b> (OWASP ASVS 4.0.3 V4.1.3, V4.2.1). Se resuelve con
/// el mismo predicado que <c>/cuenta/cliente-activo</c> (<see cref="TenantsBeneficiariosAutorizados"/>), en cada
/// petición y con el contexto del Tenant de origen; la conexión siguiente ya firma el Tenant beneficiario porque
/// <see cref="ITenantActual"/> lo lee de la selección fijada aquí. Solo abre la vía de Asignación de Operación: la vía
/// heredada de soporte y la Sesión Privilegiada no se alcanzan desde la extensión. Sin cabecera se opera en el Tenant de
/// origen, que es lo que hacían las versiones anteriores. Lo que no se pueda resolver se rechaza con 403, sin caer a
/// otro Tenant.
/// </para>
///
/// <para>
/// <b>La respuesta confirma el Tenant efectivo</b> (<see cref="ContextoExtension.CabeceraTenantEfectivo"/>). Sale de
/// <see cref="ITenantActual"/>, lo mismo que firma el contexto RLS, y no de la cabecera de la solicitud: repetirla no
/// demostraría nada. Un binario que ignore la solicitud no manda la confirmación, y la extensión falla en cerrado en
/// vez de presentar como del Tenant beneficiario lo que es del Tenant de origen. Lo que corre después de este middleware
/// en una petición de extensión no cambia la selección, así que el valor sigue siendo cierto al final de la petición.
/// </para>
/// </summary>
public class ContextoExtensionMiddleware(RequestDelegate siguiente)
{
    public async Task InvokeAsync(HttpContext contexto)
    {
        if (!ContextoExtension.Aplica(contexto.Request))
        {
            await siguiente(contexto);
            return;
        }

        // Tiene que ser la implementación que atiende a ITenantActual: si no lo es, no hay forma de fijar nada.
        var servicios = contexto.RequestServices;
        if (servicios.GetRequiredService<IClienteActivoSeleccionado>() is not ClienteActivoSeleccionado seleccion)
        {
            await ContextoExtension.RechazarAsync(contexto);
            return;
        }

        // Antes de autenticar, para que ni la selección de la cookie ni el usuario de la cookie firmen las consultas
        // que hace el propio esquema de extensión.
        seleccion.Invalidar();
        contexto.User = new ClaimsPrincipal(new ClaimsIdentity());

        var autenticacion = await contexto.AuthenticateAsync(ExtensionAuthenticationSchemeOptions.NombreEsquema);
        if (!autenticacion.Succeeded)
        {
            await contexto.ChallengeAsync(ExtensionAuthenticationSchemeOptions.NombreEsquema);
            return;
        }

        contexto.User = autenticacion.Principal;
        ContextoExtension.Marcar(contexto);

        var cabecera = contexto.Request.Headers[ContextoExtension.CabeceraTenant];
        if (cabecera.Count == 0)
        {
            ContextoExtension.ConfirmarTenantEfectivo(contexto);
            await siguiente(contexto);
            return;
        }

        try
        {
            if (!await ResolverAsync(contexto, cabecera, seleccion, servicios))
            {
                await ContextoExtension.RechazarAsync(contexto);
                return;
            }
        }
        catch (OperationCanceledException) when (contexto.RequestAborted.IsCancellationRequested)
        {
            return; // la extensión abandonó la petición: no es un error del servidor.
        }

        ContextoExtension.ConfirmarTenantEfectivo(contexto);
        await siguiente(contexto);
    }

    private static async Task<bool> ResolverAsync(
        HttpContext contexto, StringValues cabecera, ClienteActivoSeleccionado seleccion, IServiceProvider servicios)
    {
        var usuarioActual = servicios.GetRequiredService<ICurrentUserService>();
        if (!ContextoExtension.LeerTenantSolicitado(cabecera, out var tenantSolicitado)
            || await usuarioActual.ObtenerUsuarioActualIdAsync() is not { } usuarioId
            || await usuarioActual.ObtenerTenantOrigenIdAsync() is not { } tenantOrigenId)
            return false;

        if (tenantSolicitado == tenantOrigenId)
            return true;

        var cancelacion = contexto.RequestAborted;
        if (await TenantsBeneficiariosAutorizados.OperacionQueAutorizaAsync(
                servicios.GetRequiredService<IOperacionesQueryContext>(), usuarioId, tenantOrigenId, tenantSolicitado,
                await usuarioActual.ObtenerPerfilQueDecideAlcanceAsync(), DateTime.UtcNow, cancelacion)
            is not { } asignacionOperacionId)
        {
            // Misma señal que /cuenta/cliente-activo. La extensión deja de pedirlo al primer rechazo.
            if (servicios.GetService<ISenalesIncidenteSeguridad>() is { } senales)
                await senales.AccesoAOtroTenantRechazadoAsync(usuarioId, tenantOrigenId, tenantSolicitado, cancelacion);
            return false;
        }

        seleccion.FijarDesdeExtension(tenantSolicitado, asignacionOperacionId);

        // Sin rol efectivo en ese Tenant no hay nada que operar: se corta aquí, no consulta a consulta.
        if (await usuarioActual.ObtenerRolEfectivoAsync() is not null)
            return true;

        seleccion.Invalidar();
        return false;
    }
}

/// <summary>Lo que el resto del pipeline necesita saber de <see cref="ContextoExtensionMiddleware"/>.</summary>
public static class ContextoExtension
{
    /// <summary>El Tenant que la extensión solicita para esta petición.</summary>
    public const string CabeceraTenant = "X-Tenant-Beneficiario";

    /// <summary>
    /// El Tenant en el que el servidor resolvió esta petición: el Tenant beneficiario solicitado o el Tenant de origen,
    /// que son cosas distintas. Es una confirmación para la extensión, no una credencial: nadie la lee en el servidor.
    /// </summary>
    public const string CabeceraTenantEfectivo = "X-Tenant-Efectivo";

    /// <summary>Título estable del 403: la extensión lo distingue de cualquier otro para avisar a la persona.</summary>
    public const string TituloSeleccionNoAutorizada = "Extension.SeleccionNoAutorizada";

    private const string PrefijoRutas = "/extension";
    private const string EsquemaCabeceraToken = "Extension ";
    private static readonly object MarcaDePeticion = new();

    /// <summary>¿Esta petición es del esquema de extensión, ya autenticada y con su contexto resuelto?</summary>
    public static bool Es(HttpContext contexto) => contexto.Items.ContainsKey(MarcaDePeticion);

    public static void Marcar(HttpContext contexto) => contexto.Items[MarcaDePeticion] = true;

    /// <summary>Solo las rutas de la extensión y solo con su token: el resto de la aplicación no se entera.</summary>
    public static bool Aplica(HttpRequest peticion) =>
        peticion.Path.StartsWithSegments(PrefijoRutas, StringComparison.OrdinalIgnoreCase)
        && peticion.Headers.Authorization.ToString().StartsWith(EsquemaCabeceraToken, StringComparison.OrdinalIgnoreCase);

    /// <summary>Un único valor, un Guid completo y no vacío: cualquier otra cosa no es una solicitud válida.</summary>
    public static bool LeerTenantSolicitado(StringValues cabecera, out Guid tenantId)
    {
        tenantId = Guid.Empty;
        return cabecera.Count == 1
               && Guid.TryParseExact(cabecera[0], "D", out tenantId)
               && tenantId != Guid.Empty;
    }

    /// <summary>
    /// Se llama con la petición ya resuelta y nunca en un rechazo. Una petición sin Tenant no confirma ninguno: la
    /// extensión lo trata igual que a un servidor que no sabe confirmarlo.
    /// </summary>
    public static void ConfirmarTenantEfectivo(HttpContext contexto)
    {
        if (contexto.RequestServices.GetRequiredService<ITenantActual>().TenantId is { } tenantEfectivo)
            contexto.Response.Headers[CabeceraTenantEfectivo] = tenantEfectivo.ToString("D");
    }

    /// <summary>Con cuerpo: un 403 vacío lo reescribiría <c>UseStatusCodePagesWithReExecute</c> y la extensión no sabría por qué.</summary>
    public static Task RechazarAsync(HttpContext contexto)
    {
        contexto.Response.StatusCode = StatusCodes.Status403Forbidden;
        return contexto.Response.WriteAsJsonAsync(
            new
            {
                title = TituloSeleccionNoAutorizada,
                status = StatusCodes.Status403Forbidden,
            },
            (JsonSerializerOptions?)null, "application/problem+json", contexto.RequestAborted);
    }
}

public static class ContextoExtensionMiddlewareExtensions
{
    /// <summary>
    /// Después de <c>UseAuthentication</c> y <c>UseRateLimiter</c>, y ANTES de <c>UseSesionPrivilegiadaSinRolDeNegocio</c>,
    /// <c>UseTenantBeneficiarioPorDefecto</c> y <c>UseRolEfectivoDelWorkspace</c>: todos ellos necesitan ya la identidad
    /// y la selección definitivas de la petición.
    /// </summary>
    public static IApplicationBuilder UseContextoExtension(this IApplicationBuilder app) =>
        app.UseMiddleware<ContextoExtensionMiddleware>();
}
