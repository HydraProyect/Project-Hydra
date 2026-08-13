using CaeManager.Application.Asignaciones;
using CaeManager.Application.Common;
using CaeManager.Application.Documentos;
using CaeManager.Application.Trabajadores;
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
    IAsignacionesQueryContext asignacionesContext, IDocumentosQueryContext documentosContext, ITrabajadoresQueryContext trabajadoresContext,
    ISolicitudPurgaRepository solicitudRepositorio,
    IOptions<RetencionDatosOptions> opciones,
    ITenantActual tenantActual,
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
        // Fallo cerrado: sin tenant resuelto no hay ámbito seguro en el que
        // aplicar IgnoreQueryFilters() más abajo — mismo criterio que el
        // resto de la cadena de resolución de tenant.
        if (tenantActual.TenantId is not { } tenantId) return 0;

        var creadas = 0;

        if (await DetectarDocumentosAsync(tenantId, hoy, cancellationToken)) creadas++;
        if (await DetectarTrabajadoresAsync(tenantId, hoy, cancellationToken)) creadas++;

        if (creadas > 0)
            await unitOfWork.SaveChangesAsync(cancellationToken);

        return creadas;
    }

    private async Task<bool> DetectarDocumentosAsync(Guid tenantId, DateOnly hoy, CancellationToken cancellationToken)
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

        // IgnoreQueryFilters() + Where(TenantId) explícito (revisión
        // deliberada, ver CLAUDE.md): el filtro global oculta también las
        // filas EstaEliminado, así que un Documento borrado lógicamente
        // nunca entraba en detección — hallazgo P0-3 de
        // docs/business/MATURITY_REVIEW.md. Se reemplaza el filtro completo
        // (tenant + soft-delete) por uno que solo exige el tenant, para
        // ver también lo soft-deleted sin dejar de acotar al tenant actual.
        //
        // AnonimizadoEnUtc y no EstaAnonimizado: la segunda es calculada y EF
        // no la traduce a SQL, así que filtrar por ella traería el histórico
        // entero a memoria.
        var candidatos = await documentosContext.Documentos
            .IgnoreQueryFilters()
            .Where(d => d.TenantId == tenantId)
            .Where(d => d.AnonimizadoEnUtc == null)
            .Where(d => d.FechaVencimiento != null
                ? d.FechaVencimiento <= limite
                : d.FechaEmision <= limite)
            .CountAsync(cancellationToken);

        if (candidatos == 0) return false;

        solicitudRepositorio.Agregar(new SolicitudPurga(TipoDatoPurgable.Documentos, candidatos, limite));
        return true;
    }

    private async Task<bool> DetectarTrabajadoresAsync(Guid tenantId, DateOnly hoy, CancellationToken cancellationToken)
    {
        // Null desactiva esta categoría sin tocar la de documentos: la
        // retención del Trabajador afecta a datos de salud y puede querer
        // separarse tras revisión legal.
        if (_opciones.AniosRetencionTrabajadores is not { } anios) return false;

        if (await solicitudRepositorio.ExisteAbiertaAsync(TipoDatoPurgable.TrabajadoresDadosDeBaja, cancellationToken))
            return false;

        var limite = hoy.AddYears(-anios);

        // Mismo criterio que en Documentos: IgnoreQueryFilters() + Where(TenantId)
        // explícito para alcanzar también a los Trabajadores dados de baja
        // (soft-delete) — son justamente el caso que esta categoría existe
        // para cubrir (P0-3 de docs/business/MATURITY_REVIEW.md). Las
        // subconsultas sobre Asignaciones no necesitan el mismo tratamiento:
        // Asignacion no tiene soft-delete y su filtro de tenant normal ya es
        // el correcto.
        //
        // Con subconsultas y no con un 'let': EF no traduce un IQueryable
        // proyectado, y dejarlo así traería todas las asignaciones a memoria.
        var candidatos = await trabajadoresContext.Trabajadores
            .IgnoreQueryFilters()
            .Where(t => t.TenantId == tenantId)
            .Where(t => t.AnonimizadoEnUtc == null)
            .Where(t => asignacionesContext.Asignaciones.Any(a => a.TrabajadorId == t.Id))
            .Where(t => !asignacionesContext.Asignaciones.Any(a => a.TrabajadorId == t.Id && a.FechaBaja == null))
            .Where(t => asignacionesContext.Asignaciones
                .Where(a => a.TrabajadorId == t.Id)
                .Max(a => a.FechaBaja) <= limite)
            .CountAsync(cancellationToken);

        if (candidatos == 0) return false;

        solicitudRepositorio.Agregar(
            new SolicitudPurga(TipoDatoPurgable.TrabajadoresDadosDeBaja, candidatos, limite));

        return true;
    }
}
