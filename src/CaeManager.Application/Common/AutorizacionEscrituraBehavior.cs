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
/// <b>Sesiones privilegiadas de plataforma (ADR-011 § 4bis): denegación
/// explícita, y antes que el rol.</b> Hoy una sesión de plano 3 acabaría con
/// rol efectivo <c>null</c> y la lista blanca la bloquearía igual — pero eso es
/// una consecuencia de cómo se resuelve el rol, no una decisión tomada aquí. Un
/// cambio futuro en <c>ObtenerRolActualAsync</c> que devolviera el claim de
/// sesión en algún caso convertiría a un técnico de soporte en escritor sin que
/// nada de este archivo hubiera cambiado. La regla del plano 3 —la inspección
/// de soporte es de solo lectura, sin excepción implícita— se escribe aquí y se
/// prueba aquí.
///
/// Y cubre el hueco del circuito, que la revalidación por petición no puede
/// cubrir: <c>RevalidacionClienteActivoMiddleware</c> solo corre en peticiones
/// HTTP, y un circuito de Blazor ya establecido puede seguir interactuando por
/// SignalR sin generar ninguna. Aquí la denegación no depende de que esa
/// revalidación haya llegado a correr — y no depende tampoco de que la sesión
/// resuelva: si resuelve, se deniega por vía de acceso; si no resuelve, el rol
/// efectivo de un contexto privilegiado es <c>null</c> y se deniega por lista
/// blanca. Las dos ramas acaban en no.
///
/// Lo que este behavior <b>no</b> puede prometer es inmediatez en la
/// <i>lectura</i>: la resolución de la sesión se memoiza por ámbito de DI
/// —petición en HTTP, circuito entero en Blazor Server— igual que el resto de
/// la resolución de alcance, así que revocar una concesión a mitad de circuito
/// no vacía lo que ese circuito ya está viendo. Es la misma limitación que
/// documenta el middleware, y quien la cierra de verdad es el enforcement en la
/// capa de datos (rol de BD de solo lectura + RLS, ADR-011 § 4bis.7.4), que es
/// la fase siguiente.
///
/// Coste para el resto del mundo: cero consultas.
/// <c>ISesionPrivilegiadaActual</c> devuelve <c>null</c> sin tocar la base
/// cuando la sesión no trae ninguna, que es el caso de absolutamente todos los
/// usuarios hoy.
/// </summary>
public class AutorizacionEscrituraBehavior<TRequest, TResponse>(
    ICurrentUserService currentUserService,
    ISesionPrivilegiadaActual sesionPrivilegiadaActual)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    // Consulta y Cliente quedan fuera por ser de solo lectura; cualquier otro
    // valor —incluido null— tampoco escribe.
    private static readonly string[] RolesConEscritura =
        ["Administrador", "DireccionCae", "CoordinadorCae", "GestorCae"];

    public async Task<TResponse> Handle(
        TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        if (request is not ICommandBase)
            return await next(cancellationToken);

        if (await sesionPrivilegiadaActual.ObtenerAsync(cancellationToken) is { } sesion)
            return CrearRespuestaFallo<TResponse>(ErrorDeSesionPrivilegiada(sesion));

        var rol = await currentUserService.ObtenerRolActualAsync();

        if (rol is null || !RolesConEscritura.Contains(rol))
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
