using CaeManager.Application.Centros;
using CaeManager.Application.Clientes;
using CaeManager.Application.Common;
using CaeManager.Application.Empresas;
using CaeManager.Application.Trabajadores;
using CaeManager.Domain.Visitas;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Visitas.Queries.ObtenerDetalleVisita;

/// <summary>
/// Alimenta la vista de solo lectura "Ver visita" — a diferencia de
/// <c>ObtenerVisitaPorIdQuery</c> (que solo trae <c>TrabajadorIds</c> en bruto
/// para el formulario de edición), esta expone nombre/DNI de cada Trabajador y
/// el Id de Empresa, necesarios para mostrar quién entra y renderizar su
/// documentación (ver <c>PestanaDocumentacion</c>, reutilizada tal cual desde
/// el Context Workspace).
/// </summary>
public record ObtenerDetalleVisitaQuery(Guid Id) : IRequest<DetalleVisitaDto?>;

public record TrabajadorVisitaDto(Guid Id, string NombreCompleto, string Dni);

public record DetalleVisitaDto(
    Guid Id,
    string CentroNombre,
    string ClienteRazonSocial,
    Guid EmpresaId,
    string EmpresaRazonSocial,
    DateOnly FechaInicio,
    DateOnly FechaFin,
    string? Notas,
    bool NotificadoCliente,
    IReadOnlyList<TrabajadorVisitaDto> Trabajadores,
    TimeOnly? HoraEstimadaAcceso,
    DateTime? FechaHoraSolicitudUtc,
    DateTime? FechaHoraExpedienteCompletoUtc,
    decimal? AntelacionNominalHoras,
    decimal? AntelacionEfectivaHoras,
    TramoAntelacion? Tramo,
    AtribucionUrgencia Atribucion);

public class ObtenerDetalleVisitaQueryHandler(
    ICentrosQueryContext centrosContext, IClientesQueryContext clientesContext, IEmpresasQueryContext empresasContext,
    ITrabajadoresQueryContext trabajadoresContext, IVisitasQueryContext visitasContext, IAlcanceDatosService alcanceDatos)
    : IRequestHandler<ObtenerDetalleVisitaQuery, DetalleVisitaDto?>
{
    public async Task<DetalleVisitaDto?> Handle(ObtenerDetalleVisitaQuery request, CancellationToken cancellationToken)
    {
        var visita = await (
            from v in visitasContext.Visitas
            join centro in centrosContext.Centros on v.CentroId equals centro.Id
            join cliente in clientesContext.Clientes on centro.ClienteId equals cliente.Id
            join empresa in empresasContext.Empresas on centro.EmpresaId equals empresa.Id
            where v.Id == request.Id
            select new
            {
                v.Id,
                CentroId = centro.Id,
                CentroNombre = centro.Nombre,
                ClienteRazonSocial = cliente.RazonSocial,
                EmpresaId = empresa.Id,
                EmpresaRazonSocial = empresa.RazonSocial,
                v.FechaInicio,
                v.FechaFin,
                v.Notas,
                v.NotificadoCliente,
                v.HoraEstimadaAcceso,
                v.FechaHoraSolicitudUtc,
                v.FechaHoraExpedienteCompletoUtc,
                v.AntelacionNominalHoras,
                v.AntelacionEfectivaHoras,
                v.Tramo,
                v.Atribucion
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (visita is null) return null;
        if (!await alcanceDatos.CentroVisibleAsync(visita.CentroId, cancellationToken)) return null;

        var trabajadorIds = await visitasContext.VisitasTrabajadores
            .Where(vt => vt.VisitaId == request.Id)
            .Select(vt => vt.TrabajadorId)
            .ToListAsync(cancellationToken);

        var trabajadores = await trabajadoresContext.Trabajadores
            .Where(t => trabajadorIds.Contains(t.Id))
            .OrderBy(t => t.Apellidos).ThenBy(t => t.Nombre)
            .Select(t => new TrabajadorVisitaDto(t.Id, t.Nombre + " " + t.Apellidos, t.Dni))
            .ToListAsync(cancellationToken);

        return new DetalleVisitaDto(
            visita.Id, visita.CentroNombre, visita.ClienteRazonSocial, visita.EmpresaId, visita.EmpresaRazonSocial,
            visita.FechaInicio, visita.FechaFin, visita.Notas, visita.NotificadoCliente, trabajadores,
            visita.HoraEstimadaAcceso, visita.FechaHoraSolicitudUtc, visita.FechaHoraExpedienteCompletoUtc,
            visita.AntelacionNominalHoras, visita.AntelacionEfectivaHoras, visita.Tramo, visita.Atribucion);
    }
}
