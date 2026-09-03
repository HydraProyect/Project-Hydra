using CaeManager.Application.Common;
using CaeManager.Application.Plataforma.Commands.AbrirSesionPrivilegiada;
using CaeManager.Application.Plataforma.Commands.CerrarSesionPrivilegiada;
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
            [FromForm] string motivo, [FromForm] int horasDeVentana, [FromForm] string? ticket,
            [FromForm] string? returnUrl,
            HttpContext httpContext, IMediator mediator, ICurrentUserService currentUserService,
            IClienteActivoSeleccionado clienteActivoSeleccionado,
            IDataProtectionProvider dataProtectionProvider) =>
            AbrirAsync(
                concesionPrivilegioId, tenantObjetivoId, motivo, horasDeVentana, ticket, returnUrl,
                httpContext, mediator, currentUserService, clienteActivoSeleccionado, dataProtectionProvider));

        // La salida. POST por el mismo motivo que la apertura: borra la cookie de
        // contexto, que es estado de servidor.
        endpoints.MapPost("/cuenta/soporte/salir", (
            HttpContext httpContext, ICurrentUserService currentUserService,
            IClienteActivoSeleccionado clienteActivoSeleccionado) =>
            SalirAsync(httpContext, currentUserService, clienteActivoSeleccionado));

        // El cierre. Va SEPARADO de la salida y es una petición distinta a propósito
        // — ver el comentario de <see cref="CerrarAsync"/>.
        endpoints.MapPost("/cuenta/soporte/cerrar", (
            [FromForm] Guid sesionPrivilegiadaId,
            IMediator mediator, ICurrentUserService currentUserService,
            IClienteActivoSeleccionado clienteActivoSeleccionado) =>
            CerrarAsync(sesionPrivilegiadaId, mediator, currentUserService, clienteActivoSeleccionado));

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
        Guid concesionPrivilegioId, Guid tenantObjetivoId, string motivo, int horasDeVentana,
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
            concesionPrivilegioId, tenantObjetivoId, motivo, horasDeVentana, ticket));

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

    /// <summary>
    /// <b>Salir del tenant visitado</b> — primera mitad de la ceremonia de cierre (B1.3).
    ///
    /// <para>
    /// Borra la cookie de contexto y <b>lleva el id de la sesión en la redirección</b>.
    /// Eso último no es comodidad: al volver, la cookie ya no existe, así que el id no
    /// puede salir de ella, y <c>IPlataformaQueryContext</c> declara de forma normativa
    /// que «no existe ni debe existir un listar sesiones» — así que tampoco puede salir
    /// de una consulta. Pasarlo por la URL es lo único que queda, y es seguro porque
    /// <b>el id no es autoridad</b>: la política RLS <c>privilegio_del_usuario</c> (F2b-5)
    /// solo entrega las sesiones que cuelgan de una concesión que nombra al usuario
    /// actual, así que un id ajeno escrito a mano no encuentra ninguna fila que cerrar.
    /// </para>
    ///
    /// <para>
    /// <b>No cierra aquí.</b> En esta misma petición el interceptor ya hizo
    /// <c>SET ROLE cae_app_soporte</c> —que solo tiene <c>SELECT</c>— y
    /// <c>RevalidacionClienteActivoMiddleware</c> ya memoizó la sesión en
    /// <c>ISesionPrivilegiadaActual</c>, así que el <c>UPDATE</c> del cierre moriría
    /// dos veces: denegado por <c>AutorizacionEscrituraBehavior</c> y, si se le
    /// exceptuara, con 42501 en Postgres. Borrar la cookie de la respuesta no cambia
    /// ninguna de las dos cosas dentro de la petición que se está sirviendo.
    /// </para>
    /// </summary>
    public static async Task<IResult> SalirAsync(
        HttpContext httpContext,
        ICurrentUserService currentUserService,
        IClienteActivoSeleccionado clienteActivoSeleccionado)
    {
        var usuarioId = await currentUserService.ObtenerUsuarioActualIdAsync();
        if (usuarioId is null)
            return Results.Unauthorized();

        // Sin sesión de plano 3 no hay nada que abandonar. Se corta en vez de borrar
        // la cookie igualmente: este endpoint no es un "olvida el workspace" genérico
        // —ese es /cuenta/cliente-activo, con sus propias reglas— y confundirlos daría
        // una segunda vía de salir del plano 2 sin pasar por ellas.
        if (clienteActivoSeleccionado.SesionPrivilegiadaIdSeleccionada is not { } sesionId)
            return Results.Forbid();

        httpContext.Response.Cookies.Delete(ClienteActivoSeleccionado.NombreCookie);

        return Results.LocalRedirect($"/configuracion/plataforma?sesionParaCerrar={sesionId}");
    }

    /// <summary>
    /// <b>Cerrar la sesión privilegiada</b> — segunda mitad, y por eso una petición
    /// aparte (B1.3, opción B).
    ///
    /// <para>
    /// Al llegar aquí la cookie ya no viaja, así que el interceptor <b>no</b> adopta el
    /// rol de solo lectura y <c>ISesionPrivilegiadaActual</c> resuelve a nulo: el
    /// <c>UPDATE</c> corre bajo <c>cae_app_runtime</c> y el behavior lo deja pasar. La
    /// alternativa —invalidar la sesión a mitad de petición para que la escritura
    /// pasara— exigía añadir un método a <c>ISesionPrivilegiadaActual</c> cuyo único
    /// propósito sería apagar la garantía de solo lectura del plano 3. Se descartó por
    /// eso: funcionaría, y dejaría el interruptor puesto para siempre.
    /// </para>
    ///
    /// <para>
    /// <b>La guarda vive aquí y no en la pantalla</b>, y no es una preferencia. Dentro
    /// de un circuito de Blazor no hay <c>HttpContext</c>, así que
    /// <c>ClienteActivoSeleccionado</c> resuelve a nulo y memoiza ese nulo
    /// (<c>SeleccionSinHttpContextTests</c>): una comprobación en la página diría
    /// siempre «no hay sesión activa» y sería un instrumento ciego. En un endpoint la
    /// cookie sí es legible, así que aquí la comprobación observa de verdad lo que dice
    /// observar.
    /// </para>
    /// </summary>
    public static async Task<IResult> CerrarAsync(
        Guid sesionPrivilegiadaId,
        IMediator mediator,
        ICurrentUserService currentUserService,
        IClienteActivoSeleccionado clienteActivoSeleccionado)
    {
        var usuarioId = await currentUserService.ObtenerUsuarioActualIdAsync();
        if (usuarioId is null)
            return Results.Unauthorized();

        // Todavía dentro del tenant visitado: se corta ANTES de despachar. Si se
        // despachara, el behavior lo denegaría igual, pero con un mensaje sobre solo
        // lectura que no explica lo que hay que hacer — que es salir primero.
        if (clienteActivoSeleccionado.SesionPrivilegiadaIdSeleccionada is not null)
            return Results.Forbid();

        var resultado = await mediator.Send(new CerrarSesionPrivilegiadaCommand(sesionPrivilegiadaId));

        // Mismo criterio que la apertura: el código de error viaja en la URL para que
        // la pantalla lo traduzca, nunca el texto de una excepción.
        return resultado.EsFallido
            ? Results.LocalRedirect(
                $"/configuracion/plataforma?errorCierre={Uri.EscapeDataString(resultado.Error.Codigo)}")
            : Results.LocalRedirect("/configuracion/plataforma?cerrada=1");
    }
}
