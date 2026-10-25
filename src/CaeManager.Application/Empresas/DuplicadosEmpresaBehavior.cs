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
/// Traduce las violaciones de los índices únicos de CIF y razón social de Empresa en carrera
/// (<see cref="DatoEmpresaDuplicadoException"/>) al mismo error de dominio que da
/// la comprobación previa de cada comando.
/// </summary>
/// <remarks>
/// MediatR construye todos los behaviors para cualquier petición, Queries incluidas,
/// y los contenedores mínimos de los tests de integración solo llevan
/// <c>AddApplication()</c>. Por eso el repositorio se resuelve del proveedor del
/// mismo ámbito (mismo DbContext y mismo Tenant propietario) solo dentro del catch,
/// en vez de pedirlo en el constructor.
/// </remarks>
public class DuplicadosEmpresaBehavior<TRequest, TResponse>(IServiceProvider proveedor)
    : IPipelineBehavior<TRequest, TResponse> where TRequest : notnull
{
    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        try
        {
            return await next(cancellationToken);
        }
        catch (DatoEmpresaDuplicadoException ex)
        {
            if (typeof(TResponse) != typeof(Result)
                && (!typeof(TResponse).IsGenericType || typeof(TResponse).GetGenericTypeDefinition() != typeof(Result<>)))
                throw;

            // El handler ya salió de su transacción. Se consulta únicamente el Tenant propietario actual.
            var repositorio = proveedor.GetRequiredService<IEmpresaRepository>();
            var dadaDeBaja = false;
            foreach (var valor in ex.Valores)
            {
                var duplicada = ex.EsRazonSocial
                    ? await repositorio.ObtenerDuplicadaPorRazonSocialAsync(valor, cancellationToken: cancellationToken)
                    : await repositorio.ObtenerDuplicadaPorCifAsync(valor, cancellationToken: cancellationToken);
                if (duplicada is { EstaEliminado: true })
                    dadaDeBaja = true;
            }
            var error = ex.EsRazonSocial
                ? DuplicadosEmpresa.ErrorRazonSocialDuplicada(dadaDeBaja, CodigoDuplicado(request, "RazonSocialDuplicada"))
                : DuplicadosEmpresa.ErrorDuplicado(dadaDeBaja, CodigoDuplicado(request, "CifDuplicado"));
            if (typeof(TResponse) == typeof(Result))
                return (TResponse)(object)Result.Fallo(error);
            var metodo = typeof(Result).GetMethod(nameof(Result.Fallo), 1, [typeof(Error)])!
                .MakeGenericMethod(typeof(TResponse).GetGenericArguments()[0]);
            return (TResponse)metodo.Invoke(null, [error])!;
        }
    }

    // El mismo código que devuelve la comprobación previa de cada comando.
    private static string CodigoDuplicado(TRequest request, string dato) => request switch
    {
        CrearClienteCommand or EditarClienteCommand => "Cliente.",
        CrearSubcontrataCommand or EditarSubcontrataCommand => "Subcontrata.",
        _ => "Empresa."
    } + dato;
}
