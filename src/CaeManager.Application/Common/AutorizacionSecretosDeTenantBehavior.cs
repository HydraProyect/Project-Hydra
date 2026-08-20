using CaeManager.Application.Plataforma;
using MediatR;

namespace CaeManager.Application.Common;

/// <summary>
/// Corta el acceso a los secretos del tenant durante una sesión privilegiada de
/// plataforma, sea cual sea su capacidad.
///
/// Complementa a <see cref="AutorizacionEscrituraBehavior{TRequest,TResponse}"/>
/// —que cubre "¿operación permitida?"— con el escalón que le falta a
/// "SoporteLectura ve el tenant entero": <b>¿recurso permitido?</b>. Sin él,
/// dar acceso de inspección a un tenant equivaldría a entregar las contraseñas
/// de sus portales externos, que es autoridad sobre sistemas de terceros y
/// sobrevive al cierre de la sesión (ver <see cref="IConsultaDeSecretosDeTenant"/>).
///
/// Se aplica también a <c>BreakGlass</c> y a <c>AdminPlataforma</c>: ninguna de
/// las cuatro capacidades del plano 3 incluye llevarse credenciales ajenas, y
/// hacer una excepción "porque break-glass puede todo" sería justo la escalada
/// que la matriz por capacidades existe para evitar.
///
/// <b>Deniega devolviendo el mismo valor que "no hay credencial guardada"</b>
/// (<c>null</c>), en vez de lanzar: la pantalla ya sabe representar ese caso, y
/// así la respuesta tampoco delata si el cliente tiene o no credenciales
/// configuradas. Para los usuarios de negocio no cambia nada — sin sesión
/// privilegiada el behavior no consulta ni la base de datos.
/// </summary>
public class AutorizacionSecretosDeTenantBehavior<TRequest, TResponse>(
    ISesionPrivilegiadaActual sesionPrivilegiadaActual)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    public async Task<TResponse> Handle(
        TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        if (request is not IConsultaDeSecretosDeTenant)
            return await next(cancellationToken);

        if (await sesionPrivilegiadaActual.ObtenerAsync(cancellationToken) is null)
            return await next(cancellationToken);

        if (typeof(TResponse).IsValueType)
            throw new InvalidOperationException(
                $"{typeof(TRequest).Name} está marcada como IConsultaDeSecretosDeTenant pero devuelve " +
                $"{typeof(TResponse).Name}, un tipo por valor: no hay forma de denegar sin inventar un valor. " +
                "Una consulta de secretos debe devolver un tipo por referencia anulable.");

        return default!;
    }
}
