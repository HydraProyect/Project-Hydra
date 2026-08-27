using CaeManager.Application.Common;
using CaeManager.Application.Empresas;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Empresas.Queries.ObtenerEmpresasParaSelector;

/// <summary>
/// Lista ligera para poblar selectores. Con ClienteId, se restringe a las
/// Empresas ya asociadas a ese Cliente (p. ej. al elegir la Empresa de un
/// Centro nuevo) — sin ClienteId, devuelve todas (p. ej. al dar de alta un
/// Trabajador, donde la Empresa no depende de ningún Cliente concreto).
/// </summary>
public record ObtenerEmpresasParaSelectorQuery(Guid? ClienteId = null) : IRequest<IReadOnlyList<EmpresaSelectorDto>>;

public record EmpresaSelectorDto(Guid Id, string RazonSocial);

public class ObtenerEmpresasParaSelectorQueryHandler(IEmpresasQueryContext dbContext)
    : IRequestHandler<ObtenerEmpresasParaSelectorQuery, IReadOnlyList<EmpresaSelectorDto>>
{
    public async Task<IReadOnlyList<EmpresaSelectorDto>> Handle(
        ObtenerEmpresasParaSelectorQuery request, CancellationToken cancellationToken)
    {
        // El filtro EsPropia corrige un defecto que arrastra desde F3a, no de
        // F4: al unificar Cliente y Subcontrata dentro de Empresas, este
        // selector pasó a ofrecer también las contrapartes como si fueran
        // Empresas propias. Sus dos hermanos ya discriminan
        // (ObtenerClientesParaSelectorQuery por EsCritico != null,
        // ObtenerSubcontratasParaSelectorQuery por NivelServicio != null) y
        // los consumidores lo confirman: /vehiculos y /subcontratas cargan
        // por separado el selector de Empresas y el de la contraparte.
        var consulta = dbContext.Empresas.Where(e => e.EsPropia);

        if (request.ClienteId is not null)
        {
            // F4.2b: la shape Empresa propia→Cliente vive ahora en la arista
            // unificada, donde el mismo ClienteId también aparece en la shape
            // Subcontrata→Cliente — de ahí el discriminador sobre la
            // proveedora, además del filtro de vigencia.
            var empresaIdsAsociadas = dbContext.RelacionesEmpresariales
                .Where(r => r.ClienteId == request.ClienteId && r.VigenciaHasta == null)
                .Join(dbContext.Empresas.Where(e => e.EsPropia),
                    r => r.ProveedoraId, e => e.Id, (r, e) => e.Id);

            consulta = consulta.Where(e => empresaIdsAsociadas.Contains(e.Id));
        }

        return await consulta
            .OrderBy(e => e.RazonSocial)
            .Select(e => new EmpresaSelectorDto(e.Id, e.RazonSocial))
            .ToListAsync(cancellationToken);
    }
}
