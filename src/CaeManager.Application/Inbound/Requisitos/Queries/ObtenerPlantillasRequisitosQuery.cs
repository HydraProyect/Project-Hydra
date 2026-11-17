using CaeManager.Application.Inbound.Autorizacion;
using CaeManager.Application.Common;
using CaeManager.Application.Tenants.Encargo;
using CaeManager.Domain.Inbound.Requisitos;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Inbound.Requisitos.Queries;

/// <summary>
/// Las plantillas de requisitos del Tenant TCE que ve quien pregunta (<see cref="PlantillaRequisitosAutorizacion"/>),
/// paginadas y ordenadas en el servidor (R-10): por tipo, fecha de alta e Id.
/// </summary>
[AreaDelEncargo("TiposDocumento")]
public record ObtenerPlantillasRequisitosQuery(
    TipoPlantillaRequisitos? Tipo = null, bool IncluirRetiradas = false, int Pagina = 1, int TamanoPagina = 25)
    : IRequest<ResultadoPaginado<PlantillaRequisitosResumenDto>>;

public class ObtenerPlantillasRequisitosQueryHandler(IPlantillasRequisitosQueryContext contexto, IAlcanceDatosService alcanceDatos, IAutorizacionInbound autorizacionInbound)
    : IRequestHandler<ObtenerPlantillasRequisitosQuery, ResultadoPaginado<PlantillaRequisitosResumenDto>>
{
    public const int TamanoPaginaMaximo = 100;

    public async Task<ResultadoPaginado<PlantillaRequisitosResumenDto>> Handle(
        ObtenerPlantillasRequisitosQuery request, CancellationToken cancellationToken)
    {
        if (!await autorizacionInbound.TieneAsync(CapacidadInbound.LeerInbound, cancellationToken))
            return new ResultadoPaginado<PlantillaRequisitosResumenDto>([], 0, Math.Max(1, request.Pagina), request.TamanoPagina);

        var pagina = Math.Max(1, request.Pagina);
        var tamano = Math.Clamp(request.TamanoPagina, 1, TamanoPaginaMaximo);

        var filtro = await PlantillaRequisitosAutorizacion.FiltroDeLecturaAsync(alcanceDatos, cancellationToken);
        if (filtro is null)
            return new ResultadoPaginado<PlantillaRequisitosResumenDto>([], 0, pagina, tamano);

        var consulta = PlantillaRequisitosAutorizacion.AplicarA(contexto.PlantillasRequisitos, filtro);
        if (request.Tipo is { } tipo)
            consulta = consulta.Where(p => p.Tipo == tipo);
        if (!request.IncluirRetiradas)
            consulta = consulta.Where(p => p.Activa);

        var total = await consulta.CountAsync(cancellationToken);
        var elementos = await consulta
            .OrderBy(p => p.Tipo).ThenBy(p => p.CreadoEnUtc).ThenBy(p => p.Id)
            .Skip((pagina - 1) * tamano).Take(tamano)
            .Select(p => new PlantillaRequisitosResumenDto(
                p.Id,
                p.Tipo,
                (p.ActividadId ?? p.RiesgoId ?? p.CentroId ?? p.ClienteEmpresarialId)!.Value,
                p.Activa,
                p.Requisitos.Count(r => r.Activo),
                p.Version))
            .ToListAsync(cancellationToken);

        return new ResultadoPaginado<PlantillaRequisitosResumenDto>(elementos, total, pagina, tamano);
    }
}
