using CaeManager.Domain.CatalogoBase;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.CatalogoBase.Queries;

/// <summary>
/// El catálogo base de Plataforma vigente (B-13): las Actividades activas con sus códigos oficiales y los riesgos
/// activos con los requisitos de base activos que proponen. Es de solo lectura y el mismo para todos los Tenants; lo
/// lee la identidad de runtime (<c>cae_app_runtime</c>), con cualquier Tenant o sin él. Una Sesión Privilegiada de
/// Soporte TALVEG (rol <c>cae_app_soporte</c>) no lo lee: la migración le revoca todo, igual que en FestivosOficiales.
/// Hasta que un técnico habilitado revise el contenido (CA-7), está vacío.
/// </summary>
public record ObtenerCatalogoBaseQuery : IRequest<CatalogoBaseDto>;

public record CatalogoBaseDto(IReadOnlyList<ActividadCatalogoDto> Actividades, IReadOnlyList<RiesgoCatalogoDto> Riesgos);

public record ActividadCatalogoDto(Guid Id, string Codigo, string Nombre, IReadOnlyList<CodigoOficialDto> CodigosOficiales);

public record CodigoOficialDto(SistemaCodigoOficial Sistema, string Codigo);

public record RiesgoCatalogoDto(Guid Id, string Codigo, string Nombre, bool EsOtro, IReadOnlyList<RequisitoBaseDto> Requisitos);

public record RequisitoBaseDto(Guid Id, string Codigo, string Nombre, string Fundamento, NivelExigencia Nivel);

public class ObtenerCatalogoBaseQueryHandler(ICatalogoBaseQueryContext contexto)
    : IRequestHandler<ObtenerCatalogoBaseQuery, CatalogoBaseDto>
{
    public async Task<CatalogoBaseDto> Handle(ObtenerCatalogoBaseQuery request, CancellationToken cancellationToken)
    {
        var actividades = await contexto.CatalogoActividades
            .Where(a => a.Activa)
            .Select(a => new { a.Id, a.Codigo, a.Nombre })
            .ToListAsync(cancellationToken);

        var idsActividades = actividades.Select(a => a.Id).ToList();
        var codigos = (await contexto.CatalogoActividadesCodigosOficiales
                .Where(c => idsActividades.Contains(c.ActividadId))
                .Select(c => new { c.ActividadId, c.Sistema, c.Codigo })
                .ToListAsync(cancellationToken))
            .ToLookup(c => c.ActividadId);

        var riesgos = await contexto.CatalogoRiesgos
            .Where(r => r.Activo)
            .Select(r => new { r.Id, r.Codigo, r.Nombre, r.EsOtro })
            .ToListAsync(cancellationToken);

        var idsRiesgos = riesgos.Select(r => r.Id).ToList();
        var requisitosPorRiesgo = (await (
                from regla in contexto.CatalogoReglasRiesgoRequisito
                join requisito in contexto.CatalogoRequisitosBase on regla.RequisitoId equals requisito.Id
                where requisito.Activo && idsRiesgos.Contains(regla.RiesgoId)
                select new { regla.RiesgoId, requisito.Id, requisito.Codigo, requisito.Nombre, requisito.Fundamento, requisito.Nivel })
                .ToListAsync(cancellationToken))
            .ToLookup(r => r.RiesgoId);

        return new CatalogoBaseDto(
            actividades
                .OrderBy(a => a.Nombre, StringComparer.CurrentCulture).ThenBy(a => a.Codigo, StringComparer.Ordinal)
                .Select(a => new ActividadCatalogoDto(a.Id, a.Codigo, a.Nombre,
                    codigos[a.Id].OrderBy(c => c.Sistema).ThenBy(c => c.Codigo, StringComparer.Ordinal)
                        .Select(c => new CodigoOficialDto(c.Sistema, c.Codigo)).ToList()))
                .ToList(),
            riesgos
                // La opción «otro» va siempre la última, después de los riesgos con nombre.
                .OrderBy(r => r.EsOtro).ThenBy(r => r.Nombre, StringComparer.CurrentCulture).ThenBy(r => r.Codigo, StringComparer.Ordinal)
                .Select(r => new RiesgoCatalogoDto(r.Id, r.Codigo, r.Nombre, r.EsOtro,
                    requisitosPorRiesgo[r.Id].OrderBy(q => q.Nivel).ThenBy(q => q.Nombre, StringComparer.CurrentCulture)
                        .Select(q => new RequisitoBaseDto(q.Id, q.Codigo, q.Nombre, q.Fundamento, q.Nivel)).ToList()))
                .ToList());
    }
}
