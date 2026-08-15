using CaeManager.Application.Common;
using CaeManager.Application.Empresas;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Documentos.Queries.ObtenerEmpresasConSelloGuardado;

/// <summary>
/// Empresas del tenant (dentro de la cartera visible del usuario actual) que
/// tienen un sello guardado — alimenta el selector de "incluir sello" en la
/// pestaña Firma cuando el Documento no es de ámbito Empresa (así que no hay
/// una Empresa relevante que resolver automáticamente).
/// </summary>
public record ObtenerEmpresasConSelloGuardadoQuery : IRequest<IReadOnlyList<EmpresaConSelloDto>>;

public record EmpresaConSelloDto(Guid EmpresaId, string RazonSocial);

public class ObtenerEmpresasConSelloGuardadoQueryHandler(
    IDocumentosQueryContext documentosContext, IEmpresasQueryContext empresasContext, IAlcanceDatosService alcanceDatos)
    : IRequestHandler<ObtenerEmpresasConSelloGuardadoQuery, IReadOnlyList<EmpresaConSelloDto>>
{
    public async Task<IReadOnlyList<EmpresaConSelloDto>> Handle(
        ObtenerEmpresasConSelloGuardadoQuery request, CancellationToken cancellationToken)
    {
        var empresaIdsVisibles = await alcanceDatos.ObtenerEmpresaIdsVisiblesAsync(cancellationToken);
        var empresaIdsConSello = await documentosContext.SellosEmpresa
            .Select(s => s.EmpresaId)
            .ToListAsync(cancellationToken);

        var consulta = empresasContext.Empresas.Where(e => empresaIdsConSello.Contains(e.Id));
        if (empresaIdsVisibles is not null)
            consulta = consulta.Where(e => empresaIdsVisibles.Contains(e.Id));

        return await consulta
            .OrderBy(e => e.RazonSocial)
            .Select(e => new EmpresaConSelloDto(e.Id, e.RazonSocial))
            .ToListAsync(cancellationToken);
    }
}
