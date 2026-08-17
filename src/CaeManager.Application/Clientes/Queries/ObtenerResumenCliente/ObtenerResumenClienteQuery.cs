using CaeManager.Application.Asignaciones;
using CaeManager.Application.Centros;
using CaeManager.Application.Common;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Clientes.Queries.ObtenerResumenCliente;

/// <summary>
/// Datos de la pestaña "Información" del drawer ligero de vista previa
/// (Lista Clientes TALVEG.dc.html) — a diferencia de ObtenerClientePorIdQuery
/// (los campos propios del Cliente), aquí se agregan Centros y Trabajadores
/// contando asignaciones activas, mismo criterio que
/// GenerarInformeAsignacionesQuery (Reportes). "Portal principal" del mockup
/// se queda fuera a propósito: un Cliente puede tener varios Centros, cada
/// uno con su propio canal de gestión "principal" — no existe un portal
/// principal a nivel de Cliente, elegir uno cualquiera sería inventar dato.
/// </summary>
public record ObtenerResumenClienteQuery(Guid ClienteId) : IRequest<ResumenClienteDto?>;

public record ResumenClienteDto(
    Guid Id, string RazonSocial, string Cif, bool EsCritico, DateTime CreadoEnUtc,
    Guid? EjecutivoUsuarioId, int TotalCentros, int TotalTrabajadores);

public class ObtenerResumenClienteQueryHandler(
    IClientesQueryContext clientesContext, ICentrosQueryContext centrosContext,
    IAsignacionesQueryContext asignacionesContext, IAlcanceDatosService alcanceDatos)
    : IRequestHandler<ObtenerResumenClienteQuery, ResumenClienteDto?>
{
    public async Task<ResumenClienteDto?> Handle(ObtenerResumenClienteQuery request, CancellationToken cancellationToken)
    {
        if (!await alcanceDatos.ClienteVisibleAsync(request.ClienteId, cancellationToken)) return null;

        var cliente = await clientesContext.Clientes
            .Where(c => c.Id == request.ClienteId)
            .Select(c => new { c.Id, c.RazonSocial, c.Cif, c.EsCritico, c.CreadoEnUtc, c.EjecutivoUsuarioId })
            .FirstOrDefaultAsync(cancellationToken);

        if (cliente is null) return null;

        var centroIds = await centrosContext.Centros
            .Where(c => c.ClienteId == request.ClienteId)
            .Select(c => c.Id)
            .ToListAsync(cancellationToken);

        var totalTrabajadores = await asignacionesContext.Asignaciones
            .Where(a => a.FechaBaja == null && centroIds.Contains(a.CentroId))
            .Select(a => a.TrabajadorId)
            .Distinct()
            .CountAsync(cancellationToken);

        return new ResumenClienteDto(
            cliente.Id, cliente.RazonSocial, cliente.Cif, cliente.EsCritico, cliente.CreadoEnUtc,
            cliente.EjecutivoUsuarioId, centroIds.Count, totalTrabajadores);
    }
}
