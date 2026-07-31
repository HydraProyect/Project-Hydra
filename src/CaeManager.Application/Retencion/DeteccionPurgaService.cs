using CaeManager.Application.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Retencion;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CaeManager.Application.Retencion;

/// <summary>
/// Encuentra qué datos han cumplido su plazo de retención y levanta una
/// <see cref="SolicitudPurga"/> por cada categoría.
///
/// <b>No destruye nada.</b> Deja una propuesta pendiente de revisión, que es
/// lo que permite avisar antes al tenant para que extraiga sus datos si su
/// política interna exige conservarlos más años (decisión del propietario del
/// producto, 2026-07-31). Quien autoriza y programa la ejecución es una
/// persona, nunca este servicio.
///
/// Trabaja sobre el tenant que tenga resuelto quien lo invoque: el barrido que
/// recorre varios tenants es responsabilidad de quien orquesta, con ámbito
/// explícito por tenant (docs/MULTITENANCY.md § 8.4).
/// </summary>
public class DeteccionPurgaService(
    IApplicationDbContext dbContext,
    ISolicitudPurgaRepository solicitudRepositorio,
    IOptions<RetencionDatosOptions> opciones,
    IUnitOfWork unitOfWork)
{
    private readonly RetencionDatosOptions _opciones = opciones.Value;

    /// <summary>
    /// Devuelve cuántas solicitudes ha creado. Idempotente por categoría: si
    /// ya hay una propuesta abierta sin resolver, no crea otra — repetir el
    /// barrido no debe llenar la bandeja de duplicados.
    /// </summary>
    public async Task<int> DetectarAsync(DateOnly hoy, CancellationToken cancellationToken = default)
    {
        var creadas = 0;

        if (await DetectarDocumentosAsync(hoy, cancellationToken)) creadas++;
        if (await DetectarTrabajadoresAsync(hoy, cancellationToken)) creadas++;

        if (creadas > 0)
            await unitOfWork.SaveChangesAsync(cancellationToken);

        return creadas;
    }

    private async Task<bool> DetectarDocumentosAsync(DateOnly hoy, CancellationToken cancellationToken)
    {
        if (await solicitudRepositorio.ExisteAbiertaAsync(TipoDatoPurgable.Documentos, cancellationToken))
            return false;

        // El filtro se traduce a fechas para que lo resuelva SQL, igual que en
        // el listado de Documentos: traer todo el histórico a memoria para
        // decidir qué purgar sería justo el problema que se corrigió allí.
        //
        // Solo cuenta lo que YA está vencido o sin vigencia: un documento
        // vigente no ha empezado siquiera a contar su plazo.
        var limite = hoy.AddYears(-_opciones.AniosRetencionDocumentos);

        // AnonimizadoEnUtc y no EstaAnonimizado: la segunda es calculada y EF
        // no la traduce a SQL, así que filtrar por ella traería el histórico
        // entero a memoria.
        var candidatos = await dbContext.Documentos
            .Where(d => d.AnonimizadoEnUtc == null)
            .Where(d => d.FechaVencimiento != null
                ? d.FechaVencimiento <= limite
                : d.FechaEmision <= limite)
            .CountAsync(cancellationToken);

        if (candidatos == 0) return false;

        solicitudRepositorio.Agregar(new SolicitudPurga(TipoDatoPurgable.Documentos, candidatos, limite));
        return true;
    }

    private async Task<bool> DetectarTrabajadoresAsync(DateOnly hoy, CancellationToken cancellationToken)
    {
        // Null desactiva esta categoría sin tocar la de documentos: la
        // retención del Trabajador afecta a datos de salud y puede querer
        // separarse tras revisión legal.
        if (_opciones.AniosRetencionTrabajadores is not { } anios) return false;

        if (await solicitudRepositorio.ExisteAbiertaAsync(TipoDatoPurgable.TrabajadoresDadosDeBaja, cancellationToken))
            return false;

        var limite = hoy.AddYears(-anios);

        // La baja del trabajador es la de su última asignación: mientras
        // tenga una activa sigue de alta y no empieza a contar nada.
        //
        // Con subconsultas y no con un 'let': EF no traduce un IQueryable
        // proyectado, y dejarlo así traería todas las asignaciones a memoria.
        var candidatos = await dbContext.Trabajadores
            .Where(t => t.AnonimizadoEnUtc == null)
            .Where(t => dbContext.Asignaciones.Any(a => a.TrabajadorId == t.Id))
            .Where(t => !dbContext.Asignaciones.Any(a => a.TrabajadorId == t.Id && a.FechaBaja == null))
            .Where(t => dbContext.Asignaciones
                .Where(a => a.TrabajadorId == t.Id)
                .Max(a => a.FechaBaja) <= limite)
            .CountAsync(cancellationToken);

        if (candidatos == 0) return false;

        solicitudRepositorio.Agregar(
            new SolicitudPurga(TipoDatoPurgable.TrabajadoresDadosDeBaja, candidatos, limite));

        return true;
    }
}
