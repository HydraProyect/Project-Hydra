using CaeManager.Application.Plataforma;
using CaeManager.Domain.Common;
using MediatR;

namespace CaeManager.Application.Common;

/// <summary>
/// Pipeline behavior de MediatR: solo los roles con capacidad de escritura
/// ejecutan Commands (ver Roles.cs en CaeManager.Infrastructure.Identity).
/// Los literales de rol se repiten aquí a propósito: Application no puede
/// referenciar Infrastructure.Identity.Roles sin invertir la dependencia
/// entre capas.
///
/// <b>Lista blanca, no lista negra.</b> Antes enumeraba los roles de solo
/// lectura y dejaba pasar todo lo demás, incluido "sin rol": eso permitía
/// escribir a un usuario todavía sin rol asignado y, desde que
/// <c>ObtenerRolActualAsync</c> resuelve el rol de la delegación (hallazgo
/// N-5), habría dejado escribir a un operador cuya delegación acabara de
/// revocarse mientras su token de selección seguía vigente. Con lista blanca
/// ese caso falla cerrado, que es la única forma segura de equivocarse.
///
/// El rol que se comprueba es el <b>efectivo</b>: dentro de un Delegated
/// Workspace es el de la asignación, no el del claim de sesión (ADR-004
/// § 5.3).
///
/// Distingue Command de Query por la interfaz marcador <see cref="ICommand"/>,
/// no por el sufijo del nombre del tipo: con la convención de nombre, un typo
/// al declarar la clase desactivaba la autorización en silencio y ni el
/// compilador ni un test podían verlo. La convención de nombre sigue siendo
/// obligatoria (CODING_STANDARDS.md), pero ahora la sostiene
/// <c>ArquitecturaCommandsTests</c> — que exige nombre e interfaz en ambas
/// direcciones — en vez de ser ella misma el mecanismo de seguridad.
/// Se registra antes que ValidationBehavior: un Command bloqueado por rol
/// ni siquiera llega a validarse.
///
/// <b>Sesiones privilegiadas de plataforma (ADR-011 § 4bis): decisión
/// explícita, y antes que el rol.</b> Hoy una sesión de plano 3 acabaría con
/// rol efectivo <c>null</c> y la lista blanca la bloquearía igual — pero eso es
/// una consecuencia de cómo se resuelve el rol, no una decisión tomada aquí. Un
/// cambio futuro en <c>ObtenerRolActualAsync</c> que devolviera el claim de
/// sesión en algún caso convertiría a un técnico de soporte en escritor sin que
/// nada de este archivo hubiera cambiado. La regla del plano 3 —la inspección
/// de soporte es de solo lectura, sin excepción implícita— se escribe aquí y se
/// prueba aquí.
///
/// <b>PD-A3 abre la única excepción, y con tres condiciones a la vez, no una.</b>
/// Una sesión con <c>TieneCaminoDeEscritura</c> (hoy solo <c>Aprovisionamiento</c>
/// — <c>BreakGlass</c> permite escribir en el modelo pero su fase todavía no
/// existe) deja pasar el comando solo si además es
/// <see cref="IComandoDeAprovisionamiento"/> Y el tenant actual coincide con el
/// <c>TenantObjetivoId</c> de la sesión. Cualquiera de las tres que falle
/// deniega, con un código de error distinto por condición — mismo principio de
/// "cada precondición prueba su propia desaparición" que el resto de este
/// archivo. Este behavior NO abre el rol de escritura de PostgreSQL: eso es
/// <c>ElevacionEscrituraAprovisionamientoBehavior</c>, registrado más adentro
/// del pipeline, para que la ventana de rol elevado sea solo el handler.
///
/// Y cubre el hueco del circuito, que la revalidación por petición no puede
/// cubrir: <c>RevalidacionClienteActivoMiddleware</c> solo corre en peticiones
/// HTTP, y un circuito de Blazor ya establecido puede seguir interactuando por
/// SignalR sin generar ninguna. Aquí la decisión no depende de que esa
/// revalidación haya llegado a correr — y no depende tampoco de que la sesión
/// resuelva: si resuelve, se decide por vía de acceso y capacidad; si no
/// resuelve, el rol efectivo de un contexto privilegiado es <c>null</c> y se
/// deniega por lista blanca.
///
/// <b>Autoservicio: la única salida de la lista de escritura, y solo del rol.</b>
/// Un <see cref="IComandoDeAutoservicio"/> escribe solo datos del propio usuario
/// (aceptar sus términos, sus filtros, sus preferencias, sus notificaciones), y
/// pasa con cualquier rol reconocido, incluidos Consulta y Cliente. Sin esto, un
/// usuario Consulta que aún no hubiera aceptado los términos quedaba atascado
/// detrás de <c>AceptacionTerminosGate</c>, que es bloqueante. La exención va
/// después de la rama de sesión privilegiada —el soporte de plataforma no
/// escribe ni lo suyo— y no alcanza a un rol nulo o desconocido: la lista blanca
/// sigue mandando, lo que cambia es que no exige un rol de escritura.
///
/// <b>La escritura sí exige inmediatez (REC-067, DEC-44).</b> La resolución de
/// sesión se memoiza por ámbito de DI —petición en HTTP, circuito entero en
/// Blazor Server— igual que el resto de la resolución de alcance, así que un
/// simple <c>ObtenerAsync</c> podría no ver una concesión revocada a mitad de
/// circuito. Por eso este behavior consulta
/// <see cref="ISesionPrivilegiadaActual.RevalidarAsync"/>, que ignora esa memo
/// y vuelve a preguntar a la base en cada comando: la garantía crítica de
/// mutación se impone en el punto de mutación, sin depender de que el
/// circuito se cierre ni de que llegue la próxima petición HTTP. Esa
/// limitación de <c>ObtenerAsync</c> sigue viva y sigue siendo aceptable —para
/// <i>lectura</i>—, y quien la cierra de verdad es el enforcement en la capa
/// de datos (rol de BD de solo lectura + RLS, ADR-011 § 4bis.7.4).
///
/// Coste para el resto del mundo: cero consultas. La sesión privilegiada, la
/// concesión que la ampara y su alcance se leen juntos en una sola consulta
/// (más una segunda si la concesión no es de alcance global) — y
/// <c>RevalidarAsync</c> descarta esa consulta sin tocar la base en cuanto el
/// token no trae ninguna sesión, que es el caso de absolutamente todos los
/// usuarios hoy. Solo una sesión de plano 3 —soporte o administración de
/// plataforma, por definición minoritarias y deliberadas— paga una consulta
/// extra por comando de escritura.
/// </summary>
public class AutorizacionEscrituraBehavior<TRequest, TResponse>(
    ICurrentUserService currentUserService,
    ISesionPrivilegiadaActual sesionPrivilegiadaActual,
    ITenantActual tenantActual)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    // Consulta y Cliente quedan fuera por ser de solo lectura; cualquier otro
    // valor —incluido null— tampoco escribe.
    private static readonly string[] RolesConEscritura =
        ["Administrador", "DireccionCae", "CoordinadorCae", "GestorCae"];

    // Todos los roles que existen (Roles.Todos): un IComandoDeAutoservicio pasa con
    // cualquiera de ellos; null o un valor desconocido siguen sin escribir.
    private static readonly string[] RolesReconocidos =
        [.. RolesConEscritura, "Consulta", "Cliente"];

    public async Task<TResponse> Handle(
        TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        if (request is not ICommandBase)
            return await next(cancellationToken);

        if (await sesionPrivilegiadaActual.RevalidarAsync(cancellationToken) is { } sesion)
        {
            // PD-A3: una sesión con camino de escritura construido (hoy solo
            // Aprovisionamiento — ver TieneCaminoDeEscritura) puede seguir
            // adelante, pero solo si las tres condiciones se cumplen A LA VEZ.
            // No abre el ámbito de elevación aquí: eso lo hace
            // ElevacionEscrituraAprovisionamientoBehavior, registrado más
            // adentro del pipeline, para que la ventana de rol elevado sea
            // solo el handler.
            if (!sesion.TieneCaminoDeEscritura)
                return CrearRespuestaFallo<TResponse>(ErrorDeSesionPrivilegiada(sesion));

            if (request is not IComandoDeAprovisionamiento)
                return CrearRespuestaFallo<TResponse>(Error.Crear(
                    "Autorizacion.ComandoFueraDelAprovisionamiento",
                    "Esta sesión de aprovisionamiento no habilita esta operación."));

            if (tenantActual.TenantId != sesion.TenantObjetivoId)
                return CrearRespuestaFallo<TResponse>(Error.Crear(
                    "Autorizacion.AprovisionamientoFueraDelTenantObjetivo",
                    "Esta sesión de aprovisionamiento solo puede escribir en el tenant objetivo."));

            return await next(cancellationToken);
        }

        var rol = await currentUserService.ObtenerRolActualAsync();

        var rolesAdmitidos = request is IComandoDeAutoservicio ? RolesReconocidos : RolesConEscritura;

        if (rol is null || !rolesAdmitidos.Contains(rol))
        {
            var error = Error.Crear(
                "Autorizacion.SoloLectura",
                "Tu rol no permite crear, editar ni eliminar datos — solo consultarlos.");

            return CrearRespuestaFallo<TResponse>(error);
        }

        return await next(cancellationToken);
    }

    /// <summary>
    /// Dos motivos distintos, los dos deniegan — y la diferencia importa
    /// porque una es permanente y la otra es una fase que falta.
    ///
    /// <c>SoporteLectura</c> y <c>AdminPlataforma</c> no escriben nunca, y esa
    /// es una regla permanente: la inspección de soporte es de solo lectura sin
    /// excepción implícita, y administrar la plataforma no es tocar los datos de
    /// un cliente.
    ///
    /// <c>Impersonacion</c> y <c>BreakGlass</c> caen hoy en el mismo <c>no</c>,
    /// pero por falta de fase, no por regla. La impersonación se autoriza con
    /// los planos 1 y 2 <b>del usuario simulado</b> —ver ADR-011 § 4bis.2— y ese
    /// camino no existe todavía; el break-glass exige lo que le da sentido
    /// (motivo, ventana acotada, traza íntegra, revisión posterior obligatoria)
    /// y tampoco. Mientras no existan, denegar es la única respuesta correcta;
    /// cuando existan, este es el sitio donde se abren, cada uno con su revisión.
    /// </summary>
    private static Error ErrorDeSesionPrivilegiada(SesionPrivilegiadaActiva sesion) =>
        sesion.PermiteEscritura
            ? Error.Crear(
                "Autorizacion.BreakGlassSinCaminoDeEscritura",
                "El acceso break-glass todavía no tiene camino de escritura habilitado.")
            : Error.Crear(
                "Autorizacion.SesionPrivilegiadaSoloLectura",
                "Un acceso de soporte de plataforma es de solo lectura: no puede crear, editar ni eliminar datos.");

    /// <summary>
    /// Los Command devuelven Result o Result&lt;T&gt; por convención (ver
    /// CODING_STANDARDS.md) — se construye el fallo genéricamente por
    /// reflexión para no acoplar este behavior a cada tipo de respuesta.
    /// </summary>
    private static TResponse CrearRespuestaFallo<T>(Error error)
    {
        var tipoRespuesta = typeof(T);

        if (tipoRespuesta == typeof(Result))
            return (TResponse)(object)Result.Fallo(error);

        if (tipoRespuesta.IsGenericType && tipoRespuesta.GetGenericTypeDefinition() == typeof(Result<>))
        {
            var metodoFallo = typeof(Result)
                .GetMethod(nameof(Result.Fallo), 1, [typeof(Error)])!
                .MakeGenericMethod(tipoRespuesta.GetGenericArguments()[0]);

            return (TResponse)metodoFallo.Invoke(null, [error])!;
        }

        throw new InvalidOperationException(
            $"AutorizacionEscrituraBehavior solo sabe construir un fallo para Result o Result<T>, no para {tipoRespuesta.Name}.");
    }
}
