using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.ContratosAcceso.Queries.ObtenerContratoAccesoPorId;

public record ObtenerContratoAccesoPorIdQuery(Guid Id) : IRequest<ContratoAccesoDto?>;

/// <summary>
/// Mismo criterio de alcance que <c>ObtenerProyectoPorIdQuery</c>: el contrato existe para quien ve su Cliente
/// empresarial (<see cref="ContratoAccesoAutorizacion"/>); fuera de ese alcance responde null, igual que si no existiera.
/// </summary>
public class ObtenerContratoAccesoPorIdQueryHandler(
    IContratosAccesoQueryContext contratosContext, IAlcanceDatosService alcanceDatos, IPoliticaNotaInterna politicaNotaInterna)
    : IRequestHandler<ObtenerContratoAccesoPorIdQuery, ContratoAccesoDto?>
{
    public async Task<ContratoAccesoDto?> Handle(ObtenerContratoAccesoPorIdQuery request, CancellationToken cancellationToken)
    {
        var contrato = await contratosContext.ContratosAcceso.FirstOrDefaultAsync(c => c.Id == request.Id, cancellationToken);
        if (contrato is null || !await ContratoAccesoAutorizacion.VisibleAsync(contrato.ClienteEmpresarialId, alcanceDatos, cancellationToken))
            return null;

        var conNotaInterna = await politicaNotaInterna.PuedeLeerAsync(cancellationToken);
        var hoy = DiaDeNegocio.Hoy();

        return new ContratoAccesoDto(
            contrato.Id, contrato.ClienteEmpresarialId, contrato.CentroId, contrato.EmpresaContratistaId,
            contrato.FechaInicio, contrato.FechaFin, contrato.FechaCierreReal, contrato.EstaAbierto, contrato.VigenteEn(hoy),
            conNotaInterna ? contrato.Notas : null, contrato.Version);
    }
}
