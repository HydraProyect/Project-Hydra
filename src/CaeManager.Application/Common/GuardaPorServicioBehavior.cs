using CaeManager.Domain.Common;
using MediatR;

namespace CaeManager.Application.Common;

/// <summary>
/// Guarda por servicio del pipeline (DN-21, punto 3, del mapa de flujos de Inbound; B-02b): impide escribir en el Tenant
/// equivocado cuando una misma persona tiene a la vez papel Outbound e Inbound. El papel es el <c>Servicio</c> de la
/// Asignación de Operación seleccionada (<see cref="ICurrentUserService.ObtenerPapelActivoAsync"/>), no un interruptor
/// aparte. Falla cerrado en los dos sentidos:
///
/// <list type="bullet">
/// <item>Un <see cref="IComandoInbound"/> solo pasa en el Tenant de origen o con papel <see cref="PapelActivo.Inbound"/>.
/// Con papel Outbound, por la vía heredada (DelegacionTenant solo es de Outbound) o sin papel, no.</item>
/// <item>Con papel <see cref="PapelActivo.Inbound"/>, un comando de escritura solo pasa si es <see cref="IComandoInbound"/>
/// o <see cref="IComandoDeAutoservicio"/> (lo que escribe es del propio usuario). Los comandos del Núcleo y de Outbound
/// no llevan marcador de servicio, así que en un contexto Inbound se niegan todos: el incremento que necesite uno lo
/// marca con su motivo.</item>
/// </list>
///
/// <para>
/// Solo mira comandos (<see cref="ICommandBase"/>): las lecturas las acotan la RLS, el filtro de Tenant y el alcance. No
/// sustituye a <see cref="AutorizacionEscrituraBehavior{TRequest,TResponse}"/> ni al alcance: responde a «¿es este el
/// papel de esta acción?», no a «¿puede escribir?» ni a «¿ve esta fila?». Una coordenada de contexto no es autoridad: el
/// papel sale de la operación revalidada en la base, nunca del request.
/// </para>
/// </summary>
public sealed class GuardaPorServicioBehavior<TRequest, TResponse>(ICurrentUserService currentUserService)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    public async Task<TResponse> Handle(
        TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        if (request is not ICommandBase)
            return await next(cancellationToken);

        var papel = await currentUserService.ObtenerPapelActivoAsync();

        if (request is IComandoInbound)
        {
            if (papel is PapelActivo.TenantDeOrigen or PapelActivo.Inbound)
                return await next(cancellationToken);

            return AutorizacionEscrituraBehavior<TRequest, TResponse>.CrearRespuestaFallo<TResponse>(Error.Crear(
                "Autorizacion.ComandoInboundFueraDePapel",
                papel == PapelActivo.Outbound
                    ? "Esta acción es de Inbound y estás trabajando en Outbound. Cambia al papel Inbound en el selector."
                    : "Esta acción es de Inbound y el contexto actual no tiene papel Inbound."));
        }

        if (papel == PapelActivo.Inbound && request is not IComandoDeAutoservicio)
            return AutorizacionEscrituraBehavior<TRequest, TResponse>.CrearRespuestaFallo<TResponse>(Error.Crear(
                "Autorizacion.ComandoFueraDelPapelInbound",
                "Estás trabajando en Inbound y esta acción no es de Inbound. Cambia de papel en el selector."));

        return await next(cancellationToken);
    }
}
