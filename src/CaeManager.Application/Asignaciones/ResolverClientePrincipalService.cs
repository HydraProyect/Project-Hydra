using CaeManager.Application.Centros;
using CaeManager.Application.Clientes;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Asignaciones;

public record ClientePrincipalTrabajador(Guid CentroId, string CentroNombre, Guid ClienteId, string ClienteNombre);

/// <summary>
/// Resuelve el Centro/Cliente "principal" de un Trabajador — para agrupar
/// items de la Bandeja "por situación" en el rediseño de Inicio (hallazgo
/// P-03 de la auditoría de producto 2026-08-16). Un Trabajador puede tener
/// varias asignaciones activas simultáneas a distintos Clientes, así que
/// preguntas como "de qué Cliente es esta alerta de vigencia" (que a
/// diferencia de un Faltante o un Requisito no referencia un Centro
/// concreto) no tienen una única respuesta canónica. Se resuelve por la
/// asignación activa con la <c>FechaAlta</c> más reciente — mismo criterio
/// de "elegir uno determinista entre N" que
/// <see cref="CaeManager.Domain.Centros.CanalGestionDocumental.EsPrincipal"/>
/// usa para varios canales por Centro.
///
/// Sin ninguna asignación activa, el Trabajador queda sin entrada en el
/// diccionario devuelto — quien lo consuma debe tratar eso como "sin grupo",
/// nunca como un error.
/// </summary>
public interface IResolverClientePrincipalService
{
    Task<IReadOnlyDictionary<Guid, ClientePrincipalTrabajador>> ResolverPorTrabajadorAsync(
        IReadOnlyCollection<Guid> trabajadorIds, CancellationToken cancellationToken = default);
}

public class ResolverClientePrincipalService(
    IAsignacionesQueryContext asignacionesContext, ICentrosQueryContext centrosContext, IClientesQueryContext clientesContext)
    : IResolverClientePrincipalService
{
    public async Task<IReadOnlyDictionary<Guid, ClientePrincipalTrabajador>> ResolverPorTrabajadorAsync(
        IReadOnlyCollection<Guid> trabajadorIds, CancellationToken cancellationToken = default)
    {
        if (trabajadorIds.Count == 0)
            return new Dictionary<Guid, ClientePrincipalTrabajador>();

        var filas = await (
            from asignacion in asignacionesContext.Asignaciones
            where asignacion.FechaBaja == null && trabajadorIds.Contains(asignacion.TrabajadorId)
            join centro in centrosContext.Centros on asignacion.CentroId equals centro.Id
            join cliente in clientesContext.Clientes on centro.ClienteId equals cliente.Id
            select new
            {
                asignacion.TrabajadorId,
                asignacion.FechaAlta,
                CentroId = centro.Id,
                CentroNombre = centro.Nombre,
                ClienteId = cliente.Id,
                ClienteNombre = cliente.RazonSocial
            })
            .ToListAsync(cancellationToken);

        return filas
            .GroupBy(f => f.TrabajadorId)
            .ToDictionary(
                g => g.Key,
                g =>
                {
                    var principal = g.OrderByDescending(f => f.FechaAlta).ThenByDescending(f => f.CentroId).First();
                    return new ClientePrincipalTrabajador(principal.CentroId, principal.CentroNombre, principal.ClienteId, principal.ClienteNombre);
                });
    }
}
