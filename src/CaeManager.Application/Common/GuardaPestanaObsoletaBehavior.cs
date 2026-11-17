using CaeManager.Application.Auditoria;
using CaeManager.Domain.Common;
using MediatR;

namespace CaeManager.Application.Common;

/// <summary>
/// Rechaza todo comando de un ámbito cuya selección de contexto ya no es la vigente (<see cref="IGuardaSeleccionVigente"/>)
/// y deja constancia del rechazo. Un solo punto para todos los caminos de escritura que pasan por MediatR: los
/// circuitos de Blazor y los endpoints HTTP que envían un comando. Va antes que
/// <see cref="AutorizacionEscrituraBehavior{TRequest,TResponse}"/>: un comando de una pestaña obsoleta no llega a
/// tocar datos, ni siquiera para autorizarse.
/// </summary>
public class GuardaPestanaObsoletaBehavior<TRequest, TResponse>(
    IGuardaSeleccionVigente guarda,
    IRegistroCambioContexto registroCambioContexto)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    public const string CodigoError = "Contexto.SeleccionObsoleta";

    public const string MensajeCambiada = "La Empresa gestionada activa cambió en otra pestaña. Vuelve a cargar la página.";

    public const string MensajeRetirada = "La Empresa gestionada activa ya no está disponible. Vuelve a cargar la página.";

    public async Task<TResponse> Handle(
        TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        if (request is not ICommandBase || guarda.Comprobar() is not { } motivo)
            return await next(cancellationToken);

        await registroCambioContexto.RegistrarRechazoAsync(typeof(TRequest).Name, motivo, cancellationToken);

        return AutorizacionEscrituraBehavior<TRequest, TResponse>.CrearRespuestaFallo<TResponse>(Error.Crear(
            CodigoError, motivo == SeleccionNoVigente.RetiradaTrasPintar ? MensajeRetirada : MensajeCambiada));
    }
}
