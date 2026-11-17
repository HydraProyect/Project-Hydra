using CaeManager.Application.Centros;
using CaeManager.Application.Common;
using CaeManager.Application.Empresas;
using CaeManager.Application.Inbound.Autorizacion;
using CaeManager.Application.Inbound.ContratosAcceso.EvidenciasInbound;
using CaeManager.Application.Inbound.ContratosAcceso.LecturaInbound;
using CaeManager.Application.Tenants.Encargo;
using CaeManager.Application.TiposDocumento;
using CaeManager.Domain.Documentos;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Inbound.ContratosAcceso.Queries.ObtenerColaValidacionInbound;

/// <summary>
/// La cola «Documentos por validar» del Gestor CAE del Tenant TCE en la demo mínima (DECISION_LOG 2026-11-17, opción b),
/// hasta que B-21 traiga el índice de pendientes y la cola paginada. El universo es el mismo que ya define la lectura Inbound,
/// sin un segundo criterio: por cada contrato de acceso que el usuario puede validar (<see cref="LecturaInboundAutoridad"/>)
/// y con autorización vigente hoy, los Documentos autorizados (<see cref="ILectorDocumentosInbound.DocumentosAutorizadosAsync"/>)
/// que todavía no tienen evidencia en ese contrato, en ningún estado. Cada listado deja su traza en el Tenant de la Empresa
/// contratista, como cualquier lectura Inbound.
/// </summary>
[AreaDelEncargo("ContratosAcceso")]
public record ObtenerColaValidacionInboundQuery : IRequest<IReadOnlyList<DocumentoPendienteInboundDto>>;

/// <summary>Un Documento original pendiente de veredicto en un contrato de acceso, sin referencia de archivo.</summary>
/// <param name="TipoDocumentoId">
/// El tipo del catálogo del Tenant TCE con el mismo nombre que el del original (sin espacios de los bordes ni distinguir
/// mayúsculas; con dos así, el de Id menor), el que lleva su evidencia. Null si el catálogo
/// no tiene ninguno: la correspondencia completa de tipos es de B-21, y sin tipo no hay veredicto.
/// </param>
/// <param name="TrabajadorId">Null en un Documento de la Empresa propia del Tenant de la Empresa contratista.</param>
public record DocumentoPendienteInboundDto(
    Guid ContratoAccesoId,
    Guid DocumentoOrigenId,
    string TipoDocumentoNombre,
    Guid? TipoDocumentoId,
    Guid? TrabajadorId,
    string Titular,
    string? Identificador,
    string EmpresaContratista,
    string? CifEmpresaContratista,
    string Centro,
    DateOnly FechaEmision,
    DateOnly? FechaVencimiento,
    EstadoVigenciaDocumento EstadoVigencia,
    bool SoloConclusionAptitud);

public class ObtenerColaValidacionInboundQueryHandler(
    IContratosAccesoQueryContext contratosContext,
    IAlcanceDatosService alcanceDatos,
    ICurrentUserService currentUser,
    ILectorDocumentosInbound lector,
    IEvidenciasInboundQueryContext evidenciasContext,
    ITiposDocumentoQueryContext tiposContext,
    IEmpresasQueryContext empresasContext,
    ICentrosQueryContext centrosContext,
    IAutorizacionInbound autorizacionInbound)
    : IRequestHandler<ObtenerColaValidacionInboundQuery, IReadOnlyList<DocumentoPendienteInboundDto>>
{
    public async Task<IReadOnlyList<DocumentoPendienteInboundDto>> Handle(
        ObtenerColaValidacionInboundQuery request, CancellationToken cancellationToken)
    {
        // La cola es de quien valida: sin ValidarInbound (B-03) no se lista nada ni se toca el lector, como en el Command
        // que registra el veredicto. El rol Consulta lee Inbound, pero no tiene cola de validación.
        if (!await autorizacionInbound.TieneAsync(CapacidadInbound.ValidarInbound, cancellationToken))
            return [];

        var contratos = await contratosContext.ContratosAcceso
            .Where(c => c.TenantContratistaId != null)
            .Select(c => new { c.Id, c.CentroId, c.EmpresaContratistaId })
            .ToListAsync(cancellationToken);

        var tipos = (await tiposContext.TiposDocumento
                .Where(t => !t.Archivado)
                .Select(t => new { t.Id, t.Nombre, t.SoloConclusionAptitud })
                .ToListAsync(cancellationToken))
            .GroupBy(t => t.Nombre.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.MinBy(t => t.Id)!, StringComparer.OrdinalIgnoreCase);

        var cola = new List<DocumentoPendienteInboundDto>();
        foreach (var contrato in contratos)
        {
            // La autoridad, contrato a contrato, antes de tocar el lector: lo que no pasa no se lista ni deja traza.
            if (!await LecturaInboundAutoridad.PuedeLeerAsync(contrato.Id, contratosContext, alcanceDatos, currentUser, cancellationToken))
                continue;
            if (await lector.AutorizacionDelContratoAsync(contrato.Id, cancellationToken) is not { VigenteHoy: true })
                continue;

            var documentos = await lector.DocumentosAutorizadosAsync(contrato.Id, cancellationToken);
            if (documentos.Count == 0)
                continue;

            var conVeredicto = (await evidenciasContext.EvidenciasInbound
                    .Where(e => e.ContratoAccesoId == contrato.Id)
                    .Select(e => e.DocumentoOrigenId)
                    .ToListAsync(cancellationToken))
                .ToHashSet();

            var empresa = await empresasContext.Empresas
                .Where(e => e.Id == contrato.EmpresaContratistaId)
                .Select(e => new { e.RazonSocial, e.Cif })
                .FirstOrDefaultAsync(cancellationToken);
            var centro = await centrosContext.Centros
                .Where(c => c.Id == contrato.CentroId)
                .Select(c => c.Nombre)
                .FirstOrDefaultAsync(cancellationToken);

            foreach (var documento in documentos.Where(d => !conVeredicto.Contains(d.DocumentoId)))
            {
                var deTrabajador = documento.TrabajadorId is not null;
                var tipo = tipos.GetValueOrDefault(documento.TipoDocumentoNombre.Trim());
                cola.Add(new DocumentoPendienteInboundDto(
                    contrato.Id,
                    documento.DocumentoId,
                    documento.TipoDocumentoNombre,
                    tipo?.Id,
                    documento.TrabajadorId,
                    deTrabajador
                        ? $"{documento.TrabajadorNombre} {documento.TrabajadorApellidos}".Trim()
                        : empresa?.RazonSocial ?? string.Empty,
                    deTrabajador ? documento.TrabajadorDni : empresa?.Cif,
                    empresa?.RazonSocial ?? string.Empty,
                    empresa?.Cif,
                    centro ?? string.Empty,
                    documento.FechaEmision,
                    documento.FechaVencimiento,
                    documento.EstadoVigencia,
                    // Basta con que lo sea el original o el tipo del Tenant TCE: el Command pediría la conclusión.
                    documento.SoloConclusionAptitud || tipo?.SoloConclusionAptitud == true));
            }
        }

        return cola;
    }
}
