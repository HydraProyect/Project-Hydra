using CaeManager.Application.Comercial.LineaOutbound;
using CaeManager.Domain.Tenants;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Application.Common;

/// <summary>
/// Guarda de D3 en el pipeline: un Command de <see cref="RegistroLimiteTenantTec.CerradasSinLinea"/> (o uno
/// <see cref="ILimiteTenantTecSegunDatos"/> que lo pida) no pasa si Outbound está cerrado para el Tenant beneficiario
/// (<see cref="IOutboundCerradoTenantTec"/>: marcado como participante TEC y sin línea Outbound vigente). Va justo después de
/// <see cref="GuardaPorServicioBehavior{TRequest,TResponse}"/> y aparte de ella: responde a «¿tiene este Tenant contratado
/// lo que se pide?», no a «¿es este el papel?» ni a «¿puede escribir este actor?».
///
/// <para>
/// El Tenant beneficiario es el Tenant activo (<see cref="ITenantActual"/>) y, en un <see cref="IComandoSobreAsignacionDeOperacion"/>,
/// también el Tenant propietario de esa Asignación de Operación. No mira al actor (DN-6): con Sesión Privilegiada, por un Operador CAE
/// externo o por un Encargo de administración, se aplica igual; por eso este behavior no depende de quién es el usuario.
/// </para>
///
/// <para>
/// Falla cerrado: sin Tenant activo, un Command cerrado se deniega; una excepción al leer se propaga. Los Commands que no
/// están en el registro no leen la base. Corre antes de <see cref="ElevacionEscrituraAprovisionamientoBehavior{TRequest,TResponse}"/>,
/// así que nunca lee bajo <c>cae_app_aprovisionamiento</c>. Un Command anidado (el plan del Asistente IA que envía un
/// <c>CrearCentroCommand</c>) vuelve a pasar por aquí.
/// </para>
///
/// <para>
/// Sus dependencias de lectura se resuelven solo cuando llega un Command cerrado: el behavior se construye para cada
/// petición de cada contenedor que llama a <c>AddApplication()</c>, y la mayoría no las necesita. Si un contenedor no las
/// registra, el Command cerrado falla al resolverlas (cerrado), no pasa.
/// </para>
/// </summary>
public sealed class LimiteTenantTecBehavior<TRequest, TResponse>(ITenantActual tenantActual, IServiceProvider servicios)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    public async Task<TResponse> Handle(
        TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        if (request is not ICommandBase)
            return await next(cancellationToken);

        var cerrado = RegistroLimiteTenantTec.CierraSinLinea(request.GetType())
            || request is ILimiteTenantTecSegunDatos { AbreLoCerradoAlTenantTec: true };
        if (!cerrado)
            return await next(cancellationToken);

        if (tenantActual.TenantId is not { } tenantId)
            return Denegar();

        var outboundCerrado = servicios.GetRequiredService<IOutboundCerradoTenantTec>();
        if (await outboundCerrado.EstaCerradoAsync(tenantId, cancellationToken))
            return Denegar();

        // TenantClienteId es el nombre legacy del Tenant propietario de la Asignación de Operación (deuda terminológica).
        if (request is IComandoSobreAsignacionDeOperacion sobreAsignacion
            && await servicios.GetRequiredService<IDelegacionTenantRepository>()
                .ObtenerPorIdAsync(sobreAsignacion.AsignacionDeOperacionId, cancellationToken)
                is { TenantClienteId: var tenantPropietarioDeLaAsignacion }
            && tenantPropietarioDeLaAsignacion != tenantId
            && await outboundCerrado.EstaCerradoAsync(tenantPropietarioDeLaAsignacion, cancellationToken))
            return Denegar();

        return await next(cancellationToken);
    }

    private static TResponse Denegar() =>
        AutorizacionEscrituraBehavior<TRequest, TResponse>.CrearRespuestaFallo<TResponse>(ErroresLimiteTenantTec.OutboundCerrado);
}
