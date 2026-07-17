using CaeManager.Application.Common;
using CaeManager.Domain.Documentos;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Reportes.Queries;

/// <summary>
/// Reporte completo de vigencia documental (ver ROADMAP.md, Fase 3):
/// a diferencia de Alertas, incluye también los documentos Vigentes — es
/// el reporte que se entrega a un cliente/auditor para demostrar cobertura
/// completa, no solo lo pendiente de acción. Mismo cálculo de estado que
/// Dashboard/Alertas/Calendario.
/// </summary>
public record ObtenerReporteDocumentosQuery : IRequest<IReadOnlyList<FilaReporteDocumentoDto>>;

public record FilaReporteDocumentoDto(
    Guid DocumentoId,
    string TrabajadorNombre,
    string EmpresaRazonSocial,
    string TipoDocumentoNombre,
    DateOnly? FechaVencimiento,
    EstadoDocumento Estado);

public class ObtenerReporteDocumentosQueryHandler(IApplicationDbContext dbContext)
    : IRequestHandler<ObtenerReporteDocumentosQuery, IReadOnlyList<FilaReporteDocumentoDto>>
{
    public async Task<IReadOnlyList<FilaReporteDocumentoDto>> Handle(ObtenerReporteDocumentosQuery request, CancellationToken cancellationToken)
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
                TrabajadorNombre = trabajador.Nombre + " " + trabajador.Apellidos,
                RazonSocial = empresa != null ? empresa.RazonSocial : subcontrata!.RazonSocial,
                TipoDocumentoNombre = tipoDocumento.Nombre,
                documento.FechaVencimiento
            })
            .ToListAsync(cancellationToken);

        return filas
            .Select(f => new FilaReporteDocumentoDto(
                f.Id,
                f.TrabajadorNombre,
                f.RazonSocial,
                f.TipoDocumentoNombre,
                f.FechaVencimiento,
                CalculadoraEstadoDocumento.Calcular(
                    f.FechaVencimiento, hoy, parametros.UmbralAmbarDias, parametros.UmbralRojoDias)))
            .OrderBy(f => f.Estado switch
            {
                EstadoDocumento.Vencido => 0,
                EstadoDocumento.Urgente => 1,
                EstadoDocumento.Proximo => 2,
                _ => 3
            })
            .ThenBy(f => f.FechaVencimiento)
            .ToList();
    }
}
