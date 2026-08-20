using CaeManager.Application.Common;

namespace CaeManager.Web.Services;

/// <summary>
/// Resuelve la identidad de auditoría a partir de la sesión: quién es el
/// usuario y, si está operando un workspace delegado, bajo qué
/// <c>AsignacionOperacion</c> lo hace.
///
/// Hoy el actor real y el usuario autorizado son el mismo — no existe la
/// impersonación — así que este servicio no cambia ningún comportamiento. Lo
/// que sí añade desde el primer día es la <b>vía</b>: hasta ahora un registro
/// de auditoría decía "el usuario X tocó la entidad Y en el tenant Z" sin
/// forma de saber que lo hizo operando por delegación. Esa información ya
/// existía en la sesión desde F1; solo faltaba guardarla.
///
/// Deliberadamente <b>no</b> consulta la base de datos: se audita en cada
/// SaveChanges, y una consulta extra ahí multiplicaría el coste de cada
/// guardado del sistema. Todo lo que necesita viaja en el token de selección
/// de workspace, ya validado por el middleware de revalidación.
/// </summary>
public class ActorAuditoriaDesdeSesion(
    ICurrentUserService currentUserService,
    IClienteActivoSeleccionado clienteActivoSeleccionado) : IActorAuditoria
{
    public async Task<ActorAuditoria> ObtenerAsync()
    {
        var usuarioId = await currentUserService.ObtenerUsuarioActualIdAsync();
        return Construir(usuarioId);
    }

    /// <summary>
    /// El camino del guardado síncrono. Solo aprovecha la identidad si el
    /// contrato asíncrono ya la tiene resuelta —el caso normal, porque los
    /// claims están cacheados en el circuito—; si no, devuelve null para que
    /// quien llama registre explícitamente "no lo sé" en vez de inventar un
    /// acceso normal sin usuario.
    /// </summary>
    public ActorAuditoria? ObtenerSiYaEstaResuelto()
    {
        var tarea = currentUserService.ObtenerUsuarioActualIdAsync();
        if (!tarea.IsCompletedSuccessfully) return null;

        return Construir(tarea.Result);
    }

    private ActorAuditoria Construir(Guid? usuarioId)
    {
        if (usuarioId is null) return ActorAuditoria.SinResolver;

        // El plano 3 va primero porque es exclusivo: el token no admite las dos
        // vías a la vez —ClienteActivoSeleccionado descarta entero uno que
        // nombre operación y sesión— pero el orden lo deja escrito.
        //
        // Se lee del token, no de la base, por el mismo motivo que el resto de
        // este servicio: se audita en cada SaveChanges. Y no es "confiar en el
        // token": un acto que llega hasta aquí ya pasó por
        // RevalidacionClienteActivoMiddleware, que invalida la selección entera
        // cuando la sesión no vale, y por AutorizacionEscrituraBehavior, que
        // deniega toda escritura bajo sesión privilegiada revalidándola contra
        // la base en cada Command.
        //
        // El "actuando como" va en null y eso es correcto hoy, no un olvido:
        // ninguna sesión puede simular a nadie mientras no exista la fase de
        // impersonación, y con null el interceptor registra como autor al actor
        // real — lo conservador. Cuando esa fase llegue tendrá que traer el
        // usuario simulado hasta aquí, y tendrá que hacerlo sin consultar la
        // base (este servicio corre en cada SaveChanges): el sitio natural es un
        // quinto campo del token, con la sesión ya revalidada por el middleware.
        if (clienteActivoSeleccionado.SesionPrivilegiadaIdSeleccionada is { } sesionId)
            return new ActorAuditoria(usuarioId, null, TipoViaAcceso.SesionPrivilegiada, sesionId);

        // Operar un workspace delegado es una vía distinta de operar el
        // propio, y la auditoría del tenant visitado tiene derecho a
        // distinguirlas: es su dato el que se está tocando.
        if (clienteActivoSeleccionado.AsignacionOperacionIdSeleccionada is { } operacionId)
            return new ActorAuditoria(usuarioId, null, TipoViaAcceso.OperacionDelegada, operacionId);

        return ActorAuditoria.Normal(usuarioId);
    }
}
