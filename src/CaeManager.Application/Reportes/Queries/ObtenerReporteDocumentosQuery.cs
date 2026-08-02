using CaeManager.Application.Common;
using CaeManager.Application.Configuracion;
using CaeManager.Application.Documentos;
using CaeManager.Application.Empresas;
using CaeManager.Application.Subcontratas;
using CaeManager.Application.TiposDocumento;
using CaeManager.Application.Trabajadores;
using CaeManager.Domain.Documentos;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Reportes.Queries;

/// <summary>
/// Reporte completo de vigencia documental (ver ROADMAP.md, Fase 3):
/// a diferencia de Alertas, incluye también los documentos Vigentes — es
/// el reporte que se entrega a un cliente/auditor para demostrar cobertura
/// completa, no solo lo pendiente de acción. Mismo cálculo de estado que
/// Dashboard/Alertas/Calendario. Solo cubre Documentos de Trabajador — los
/// de Cliente/Empresa no aparecen en este reporte todavía (fuera de alcance).
/// </summary>
public record ObtenerReporteDocumentosQuery : IRequest<IReadOnlyList<FilaReporteDocumentoDto>>;

public record FilaReporteDocumentoDto(
    Guid DocumentoId,
    string TrabajadorNombre,
    string EmpresaRazonSocial,
    string TipoDocumentoNombre,
    DateOnly? FechaVencimiento,
    EstadoDocumento Estado);

public class ObtenerReporteDocumentosQueryHandler(IConfiguracionQueryContext configuracionContext, IDocumentosQueryContext documentosContext, IEmpresasQueryContext empresasContext, ISubcontratasQueryContext subcontratasContext, ITiposDocumentoQueryContext tiposDocumentoContext, ITrabajadoresQueryContext trabajadoresContext)
    : IRequestHandler<ObtenerReporteDocumentosQuery, IReadOnlyList<FilaReporteDocumentoDto>>
{
    public async Task<IReadOnlyList<FilaReporteDocumentoDto>> Handle(ObtenerReporteDocumentosQuery request, CancellationToken cancellationToken)
    {
        var parametros = await configuracionContext.ParametrosSistema.SingleAsync(cancellationToken);
        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);

        var filas = await (
            from documento in documentosContext.Documentos
            where documento.TrabajadorId != null
            join trabajador in trabajadoresContext.Trabajadores on documento.TrabajadorId!.Value equals trabajador.Id
            join tipoDocumento in tiposDocumentoContext.TiposDocumento on documento.TipoDocumentoId equals tipoDocumento.Id
            join empresa in empresasContext.Empresas on trabajador.EmpresaId equals empresa.Id into empresasCoincidentes
            from empresa in empresasCoincidentes.DefaultIfEmpty()
            join subcontrata in subcontratasContext.Subcontratas on trabajador.SubcontrataId equals subcontrata.Id into subcontratasCoincidentes
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
