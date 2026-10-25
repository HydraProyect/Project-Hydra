using CaeManager.Domain.Common;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Application.Common;

/// <summary>
/// Dentro de la serialización del circuito, impide que un comando posterior guarde
/// los cambios pendientes de un comando fallido. No revierte escrituras ya guardadas.
/// </summary>
public class RestauracionCambiosDeComandoBehavior<TRequest, TResponse>(IServiceProvider proveedor)
    : IPipelineBehavior<TRequest, TResponse> where TRequest : notnull
{
    public async Task<TResponse> Handle(
        TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        if (request is not ICommandBase)
            return await next(cancellationToken);

        // MediatR construye todos los behaviors también para consultas: no resolver
        // persistencia antes de distinguirlas. Los contenedores mínimos pueden registrar
        // el contexto solo como IUnitOfWork, sin ejecutar AddInfrastructure.
        var restauracion = proveedor.GetService<IRestauracionCambiosDeComando>()
            ?? proveedor.GetRequiredService<IUnitOfWork>() as IRestauracionCambiosDeComando
            ?? throw new InvalidOperationException("La unidad de trabajo debe permitir restaurar los cambios del comando.");
        using var instantanea = restauracion.CapturarRestauracion();
        var conservar = false;
        try
        {
            var respuesta = await next(cancellationToken);
            conservar = respuesta is not Result { EsFallido: true };
            return respuesta;
        }
        finally
        {
            if (!conservar)
                instantanea.Restaurar();
        }
    }
}
