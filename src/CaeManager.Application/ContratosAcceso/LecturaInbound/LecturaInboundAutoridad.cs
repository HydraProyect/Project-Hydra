using CaeManager.Application.Common;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.ContratosAcceso.LecturaInbound;

/// <summary>
/// Autoridad del usuario dentro del Tenant del Cliente empresarial antes de leer Documentos de otro Tenant por un contrato
/// de acceso: el contrato tiene que verse con <see cref="ContratoAccesoAutorizacion"/> (cartera sobre su Cliente
/// empresarial) y el usuario tiene que tener un rol que valida, los de
/// <see cref="AutorizacionEscrituraBehavior{TRequest, TResponse}.RolesConEscritura"/>: Consulta y Cliente no validan
/// Documentos y tampoco abren los de otro Tenant, porque cada apertura deja traza en el Tenant propietario. Lo que no pasa
/// responde «no encontrado», sin decir cuál de las dos condiciones falla ni si el contrato existe.
/// </summary>
public static class LecturaInboundAutoridad
{
    public static async Task<bool> PuedeLeerAsync(
        Guid contratoAccesoId,
        IContratosAccesoQueryContext contratosContext,
        IAlcanceDatosService alcanceDatos,
        ICurrentUserService currentUser,
        CancellationToken cancellationToken)
    {
        var rol = await currentUser.ObtenerRolEfectivoAsync();
        if (rol is null || !AutorizacionEscrituraBehavior<object, object>.RolesConEscritura.Contains(rol))
            return false;

        var clienteEmpresarialId = await contratosContext.ContratosAcceso
            .Where(c => c.Id == contratoAccesoId)
            .Select(c => (Guid?)c.ClienteEmpresarialId)
            .FirstOrDefaultAsync(cancellationToken);

        return clienteEmpresarialId is { } cliente
               && await ContratoAccesoAutorizacion.VisibleAsync(cliente, alcanceDatos, cancellationToken);
    }
}
