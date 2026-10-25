using CaeManager.Domain.Common;
using CaeManager.Domain.Empresas;
using MediatR;

namespace CaeManager.Application.Empresas;

public class DuplicadosCifEmpresaBehavior<TRequest, TResponse>(IEmpresaRepository repositorio)
    : IPipelineBehavior<TRequest, TResponse> where TRequest : notnull
{
    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        try
        {
            return await next(cancellationToken);
        }
        catch (CifEmpresaDuplicadoException ex)
        {
            // El handler ya salió de su transacción. Se consulta únicamente el Tenant propietario actual.
            var dadaDeBaja = false;
            foreach (var cif in ex.Cifs)
            {
                if (await repositorio.ObtenerDuplicadaPorCifAsync(cif, cancellationToken: cancellationToken) is { EstaEliminado: true })
                    dadaDeBaja = true;
            }
            var error = DuplicadosCifEmpresa.ErrorDuplicado(dadaDeBaja);
            if (typeof(TResponse) == typeof(Result))
                return (TResponse)(object)Result.Fallo(error);
            if (!typeof(TResponse).IsGenericType || typeof(TResponse).GetGenericTypeDefinition() != typeof(Result<>))
                throw;
            var metodo = typeof(Result).GetMethod(nameof(Result.Fallo), 1, [typeof(Error)])!
                .MakeGenericMethod(typeof(TResponse).GetGenericArguments()[0]);
            return (TResponse)metodo.Invoke(null, [error])!;
        }
    }
}
