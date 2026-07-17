using CaeManager.Application.Common;
using CaeManager.Domain.Documentos;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Alertas.Queries.ObtenerAlertas;

/// <summary>
/// Se calcula en vivo sobre Documentos en cada petición, en vez de leer de
/// una tabla Alerta sincronizada por un job en segundo plano — todavía no
/// existe infraestructura de jobs programados en el proyecto, y una vista
/// calculada nunca puede desincronizarse del estado real (ver ROADMAP.md).
/// La entidad Alerta del dominio queda preparada para cuando se necesite
/// marcar alertas como leídas por usuario.
/// </summary>
public record ObtenerAlertasQuery : IRequest<IReadOnlyList<AlertaDto>>;

public record AlertaDto(
    Guid DocumentoId,
    string TrabajadorNombre,
    string TipoDocumentoNombre,
    DateOnly? FechaVencimiento,
    EstadoDocumento Estado,
    string? ArchivoUrl);

public class ObtenerAlertasQueryHandler(IApplicationDbContext dbContext)
    : IRequestHandler<ObtenerAlertasQuery, IReadOnlyList<AlertaDto>>
{
    public async Task<IReadOnlyList<AlertaDto>> Handle(ObtenerAlertasQuery request, CancellationToken cancellationToken)
    {
        var parametros = await dbContext.ParametrosSistema.SingleAsync(cancellationToken);
        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);

        var filas = await (
            from documento in dbContext.Documentos
            join trabajador in dbContext.Trabajadores on documento.TrabajadorId equals trabajador.Id
            join tipoDocumento in dbContext.TiposDocumento on documento.TipoDocumentoId equals tipoDocumento.Id
            where documento.FechaVencimiento != null
            select new
            {
                documento.Id,
                TrabajadorNombre = trabajador.Nombre + " " + trabajador.Apellidos,
                tipoDocumento.Nombre,
                documento.FechaVencimiento,
                documento.ArchivoUrl
            })
            .ToListAsync(cancellationToken);

        return filas
            .Select(f => new AlertaDto(
                f.Id, f.TrabajadorNombre, f.Nombre, f.FechaVencimiento,
                CalculadoraEstadoDocumento.Calcular(
                    f.FechaVencimiento, hoy, parametros.UmbralAmbarDias, parametros.UmbralRojoDias),
                f.ArchivoUrl))
            .Where(a => a.Estado is EstadoDocumento.Proximo or EstadoDocumento.Urgente or EstadoDocumento.Vencido)
            .OrderBy(a => a.Estado == EstadoDocumento.Vencido ? 0 : a.Estado == EstadoDocumento.Urgente ? 1 : 2)
            .ThenBy(a => a.FechaVencimiento)
            .ToList();
    }
}
