using CaeManager.Application.Common;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Documentos.Queries.ObtenerSelloEmpresa;

/// <summary>Sello guardado de una Empresa, o null si todavía no tiene ninguno configurado, o si la Empresa está fuera de la cartera del usuario actual.</summary>
public record ObtenerSelloEmpresaQuery(Guid EmpresaId) : IRequest<SelloEmpresaDto?>;

public record SelloEmpresaDto(string ImagenUrl, DateTime ActualizadaEnUtc);

public class ObtenerSelloEmpresaQueryHandler(IDocumentosQueryContext documentosContext, IAlcanceDatosService alcanceDatos)
    : IRequestHandler<ObtenerSelloEmpresaQuery, SelloEmpresaDto?>
{
    public async Task<SelloEmpresaDto?> Handle(ObtenerSelloEmpresaQuery request, CancellationToken cancellationToken)
    {
        if (!await alcanceDatos.EmpresaVisibleAsync(request.EmpresaId, cancellationToken))
            return null;

        return await documentosContext.SellosEmpresa
            .Where(s => s.EmpresaId == request.EmpresaId)
            .Select(s => new SelloEmpresaDto(s.ImagenUrl, s.ActualizadaEnUtc))
            .FirstOrDefaultAsync(cancellationToken);
    }
}
