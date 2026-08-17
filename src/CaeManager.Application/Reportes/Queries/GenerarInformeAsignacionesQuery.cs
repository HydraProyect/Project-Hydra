using CaeManager.Application.Asignaciones;
using CaeManager.Application.Centros;
using CaeManager.Application.Clientes;
using CaeManager.Application.Trabajadores;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Reportes.Queries;

/// <summary>
/// "Asignaciones activas" de la Biblioteca de Reportes: quién está
/// autorizado en cada centro y desde cuándo — a diferencia de
/// ObtenerAsignacionesDocumentacionPorCentroQuery (Centro 360, un solo
/// Centro), esta agrega todas las asignaciones activas del alcance pedido.
/// </summary>
public record GenerarInformeAsignacionesQuery(Guid? ClienteId, Guid? CentroId) : IRequest<InformeAsignacionesDto>;

public record InformeAsignacionesDto(string Alcance, IReadOnlyList<FilaInformeAsignacionDto> Filas);

public record FilaInformeAsignacionDto(string TrabajadorNombre, string CentroNombre, DateOnly FechaAlta);

public class GenerarInformeAsignacionesQueryHandler(
    IAsignacionesQueryContext asignacionesContext, ITrabajadoresQueryContext trabajadoresContext,
    ICentrosQueryContext centrosContext, IClientesQueryContext clientesContext)
    : IRequestHandler<GenerarInformeAsignacionesQuery, InformeAsignacionesDto>
{
    public async Task<InformeAsignacionesDto> Handle(GenerarInformeAsignacionesQuery request, CancellationToken cancellationToken)
    {
        var consulta =
            from asignacion in asignacionesContext.Asignaciones
            where asignacion.FechaBaja == null
            join trabajador in trabajadoresContext.Trabajadores on asignacion.TrabajadorId equals trabajador.Id
            join centro in centrosContext.Centros on asignacion.CentroId equals centro.Id
            select new { trabajador.Nombre, trabajador.Apellidos, CentroNombre = centro.Nombre, centro.Id, centro.ClienteId, asignacion.FechaAlta };

        if (request.CentroId is { } centroId)
            consulta = consulta.Where(a => a.Id == centroId);
        else if (request.ClienteId is { } clienteId)
            consulta = consulta.Where(a => a.ClienteId == clienteId);

        var filas = await consulta
            .OrderBy(a => a.CentroNombre).ThenBy(a => a.Nombre)
            .Select(a => new FilaInformeAsignacionDto($"{a.Nombre} {a.Apellidos}", a.CentroNombre, a.FechaAlta))
            .ToListAsync(cancellationToken);

        var alcance = await ResolverTextoAlcanceAsync(request.ClienteId, request.CentroId, cancellationToken);
        return new InformeAsignacionesDto(alcance, filas);
    }

    private async Task<string> ResolverTextoAlcanceAsync(Guid? clienteId, Guid? centroId, CancellationToken cancellationToken)
    {
        if (centroId is { } cId)
        {
            var centro = await centrosContext.Centros.Where(c => c.Id == cId).Select(c => c.Nombre).SingleOrDefaultAsync(cancellationToken);
            return centro is null ? "Centro" : $"Solo centro: {centro}";
        }

        if (clienteId is { } clId)
        {
            var cliente = await clientesContext.Clientes.Where(c => c.Id == clId).Select(c => c.RazonSocial).SingleOrDefaultAsync(cancellationToken);
            return cliente is null ? "Cliente" : $"{cliente} · todo el cliente";
        }

        return "Toda la cartera (sin filtrar por cliente)";
    }
}
