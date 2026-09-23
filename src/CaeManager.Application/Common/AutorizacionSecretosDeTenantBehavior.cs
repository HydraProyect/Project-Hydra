using CaeManager.Application.Plataforma;
using MediatR;

namespace CaeManager.Application.Common;

/// <summary>
/// Corta el acceso a los secretos del tenant durante una sesión privilegiada de
/// plataforma, sea cual sea su capacidad, y a cualquier usuario de negocio
/// cuyo rol no escriba.
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
/// configuradas.
///
/// <para>
/// <b>Usuarios de negocio: solo los roles con escritura</b> (decisión del
/// propietario, 2026-09-23, opción A). Consulta es un rol de solo lectura, y
/// una credencial de una plataforma CAE no es un dato que se mira: es la llave
/// para actuar en esa plataforma en nombre del Tenant propietario, fuera de
/// TALVEG. Vale igual para la Consulta delegada del Operador CAE externo: el rol
/// que se evalúa es el efectivo del workspace delegado, que ya resuelve
/// <see cref="ICurrentUserService.ObtenerRolActualAsync"/>. Cliente (usuario de
/// portal) y un rol sin resolver tampoco leen: fallo cerrado. Es la misma lista
/// que <see cref="AutorizacionEscrituraBehavior{TRequest,TResponse}"/>, repetida
/// con literales porque Application no referencia Infrastructure.Identity; la
/// paridad con <c>Roles.ConEscrituraCsv</c> —la que usa <c>SoloConEscritura</c>
/// para ocultar los botones de copiar— la vigila <c>RolesConEscrituraParidadTests</c>.
/// El alcance de gestión de cada consulta (qué Centro, Empresa o Subcontrata)
/// sigue decidiéndose en su handler; esto solo decide qué rol puede pedir un
/// secreto.
/// </para>
///
/// <para>
/// Las consultas marcadas con <see cref="IConsultaDeDatosDeCredencial"/> (el usuario
/// de una credencial, sin contraseña) siguen la misma regla de roles,
/// pero no la de la sesión privilegiada: ver el porqué en esa interfaz.
/// </para>
/// </summary>
public class AutorizacionSecretosDeTenantBehavior<TRequest, TResponse>(
    ISesionPrivilegiadaActual sesionPrivilegiadaActual,
    ICurrentUserService currentUserService)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    private static readonly string[] RolesQueLeenSecretos =
        ["Administrador", "DireccionCae", "CoordinadorCae", "GestorCae"];

    public async Task<TResponse> Handle(
        TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        var esSecreto = request is IConsultaDeSecretosDeTenant;
        if (!esSecreto && request is not IConsultaDeDatosDeCredencial)
            return await next(cancellationToken);

        var sinSesionQueLoImpida = !esSecreto
            || await sesionPrivilegiadaActual.ObtenerAsync(cancellationToken) is null;

        if (sinSesionQueLoImpida
            && await currentUserService.ObtenerRolActualAsync() is { } rol
            && RolesQueLeenSecretos.Contains(rol))
            return await next(cancellationToken);

        if (typeof(TResponse).IsValueType)
            throw new InvalidOperationException(
                $"{typeof(TRequest).Name} está marcada como consulta de credenciales pero devuelve " +
                $"{typeof(TResponse).Name}, un tipo por valor: no hay forma de denegar sin inventar un valor. " +
                "Una consulta de credenciales debe devolver un tipo por referencia anulable.");

        return default!;
    }
}
