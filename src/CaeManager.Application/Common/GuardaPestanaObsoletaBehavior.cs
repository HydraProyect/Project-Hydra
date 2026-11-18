using CaeManager.Application.Auditoria;
using CaeManager.Domain.Common;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Application.Common;

/// <summary>
/// Rechaza todo comando de un ámbito cuya selección de contexto ya no es la vigente (<see cref="IGuardaSeleccionVigente"/>)
/// y deja constancia del rechazo. Un solo punto para todos los caminos de escritura que pasan por MediatR: los
/// circuitos de Blazor y los endpoints HTTP que envían un comando. En un circuito detecta la pestaña obsoleta; en una
/// petición HTTP, no: su versión sale de la misma cookie que el registro refleja, así que un formulario pintado con
/// otro Tenant que se envía con la cookie nueva pasa (sí rechaza la selección retirada a mitad de petición). Para eso el comando
/// tiene que llevar la versión con la que se pintó (propuesta § 5.4), y queda para R-5. Va antes que
/// <see cref="AutorizacionEscrituraBehavior{TRequest,TResponse}"/>: un comando de una pestaña obsoleta no llega a
/// tocar datos, ni siquiera para autorizarse.
/// </summary>
/// <remarks>
/// MediatR construye todos los behaviors para cualquier petición, Queries incluidas, y no todo contenedor que monta
/// <c>AddApplication()</c> registra <see cref="IActorAuditoria"/> (lo registra Web) ni el repositorio de auditoría (lo
/// registra Infrastructure). Por eso <see cref="IRegistroCambioContexto"/> se resuelve del proveedor del mismo ámbito solo
/// al rechazar, como <c>DuplicadosEmpresaBehavior</c>, y no en el constructor: con <see cref="SinGuardaDeSeleccion"/>
/// nunca se rechaza y nunca se pide, y un host con guarda real que no componga la auditoría falla al rechazar, con el
/// comando sin ejecutar, en vez de rechazar sin dejar rastro.
/// </remarks>
public class GuardaPestanaObsoletaBehavior<TRequest, TResponse>(
    IGuardaSeleccionVigente guarda,
    IServiceProvider proveedor)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    public const string CodigoError = "Contexto.SeleccionObsoleta";

    // Sin «Empresa gestionada»: el rechazo sale también dentro de una Sesión Privilegiada, donde Soporte TALVEG no es
    // Gestor CAE, y en la vuelta al Tenant de origen. Sin afirmar que haya otra pestaña: con una sola, una navegación
    // mejorada que conserva el circuito también cambia la selección vigente (renovación o Tenant por defecto).
    public const string MensajeCambiada = "El contexto activo cambió, quizá en otra pestaña. Vuelve a cargar la página.";

    public const string MensajeRetirada = "El contexto activo ya no está disponible. Vuelve a cargar la página.";

    public async Task<TResponse> Handle(
        TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        if (request is not ICommandBase || guarda.Comprobar() is not { } motivo)
            return await next(cancellationToken);

        var registroCambioContexto = proveedor.GetRequiredService<IRegistroCambioContexto>();
        await registroCambioContexto.RegistrarRechazoAsync(typeof(TRequest).Name, motivo, cancellationToken);

        return AutorizacionEscrituraBehavior<TRequest, TResponse>.CrearRespuestaFallo<TResponse>(Error.Crear(
            CodigoError, motivo == SeleccionNoVigente.RetiradaTrasPintar ? MensajeRetirada : MensajeCambiada));
    }
}
