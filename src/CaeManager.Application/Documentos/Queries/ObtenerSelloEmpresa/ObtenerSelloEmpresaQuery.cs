using CaeManager.Application.Common;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Documentos.Queries.ObtenerSelloEmpresa;

/// <summary>Sello guardado de una Empresa, o null si todavía no tiene ninguno configurado, o si la Empresa está fuera de la cartera de GESTIÓN del usuario actual (REC-153).</summary>
public record ObtenerSelloEmpresaQuery(Guid EmpresaId) : IRequest<SelloEmpresaDto?>;

public record SelloEmpresaDto(string ImagenUrl, DateTime ActualizadaEnUtc);

public class ObtenerSelloEmpresaQueryHandler(IDocumentosQueryContext documentosContext, IAlcanceDatosService alcanceDatos)
    : IRequestHandler<ObtenerSelloEmpresaQuery, SelloEmpresaDto?>
{
    public async Task<SelloEmpresaDto?> Handle(ObtenerSelloEmpresaQuery request, CancellationToken cancellationToken)
    {
        // Alcance de GESTIÓN, no de lectura (REC-153): el sello es un
        // instrumento de firma, no documentación sobre la Empresa. Con el
        // alcance de lectura (ObtenerEmpresaIdsVisiblesAsync) como puerta, un
        // usuario de portal (rol Cliente) podía descargar la imagen del sello
        // de una contratista de su Cliente.
        if (!await alcanceDatos.EmpresaParaGestionVisibleAsync(request.EmpresaId, cancellationToken))
            return null;

        return await documentosContext.SellosEmpresa
            .Where(s => s.EmpresaId == request.EmpresaId)
            .Select(s => new SelloEmpresaDto(s.ImagenUrl, s.ActualizadaEnUtc))
            .FirstOrDefaultAsync(cancellationToken);
    }
}
