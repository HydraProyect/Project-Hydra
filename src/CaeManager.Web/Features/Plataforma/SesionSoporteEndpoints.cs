using CaeManager.Application.Common;
using CaeManager.Application.Plataforma.Commands.AbrirSesionPrivilegiada;
using CaeManager.Web.Components.Account;
using CaeManager.Web.Services;
using MediatR;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;

namespace CaeManager.Web.Features.Plataforma;

/// <summary>
/// <b>Entrada al tenant visitado por una sesión privilegiada de plano 3</b> (B1).
///
/// <para>
/// Es el productor que faltaba. Todo el plano 3 estaba construido —concesión, sesión,
/// ceremonia, cierre, denegación de escritura, rol de solo lectura en PostgreSQL— pero
/// <see cref="AbrirSesionPrivilegiadaCommand"/> no tenía ningún llamador de producción,
/// y <c>ClienteActivoSeleccionado.Proteger</c> aceptaba un id de sesión que nadie le
/// pasaba. Sin este endpoint <b>nadie podía emitir la cookie que activa el circuito</b>.
/// </para>
///
/// <para>
/// <b>Por qué un fichero aparte y no una rama más de <see cref="Tenants.ClienteActivoEndpoints"/>.</b>
/// Aquel calcula su autorización como un OR de tres ramas. Añadir el plano 3 ahí sería
/// una cuarta rama del mismo OR — exactamente la acumulación entre planos que el lector
/// del token rechaza cuando el token nombra a la vez operación y sesión. Un emisor que
/// las mezcla y un lector que las prohíbe es una contradicción latente. Además B3 borrará
/// la rama heredada de aquel fichero: separados, B1 es aditivo puro y B3 es borrado puro.
/// </para>
///
/// <para>
/// <b>Este endpoint no reimplementa ni una precondición.</b> 2FA, tenant ajeno, concesión
/// propia, capacidad que abre y cobertura del ámbito viven en el comando. Copiarlas aquí
/// crearía un segundo sitio donde decidir lo mismo, que es un segundo sitio donde pueden
/// dejar de coincidir. Lo único propio es la guarda de contexto de § 2.
/// </para>
/// </summary>
public static class SesionSoporteEndpoints
{
    public static IEndpointRouteBuilder MapSesionSoporteEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // POST con formulario, igual que /cuenta/cliente-activo y por el mismo motivo:
        // cambia estado de servidor —escribe la cookie de contexto— así que no puede
        // viajar en una URL que alguien haga seguir a un usuario, y al aceptar formulario
        // UseAntiforgery valida el token sin que haya que acordarse de hacerlo.
        endpoints.MapPost("/cuenta/soporte/abrir", (
            [FromForm] Guid concesionPrivilegioId, [FromForm] Guid tenantObjetivoId,
            [FromForm] string motivo, [FromForm] int diasDeVentana, [FromForm] string? ticket,
            [FromForm] string? returnUrl,
            HttpContext httpContext, IMediator mediator, ICurrentUserService currentUserService,
            IClienteActivoSeleccionado clienteActivoSeleccionado,
            IDataProtectionProvider dataProtectionProvider) =>
            AbrirAsync(
                concesionPrivilegioId, tenantObjetivoId, motivo, diasDeVentana, ticket, returnUrl,
                httpContext, mediator, currentUserService, clienteActivoSeleccionado, dataProtectionProvider));

        return endpoints;
    }

    /// <summary>
    /// El manejador, extraído del lambda para que sea comprobable.
    ///
    /// <para>
    /// No es una concesión a los tests: las tres cosas que este endpoint decide —la guarda
    /// de contexto, que un comando fallido <b>no</b> deje cookie, y que el token nombre la
    /// sesión y <b>no</b> una operación— no las cubre ningún test del comando, y no hay en
    /// todo <c>tests/</c> ningún arnés de tubería HTTP con el que ejercitarlas desde fuera.
    /// Un lambda dentro de <c>MapPost</c> no se puede llamar; un método sí.
    /// </para>
    ///
    /// <para>
    /// Pública, no <c>internal</c>, por el mismo motivo que
    /// <c>WebhookWhatsAppEndpoints.EsFirmaValida</c>: no hay <c>InternalsVisibleTo</c> en
    /// <c>CaeManager.Web</c>, y añadirlo por un solo método abriría el ensamblado entero.
    /// </para>
    /// </summary>
    public static async Task<IResult> AbrirAsync(
        Guid concesionPrivilegioId, Guid tenantObjetivoId, string motivo, int diasDeVentana,
        string? ticket, string? returnUrl,
        HttpContext httpContext, IMediator mediator, ICurrentUserService currentUserService,
        IClienteActivoSeleccionado clienteActivoSeleccionado,
        IDataProtectionProvider dataProtectionProvider)
    {
        var usuarioId = await currentUserService.ObtenerUsuarioActualIdAsync();
        if (usuarioId is null)
            return Results.Unauthorized();

        // Única validación propia del endpoint: no se abre un acceso de soporte
        // desde dentro de otro workspace. Mismo criterio que la vía heredada, y
        // aquí además evita un fallo peor: con una sesión de plano 3 ya activa la
        // conexión lleva SET ROLE al rol de solo lectura, así que el INSERT de la
        // sesión nueva moriría con 42501. La guarda convierte un 500 en un 403.
        if (clienteActivoSeleccionado.TenantIdSeleccionado is not null)
            return Results.Forbid();

        var resultado = await mediator.Send(new AbrirSesionPrivilegiadaCommand(
            concesionPrivilegioId, tenantObjetivoId, motivo, diasDeVentana, ticket));

        if (resultado.EsFallido)
        {
            // Sin cookie. Es la mitad del contrato que los negativos comprueban:
            // que el comando falle no basta si el endpoint deja el contexto abierto
            // igualmente. El código viaja en la URL para que la pantalla lo traduzca;
            // es un código de error propio, no texto de la excepción.
            var destino = RedireccionLocal.Sanear(returnUrl);
            var separador = destino.Contains('?') ? '&' : '?';
            return Results.LocalRedirect(
                $"{destino}{separador}errorSoporte={Uri.EscapeDataString(resultado.Error.Codigo)}");
        }

        // asignacionOperacionId: null NO es cosmético. Un token que nombre a la vez
        // operación y sesión lo descarta entero el lector, y el técnico caería en
        // silencio a su propio tenant creyendo estar en el visitado.
        var token = ClienteActivoSeleccionado.Proteger(
            dataProtectionProvider, usuarioId.Value, tenantObjetivoId,
            asignacionOperacionId: null, sesionPrivilegiadaId: resultado.Valor);

        // Mismas opciones que la cookie de workspace: la diferencia entre los dos
        // caminos está en la carga útil del token, no en cómo se transporta.
        httpContext.Response.Cookies.Append(ClienteActivoSeleccionado.NombreCookie, token, new CookieOptions
        {
            HttpOnly = true,
            Secure = httpContext.Request.IsHttps,
            SameSite = SameSiteMode.Lax,
            MaxAge = ClienteActivoSeleccionado.Vigencia,
        });

        // A la raíz, no a returnUrl: la pantalla desde la que se abre es de
        // plataforma y no existe dentro del tenant visitado. Y bajo sesión
        // privilegiada el principal se queda sin claims de rol, así que muchas rutas
        // fallarían cerradas — aterrizar en "/" es lo único que se puede prometer.
        return Results.LocalRedirect("/");
    }
}
