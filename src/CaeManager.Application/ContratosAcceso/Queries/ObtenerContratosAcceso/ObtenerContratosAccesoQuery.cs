using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.ContratosAcceso;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.ContratosAcceso.Queries.ObtenerContratosAcceso;

/// <param name="CentroId">Solo los contratos de este Centro de trabajo.</param>
/// <param name="EmpresaContratistaId">Solo los contratos de esta Empresa contratista.</param>
/// <param name="SoloVigentesEn">Solo los vigentes ese día, con el criterio de <see cref="ContratoAcceso.EstaVigenteEn"/>.</param>
public record ObtenerContratosAccesoQuery(Guid? CentroId = null, Guid? EmpresaContratistaId = null, DateOnly? SoloVigentesEn = null)
    : IRequest<IReadOnlyList<ContratoAccesoDto>>;

/// <summary>
/// Listado acotado por el mismo alcance que <c>ObtenerContratoAccesoPorIdQuery</c>: los Clientes empresariales visibles
/// del actor (sin restricción para acceso total). Ordenado por inicio, el más reciente primero.
/// </summary>
public class ObtenerContratosAccesoQueryHandler(
    IContratosAccesoQueryContext contratosContext, IAlcanceDatosService alcanceDatos, IPoliticaNotaInterna politicaNotaInterna)
    : IRequestHandler<ObtenerContratosAccesoQuery, IReadOnlyList<ContratoAccesoDto>>
{
    public async Task<IReadOnlyList<ContratoAccesoDto>> Handle(ObtenerContratosAccesoQuery request, CancellationToken cancellationToken)
    {
        var consulta = contratosContext.ContratosAcceso;

        var clienteIdsVisibles = await alcanceDatos.ObtenerClienteIdsVisiblesAsync(cancellationToken);
        if (clienteIdsVisibles is not null)
            consulta = consulta.Where(c => clienteIdsVisibles.Contains(c.ClienteEmpresarialId));

        if (request.CentroId is { } centroId)
            consulta = consulta.Where(c => c.CentroId == centroId);
        if (request.EmpresaContratistaId is { } empresaContratistaId)
            consulta = consulta.Where(c => c.EmpresaContratistaId == empresaContratistaId);
        if (request.SoloVigentesEn is { } fecha)
            consulta = consulta.Where(ContratoAcceso.EstaVigenteEn(fecha));

        var contratos = await consulta
            .OrderByDescending(c => c.FechaInicio).ThenBy(c => c.Id)
            .ToListAsync(cancellationToken);

        var conNotaInterna = await politicaNotaInterna.PuedeLeerAsync(cancellationToken);
        var hoy = DiaDeNegocio.Hoy();

        return contratos
            .Select(c => new ContratoAccesoDto(
                c.Id, c.ClienteEmpresarialId, c.CentroId, c.EmpresaContratistaId, c.FechaInicio, c.FechaFin, c.FechaCierreReal,
                c.EstaAbierto, c.VigenteEn(hoy), conNotaInterna ? c.Notas : null, c.Version))
            .ToList();
    }
}
