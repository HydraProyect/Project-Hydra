using CaeManager.Application.Common;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Reclamaciones.Queries.ObtenerUltimaReclamacionCliente;

/// <summary>
/// Último lote reclamado a un Cliente, para que Centro 360 pueda mostrar el
/// rastro sin cargar la vista previa completa de
/// <c>ObtenerLoteReclamacionQuery</c> (que recorre todos los clientes del
/// tenant). Null si a ese Cliente nunca se le reclamó nada.
/// </summary>
public record ObtenerUltimaReclamacionClienteQuery(Guid ClienteId) : IRequest<UltimaReclamacionClienteDto?>;

/// <param name="ConversacionId">Null si esa reclamación salió sin buzón conectado, o si es anterior al vínculo con Comunicaciones.</param>
public record UltimaReclamacionClienteDto(Guid Id, DateTime FechaEnvioUtc, int TotalDocumentos, Guid? ConversacionId);

public class ObtenerUltimaReclamacionClienteQueryHandler(
    IReclamacionesQueryContext reclamacionesContext, IAlcanceDatosService alcanceDatos)
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

        if (ultima is null) return null;

        var totalDocumentos = await reclamacionesContext.ReclamacionesDocumentalesDocumento
            .CountAsync(d => d.ReclamacionDocumentalId == ultima.Id, cancellationToken);

        return new UltimaReclamacionClienteDto(ultima.Id, ultima.FechaEnvioUtc, totalDocumentos, ultima.ConversacionId);
    }
}
