using CaeManager.Application.Common;
using CaeManager.Application.Vehiculos;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Vehiculos.Queries.ObtenerVehiculosParaSelector;

/// <summary>
/// Lista para el selector "¿A quién pertenece?" de Documentos cuando el ámbito es Vehículo.
/// Acotada al alcance de datos del usuario
/// (<see cref="IAlcanceDatosService.ObtenerVehiculoIdsVisiblesAsync"/>), igual que los
/// selectores de Cliente empresarial, Empresa y Proyecto: su único consumidor cuelga un Documento
/// de un Vehículo ya existente, y ofrecer uno fuera de la Asignación de Cartera del Gestor CAE
/// enseñaría su matrícula. Sin restricción para los roles con acceso total.
/// </summary>
public record ObtenerVehiculosParaSelectorQuery : IRequest<IReadOnlyList<VehiculoSelectorDto>>;

public record VehiculoSelectorDto(Guid Id, string Nombre, string NumeroPlaca);

public class ObtenerVehiculosParaSelectorQueryHandler(IVehiculosQueryContext dbContext, IAlcanceDatosService alcanceDatos)
    : IRequestHandler<ObtenerVehiculosParaSelectorQuery, IReadOnlyList<VehiculoSelectorDto>>
{
    public async Task<IReadOnlyList<VehiculoSelectorDto>> Handle(
        ObtenerVehiculosParaSelectorQuery request, CancellationToken cancellationToken)
    {
        var consulta = dbContext.Vehiculos.AsQueryable();

        var vehiculoIdsVisibles = await alcanceDatos.ObtenerVehiculoIdsVisiblesAsync(cancellationToken);
        if (vehiculoIdsVisibles is not null)
            consulta = consulta.Where(v => vehiculoIdsVisibles.Contains(v.Id));

        return await consulta
            .OrderBy(v => v.Nombre)
            .Select(v => new VehiculoSelectorDto(v.Id, v.Nombre, v.NumeroPlaca))
            .ToListAsync(cancellationToken);
    }
}
