using CaeManager.Application.Common;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Reclamaciones.Queries.ObtenerUltimaReclamacionCliente;

/// <summary>
/// Último lote reclamado a un Cliente, para que Centro 360 pueda mostrar el
/// rastro sin cargar la vista previa completa de
/// <c>ObtenerLoteReclamacionQuery</c> (que recorre todos los clientes del
/// tenant). Null si a ese Cliente nunca se le reclamó nada.
///
/// Cubre las dos vías por las que «Gestionar» reclama los documentos de los Trabajadores del Cliente: la reclamación
/// anclada al Cliente (Trabajadores de Subcontrata) y la enviada a la Empresa empleadora de sus Trabajadores
/// (2026-10-18), y devuelve la más reciente de las dos. Una reclamación de documentos de la propia Empresa contraparte es
/// otro pendiente, con otros documentos y otros destinatarios, y no entra: la vía de la Empresa solo cuenta las que
/// piden documentos de Trabajadores asignados a Centros de este Cliente, y solo de Empresas del alcance de gestión.
/// </summary>
public record ObtenerUltimaReclamacionClienteQuery(Guid ClienteId) : IRequest<UltimaReclamacionClienteDto?>;

/// <param name="ConversacionId">Null si esa reclamación salió sin buzón conectado, o si es anterior al vínculo con Comunicaciones.</param>
public record UltimaReclamacionClienteDto(Guid Id, DateTime FechaEnvioUtc, int TotalDocumentos, Guid? ConversacionId);

public class ObtenerUltimaReclamacionClienteQueryHandler(
    IReclamacionesQueryContext reclamacionesContext, IAlcanceDatosService alcanceDatos,
    DestinoDeReclamacionesEnviadasService reclamacionesEnviadas)
    : IRequestHandler<ObtenerUltimaReclamacionClienteQuery, UltimaReclamacionClienteDto?>
{
    public async Task<UltimaReclamacionClienteDto?> Handle(
        ObtenerUltimaReclamacionClienteQuery request, CancellationToken cancellationToken)
    {
        if (!await alcanceDatos.ClienteVisibleAsync(request.ClienteId, cancellationToken))
            return null;

        var ultima = await reclamacionesContext.ReclamacionesDocumentales
            .Where(r => r.ClienteId == request.ClienteId)
            .OrderByDescending(r => r.FechaEnvioUtc)
            .Select(r => new { r.Id, r.FechaEnvioUtc, r.ConversacionId })
            .FirstOrDefaultAsync(cancellationToken);

        var viaEmpresa = await reclamacionesEnviadas.UltimaViaEmpresaEmpleadoraDeClienteAsync(
            request.ClienteId, await alcanceDatos.ObtenerEmpresaIdsParaGestionAsync(cancellationToken), cancellationToken);
        if (viaEmpresa is not null && (ultima is null || viaEmpresa.FechaEnvioUtc > ultima.FechaEnvioUtc))
            return new UltimaReclamacionClienteDto(viaEmpresa.Id, viaEmpresa.FechaEnvioUtc, viaEmpresa.TotalDocumentos, viaEmpresa.ConversacionId);

        if (ultima is null) return null;

        var totalDocumentos = await reclamacionesContext.ReclamacionesDocumentalesDocumento
            .CountAsync(d => d.ReclamacionDocumentalId == ultima.Id, cancellationToken);

        return new UltimaReclamacionClienteDto(ultima.Id, ultima.FechaEnvioUtc, totalDocumentos, ultima.ConversacionId);
    }
}
