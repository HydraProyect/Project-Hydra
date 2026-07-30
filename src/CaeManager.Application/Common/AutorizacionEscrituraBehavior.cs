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
/// Distingue Command de Query por convención de nombre (sufijo "Command",
/// exigido en todo el código — ver CODING_STANDARDS.md) en vez de una
/// interfaz marcador, para no tener que tocar los ~40 Command existentes.
/// Se registra antes que ValidationBehavior: un Command bloqueado por rol
/// ni siquiera llega a validarse.
/// </summary>
public class AutorizacionEscrituraBehavior<TRequest, TResponse>(ICurrentUserService currentUserService)
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
        if (!typeof(TRequest).Name.EndsWith("Command", StringComparison.Ordinal))
            return await next(cancellationToken);

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
