using CaeManager.Application.Centros;
using CaeManager.Application.Common;
using CaeManager.Application.Integraciones;
using CaeManager.Domain.Centros;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Documentos.Queries.ObtenerPlataformasEnUso;

/// <summary>
/// Las plataformas CAE que de verdad se usan: las que tienen algún canal de gestión en un Centro que el usuario
/// ve. Son las opciones del filtro «Plataforma» del listado de Documentos; el catálogo completo
/// (<c>ObtenerProveedoresPlataformaCaeQuery</c>) ofrecería decenas de plataformas sin ningún Documento.
/// </summary>
public record ObtenerPlataformasEnUsoQuery : IRequest<IReadOnlyList<PlataformaEnUsoDto>>;

public record PlataformaEnUsoDto(Guid Id, string Nombre);

public class ObtenerPlataformasEnUsoQueryHandler(
    ICentrosQueryContext centrosContext,
    IProveedoresPlataformaCaeQueryContext proveedoresContext,
    IAlcanceDatosService alcanceDatos)
    : IRequestHandler<ObtenerPlataformasEnUsoQuery, IReadOnlyList<PlataformaEnUsoDto>>
{
    public async Task<IReadOnlyList<PlataformaEnUsoDto>> Handle(ObtenerPlataformasEnUsoQuery request, CancellationToken cancellationToken)
    {
        var centroIdsVisibles = await alcanceDatos.ObtenerCentroIdsVisiblesAsync(cancellationToken);

        var canales = centrosContext.CanalesGestionDocumental
            .Where(c => c.Tipo == TipoCanalGestion.Plataforma && c.ProveedorPlataformaCaeId != null);
        if (centroIdsVisibles is not null)
            canales = canales.Where(c => centroIdsVisibles.Contains(c.CentroId));

        var proveedorIds = await canales
            .Select(c => c.ProveedorPlataformaCaeId!.Value)
            .Distinct()
            .ToListAsync(cancellationToken);
        if (proveedorIds.Count == 0)
            return [];

        return await proveedoresContext.ProveedoresPlataformaCae
            .Where(p => proveedorIds.Contains(p.Id))
            .OrderBy(p => p.Nombre)
            .Select(p => new PlataformaEnUsoDto(p.Id, p.Nombre))
            .ToListAsync(cancellationToken);
    }
}
