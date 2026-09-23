using MediatR;

namespace CaeManager.Application.Common;

/// <summary>
/// Descarta el alcance memoizado del scope actual (<see cref="IInvalidadorAlcance"/>) antes y
/// después de cada Command (<see cref="ICommandBase"/>). Las Queries no lo tocan.
///
/// <para>
/// <b>Antes</b>: la escritura exige inmediatez (mismo criterio que REC-067/DEC-44 en
/// <see cref="AutorizacionEscrituraBehavior{TRequest,TResponse}"/>). Los handlers de Command
/// autorizan con <see cref="IAlcanceDatosService"/>, y en Blazor Server el alcance memoizado es el
/// del circuito: sin invalidar antes, un Gestor CAE cuya Asignación de Cartera se cerró desde otro
/// circuito seguía escribiendo sobre ese ámbito con la visión anterior. Este behavior va por fuera
/// de la autorización de escritura y del handler, así que ambos resuelven el alcance de nuevo.
/// </para>
///
/// <para>
/// <b>Después</b>: para que la siguiente lectura en el mismo circuito vea el alcance ya
/// actualizado por la propia escritura. Invalida también si el handler falla: un guardado parcial
/// o un fallo de concurrencia tampoco deben dejar una visión que ya no se sabe si es cierta.
/// </para>
///
/// Va justo dentro de <see cref="SerializacionAccesoDatosBehavior{TRequest,TResponse}"/>: las dos
/// invalidaciones ocurren mientras la puerta de acceso a datos sigue tomada, así que ninguna
/// lectura concurrente puede volver a memoizar el alcance viejo entre medias.
///
/// Límite conocido: solo cubre escrituras que pasan por MediatR. Una escritura directa sobre el
/// DbContext desde un componente, o un seeder, no invalida. Lo que se revoca desde otro circuito y
/// solo afecta a la LECTURA lo acota la caducidad de la memoización (<c>CaducidadAlcanceOptions</c>).
/// </summary>
public class InvalidacionAlcanceBehavior<TRequest, TResponse>(IInvalidadorAlcance invalidador)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    public async Task<TResponse> Handle(
        TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        if (request is not ICommandBase) return await next(cancellationToken);

        invalidador.Invalidar();
        try
        {
            return await next(cancellationToken);
        }
        finally
        {
            invalidador.Invalidar();
        }
    }
}
