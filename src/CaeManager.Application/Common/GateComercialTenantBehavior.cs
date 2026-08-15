using CaeManager.Application.Tenants;
using CaeManager.Domain.Common;
using CaeManager.Domain.Tenants;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Common;

/// <summary>
/// Pipeline behavior de MediatR: bloquea Commands sobre un tenant cuyo
/// estado comercial ya no lo permite (plan de negocio § 1.7 — el gate
/// "aviso → solo lectura → suspendido"). Deliberadamente aparte de
/// <see cref="AutorizacionEscrituraBehavior{TRequest,TResponse}"/>, que
/// decide por ROL; este decide por el estado del TENANT — un Administrador
/// con permiso de sobra puede seguir bloqueado aquí si su tenant dejó de
/// pagar.
///
/// <see cref="EstadoComercialTenant.SinSuscripcion"/> y
/// <see cref="EstadoComercialTenant.AvisoImpago"/> NO bloquean — el gate
/// real empieza en <see cref="EstadoComercialTenant.SoloLectura"/> (plan
/// § 1.7: "aviso" es una etapa de advertencia, no de bloqueo; "solo lectura"
/// es la primera que sí restringe escritura). Sin este comportamiento, todo
/// tenant ya desplegado antes de este Horizonte —en SinSuscripcion, el valor
/// por defecto— se habría bloqueado de golpe al desplegar esta feature.
///
/// El tenant de plataforma queda exento siempre: nunca es él mismo un
/// suscriptor (ver TenantConfiguration.HasData), y bloquearlo dejaría a
/// Christopher sin forma de arreglar el estado comercial de nadie.
///
/// <see cref="ITenantActual.TenantId"/> null (sin tenant resuelto — trabajos
/// en segundo plano, seeds, etc.) no bloquea: este gate es para operación
/// interactiva de producto, no para tareas internas del sistema, que no
/// pasan por un tenant de sesión.
/// </summary>
public class GateComercialTenantBehavior<TRequest, TResponse>(ITenantActual tenantActual, ITenantsQueryContext dbContext)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    public async Task<TResponse> Handle(
        TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        if (request is not ICommandBase)
            return await next(cancellationToken);

        if (tenantActual.TenantId is not { } tenantId)
            return await next(cancellationToken);

        var tenant = await dbContext.Tenants
            .Where(t => t.Id == tenantId)
            .Select(t => new { t.EsPlataforma, t.EstadoComercial })
            .SingleOrDefaultAsync(cancellationToken);

        if (tenant is null || tenant.EsPlataforma)
            return await next(cancellationToken);

        var error = tenant.EstadoComercial switch
        {
            EstadoComercialTenant.Suspendida => Error.Crear(
                "Comercial.Suspendido",
                "Esta cuenta está suspendida por impago. Contacta con nosotros para reactivarla."),
            EstadoComercialTenant.SoloLectura => Error.Crear(
                "Comercial.SoloLectura",
                "Esta cuenta está en modo de solo lectura por un impago pendiente. Contacta con nosotros para regularizarlo."),
            _ => null,
        };

        if (error is not null)
            return CrearRespuestaFallo<TResponse>(error);

        return await next(cancellationToken);
    }

    /// <summary>Igual mecanismo que AutorizacionEscrituraBehavior — ver su comentario.</summary>
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
            $"GateComercialTenantBehavior solo sabe construir un fallo para Result o Result<T>, no para {tipoRespuesta.Name}.");
    }
}
