using CaeManager.Application.Tenants.Encargo;
using CaeManager.Domain.Common;
using MediatR;

namespace CaeManager.Application.Common;

/// <summary>
/// Pipeline behavior de MediatR: niega a quien administra por Encargo de
/// administración (decisión D-8, 2026-10-08) las peticiones —comandos y
/// consultas— de la lista cerrada <see cref="ActosExcluidosDelEncargo"/>.
///
/// <para>
/// Es una <b>restricción</b>, no una autorización: solo actúa sobre quien tiene
/// el rol elevado por un encargo (<see cref="IEncargoDeAdministracionActual"/>),
/// y solo para negar. Un Administrador propio del Tenant propietario, un rol de
/// cartera sin encargo, una Sesión Privilegiada y un servicio de fondo pasan
/// sin tocarse; lo que cada uno puede hacer lo siguen decidiendo los behaviors
/// y handlers de siempre.
/// </para>
///
/// <para>
/// Hace falta en Application y no basta con las puertas de página: los
/// handlers de estas áreas no comprueban el rol, así que su única barrera era
/// una ruta que mira el rol ya elevado.
/// </para>
/// </summary>
public class ExclusionesDelEncargoBehavior<TRequest, TResponse>(IEncargoDeAdministracionActual encargoActual)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    public async Task<TResponse> Handle(
        TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        // Primero la lista, que no cuesta nada: la inmensa mayoría de las peticiones no están en ella
        // y no deben pagar una resolución del rol efectivo.
        if (!ActosExcluidosDelEncargo.Excluye(request))
            return await next(cancellationToken);

        if (await encargoActual.EncargoQueElevaAsync() is null)
            return await next(cancellationToken);

        return CrearRespuestaFallo(ErroresEncargoAdministracion.ActoExcluido, request.GetType().Name);
    }

    /// <summary>
    /// Mismo mecanismo que <c>AutorizacionEscrituraBehavior</c> para los
    /// comandos. Una consulta no devuelve <c>Result</c>: se interrumpe con
    /// <see cref="ActoExcluidoDelEncargoException"/> antes que devolver datos.
    /// </summary>
    private static TResponse CrearRespuestaFallo(Error error, string nombrePeticion)
    {
        var tipoRespuesta = typeof(TResponse);

        if (tipoRespuesta == typeof(Result))
            return (TResponse)(object)Result.Fallo(error);

        if (tipoRespuesta.IsGenericType && tipoRespuesta.GetGenericTypeDefinition() == typeof(Result<>))
        {
            var metodoFallo = typeof(Result)
                .GetMethod(nameof(Result.Fallo), 1, [typeof(Error)])!
                .MakeGenericMethod(tipoRespuesta.GetGenericArguments()[0]);

            return (TResponse)metodoFallo.Invoke(null, [error])!;
        }

        throw new ActoExcluidoDelEncargoException(nombrePeticion);
    }
}
