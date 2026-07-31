using CaeManager.Domain.Common;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Common;

/// <summary>
/// Traduce el choque de concurrencia optimista
/// (<see cref="DbUpdateConcurrencyException"/>, ver
/// <c>EntidadBase.Version</c>) en un <see cref="Result"/> de fallo con
/// microcopy en español, en vez de dejar que reviente como error no
/// controlado.
///
/// Se hace aquí y no en cada handler porque el conflicto puede saltar en
/// cualquier <c>SaveChangesAsync</c> del sistema: dejarlo a criterio de cada
/// Command garantizaría que la mayoría no lo trata, que es como estaba antes
/// de que existiera el token — solo que antes el choque no se detectaba
/// siquiera y el último en guardar pisaba al primero en silencio.
///
/// Va por fuera de <c>ValidationBehavior</c> y de
/// <c>AutorizacionEscrituraBehavior</c>: la excepción nace dentro del handler,
/// así que este behavior tiene que envolverlos a ambos.
/// </summary>
public class ConcurrenciaBehavior<TRequest, TResponse>
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    public async Task<TResponse> Handle(
        TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        try
        {
            return await next(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            var error = Error.Crear(
                "Concurrencia.Conflicto",
                "Otra persona modificó este registro mientras lo editabas. Vuelve a abrirlo para ver los cambios y aplica los tuyos de nuevo.");

            // Una Query no guarda nada, así que si llega aquí es un caso que
            // este behavior no sabe representar: mejor propagarlo que
            // devolver un Result que el llamador no espera.
            if (!EsRespuestaDeResultado(typeof(TResponse)))
                throw;

            return CrearRespuestaFallo<TResponse>(error);
        }
    }

    private static bool EsRespuestaDeResultado(Type tipoRespuesta) =>
        tipoRespuesta == typeof(Result) ||
        (tipoRespuesta.IsGenericType && tipoRespuesta.GetGenericTypeDefinition() == typeof(Result<>));

    /// <summary>
    /// Mismo mecanismo que <c>AutorizacionEscrituraBehavior</c>: los Command
    /// devuelven Result o Result&lt;T&gt; por convención y el fallo se
    /// construye por reflexión para no acoplar el behavior a cada tipo.
    /// </summary>
    private static TResponse CrearRespuestaFallo<T>(Error error)
    {
        var tipoRespuesta = typeof(T);

        if (tipoRespuesta == typeof(Result))
            return (TResponse)(object)Result.Fallo(error);

        var metodoFallo = typeof(Result)
            .GetMethod(nameof(Result.Fallo), 1, [typeof(Error)])!
            .MakeGenericMethod(tipoRespuesta.GetGenericArguments()[0]);

        return (TResponse)metodoFallo.Invoke(null, [error])!;
    }
}
