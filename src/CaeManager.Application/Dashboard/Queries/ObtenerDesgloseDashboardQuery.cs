using CaeManager.Application.Common;
using CaeManager.Domain.Documentos;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Dashboard.Queries;

/// <summary>
/// Desglose adicional del Dashboard, mostrado según el rol del usuario (ver
/// ROADMAP.md, Fase 3): "documentos que requieren atención" para
/// EjecutivoCae, "centros en riesgo" añadido para Supervisor, "empresas en
/// riesgo" añadido para Administrador. Consulta no ve nada de esto, solo
/// los KPI base de ObtenerKpisDashboardQuery.
/// </summary>
public record ObtenerDesgloseDashboardQuery : IRequest<DesgloseDashboardDto>;

public record DocumentoAtencionDto(
    Guid DocumentoId,
    string TrabajadorNombre,
    string TipoDocumentoNombre,
    DateOnly? FechaVencimiento,
    EstadoDocumento Estado);

public record RiesgoCentroDto(
    Guid CentroId,
    string CentroNombre,
    string ClienteNombre,
    int DocumentosVencidos,
    int DocumentosUrgentes);

public record RiesgoEmpresaDto(
    Guid EmpresaId,
    string EmpresaRazonSocial,
    int DocumentosVencidos,
    int DocumentosUrgentes);

public record DesgloseDashboardDto(
    IReadOnlyList<DocumentoAtencionDto> DocumentosAtencion,
    IReadOnlyList<RiesgoCentroDto> CentrosEnRiesgo,
    IReadOnlyList<RiesgoEmpresaDto> EmpresasEnRiesgo);

public class ObtenerDesgloseDashboardQueryHandler(IApplicationDbContext dbContext)
    : IRequestHandler<ObtenerDesgloseDashboardQuery, DesgloseDashboardDto>
{
    private const int MaximoFilas = 5;

    public async Task<DesgloseDashboardDto> Handle(ObtenerDesgloseDashboardQuery request, CancellationToken cancellationToken)
    {
        var parametros = await dbContext.ParametrosSistema.SingleAsync(cancellationToken);
        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);

        var filas = await (
            from documento in dbContext.Documentos
            join trabajador in dbContext.Trabajadores on documento.TrabajadorId equals trabajador.Id
            join tipoDocumento in dbContext.TiposDocumento on documento.TipoDocumentoId equals tipoDocumento.Id
            join empresa in dbContext.Empresas on trabajador.EmpresaId equals empresa.Id into empresasCoincidentes
            from empresa in empresasCoincidentes.DefaultIfEmpty()
            join subcontrata in dbContext.Subcontratas on trabajador.SubcontrataId equals subcontrata.Id into subcontratasCoincidentes
            from subcontrata in subcontratasCoincidentes.DefaultIfEmpty()
            select new
            {
                documento.Id,
                documento.TrabajadorId,
                TrabajadorNombre = trabajador.Nombre + " " + trabajador.Apellidos,
                TipoDocumentoNombre = tipoDocumento.Nombre,
                documento.FechaVencimiento,
                EmpresaId = empresa != null ? empresa.Id : subcontrata!.Id,
                RazonSocial = empresa != null ? empresa.RazonSocial : subcontrata!.RazonSocial
            })
            .ToListAsync(cancellationToken);

        var documentosEnAtencion = filas
            .Select(f => new
            {
                f.Id,
                f.TrabajadorId,
                f.TrabajadorNombre,
                f.TipoDocumentoNombre,
                f.FechaVencimiento,
                f.EmpresaId,
                f.RazonSocial,
                Estado = CalculadoraEstadoDocumento.Calcular(
                    f.FechaVencimiento, hoy, parametros.UmbralAmbarDias, parametros.UmbralRojoDias)
            })
            .Where(d => d.Estado is EstadoDocumento.Urgente or EstadoDocumento.Vencido)
            .ToList();

        var documentosAtencion = documentosEnAtencion
            .OrderBy(d => d.Estado == EstadoDocumento.Vencido ? 0 : 1)
            .ThenBy(d => d.FechaVencimiento)
            .Take(MaximoFilas)
            .Select(d => new DocumentoAtencionDto(d.Id, d.TrabajadorNombre, d.TipoDocumentoNombre, d.FechaVencimiento, d.Estado))
            .ToList();

        var empresasEnRiesgo = documentosEnAtencion
            .GroupBy(d => new { d.EmpresaId, d.RazonSocial })
            .Select(g => new RiesgoEmpresaDto(
                g.Key.EmpresaId,
                g.Key.RazonSocial,
                g.Count(d => d.Estado == EstadoDocumento.Vencido),
                g.Count(d => d.Estado == EstadoDocumento.Urgente)))
            .OrderByDescending(e => e.DocumentosVencidos)
            .ThenByDescending(e => e.DocumentosUrgentes)
            .Take(MaximoFilas)
            .ToList();

        var asignacionesActivas = await (
            from asignacion in dbContext.Asignaciones
            join centro in dbContext.Centros on asignacion.CentroId equals centro.Id
            join cliente in dbContext.Clientes on centro.ClienteId equals cliente.Id
            where asignacion.FechaBaja == null
            select new { asignacion.TrabajadorId, CentroId = centro.Id, CentroNombre = centro.Nombre, ClienteNombre = cliente.RazonSocial })
            .ToListAsync(cancellationToken);

        var centrosEnRiesgo = (
            from documento in documentosEnAtencion
            join asignacion in asignacionesActivas on documento.TrabajadorId equals asignacion.TrabajadorId
            select new { asignacion.CentroId, asignacion.CentroNombre, asignacion.ClienteNombre, documento.Estado })
            .GroupBy(x => new { x.CentroId, x.CentroNombre, x.ClienteNombre })
            .Select(g => new RiesgoCentroDto(
                g.Key.CentroId,
                g.Key.CentroNombre,
                g.Key.ClienteNombre,
                g.Count(x => x.Estado == EstadoDocumento.Vencido),
                g.Count(x => x.Estado == EstadoDocumento.Urgente)))
            .OrderByDescending(c => c.DocumentosVencidos)
            .ThenByDescending(c => c.DocumentosUrgentes)
            .Take(MaximoFilas)
            .ToList();

        return new DesgloseDashboardDto(documentosAtencion, centrosEnRiesgo, empresasEnRiesgo);
    }
}
