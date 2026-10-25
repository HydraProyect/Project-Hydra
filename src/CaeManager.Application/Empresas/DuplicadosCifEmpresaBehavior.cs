using CaeManager.Application.Clientes.Commands.CrearCliente;
using CaeManager.Application.Clientes.Commands.EditarCliente;
using CaeManager.Application.Subcontratas.Commands.CrearSubcontrata;
using CaeManager.Application.Subcontratas.Commands.EditarSubcontrata;
using CaeManager.Domain.Common;
using CaeManager.Domain.Empresas;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Application.Empresas;

/// <summary>
/// Z5-7: traduce la violación del índice único de CIF de Empresa en carrera
/// (<see cref="CifEmpresaDuplicadoException"/>) al mismo error de dominio que da
/// la comprobación previa de cada comando.
/// </summary>
/// <remarks>
/// MediatR construye todos los behaviors para cualquier petición, Queries incluidas,
/// y los contenedores mínimos de los tests de integración solo llevan
/// <c>AddApplication()</c>. Por eso el repositorio se resuelve del proveedor del
/// mismo ámbito (mismo DbContext y mismo Tenant propietario) solo dentro del catch,
/// en vez de pedirlo en el constructor.
/// </remarks>
public class DuplicadosCifEmpresaBehavior<TRequest, TResponse>(IServiceProvider proveedor)
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
            if (typeof(TResponse) != typeof(Result)
                && (!typeof(TResponse).IsGenericType || typeof(TResponse).GetGenericTypeDefinition() != typeof(Result<>)))
                throw;

            // El handler ya salió de su transacción. Se consulta únicamente el Tenant propietario actual.
            var repositorio = proveedor.GetRequiredService<IEmpresaRepository>();
            var dadaDeBaja = false;
            foreach (var cif in ex.Cifs)
            {
                if (await repositorio.ObtenerDuplicadaPorCifAsync(cif, cancellationToken: cancellationToken) is { EstaEliminado: true })
                    dadaDeBaja = true;
            }
            var error = DuplicadosCifEmpresa.ErrorDuplicado(dadaDeBaja, CodigoDuplicado(request));
            if (typeof(TResponse) == typeof(Result))
                return (TResponse)(object)Result.Fallo(error);
            var metodo = typeof(Result).GetMethod(nameof(Result.Fallo), 1, [typeof(Error)])!
                .MakeGenericMethod(typeof(TResponse).GetGenericArguments()[0]);
            return (TResponse)metodo.Invoke(null, [error])!;
        }
    }

    // El mismo código que devuelve la comprobación previa de cada comando.
    private static string CodigoDuplicado(TRequest request) => request switch
    {
        CrearClienteCommand or EditarClienteCommand => "Cliente.CifDuplicado",
        CrearSubcontrataCommand or EditarSubcontrataCommand => "Subcontrata.CifDuplicado",
        _ => "Empresa.CifDuplicado"
    };
}
