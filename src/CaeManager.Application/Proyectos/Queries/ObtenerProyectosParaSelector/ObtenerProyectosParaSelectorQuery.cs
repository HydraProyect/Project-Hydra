using CaeManager.Application.Centros;
using CaeManager.Application.Common;
using CaeManager.Application.Empresas;
using CaeManager.Application.Proyectos;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Proyectos.Queries.ObtenerProyectosParaSelector;

/// <summary>
/// Lista para poblar el selector "¿A quién pertenece?" de Documentos cuando
/// el ámbito es Proyecto. Igual que el selector de Cliente (ver
/// <see cref="Clientes.Queries.ObtenerClientesParaSelector.ObtenerClientesParaSelectorQuery"/>),
/// se acota al alcance del usuario — un Proyecto pertenece a un Cliente
/// concreto, no tiene sentido ofrecer el de otro Gestor CAE.
/// </summary>
public record ObtenerProyectosParaSelectorQuery : IRequest<IReadOnlyList<ProyectoSelectorDto>>;

public record ProyectoSelectorDto(Guid Id, string Nombre, string ClienteRazonSocial, string CentroNombre, bool EstaAbierto);

public class ObtenerProyectosParaSelectorQueryHandler(ICentrosQueryContext centrosContext, IEmpresasQueryContext empresasContext, IProyectosQueryContext proyectosContext, IAlcanceDatosService alcanceDatos)
    : IRequestHandler<ObtenerProyectosParaSelectorQuery, IReadOnlyList<ProyectoSelectorDto>>
{
    public async Task<IReadOnlyList<ProyectoSelectorDto>> Handle(
        ObtenerProyectosParaSelectorQuery request, CancellationToken cancellationToken)
    {
        var consulta =
            from proyecto in proyectosContext.Proyectos
            join cliente in empresasContext.Empresas on proyecto.ClienteId equals cliente.Id
            join centro in centrosContext.Centros on proyecto.CentroId equals centro.Id
            select new { proyecto, cliente.RazonSocial, centro.Nombre };

        var clienteIdsVisibles = await alcanceDatos.ObtenerClienteIdsVisiblesAsync(cancellationToken);
        if (clienteIdsVisibles is not null)
            consulta = consulta.Where(x => clienteIdsVisibles.Contains(x.proyecto.ClienteId));

        return await consulta
            .OrderBy(x => x.RazonSocial).ThenBy(x => x.proyecto.Nombre)
            .Select(x => new ProyectoSelectorDto(x.proyecto.Id, x.proyecto.Nombre, x.RazonSocial, x.Nombre, x.proyecto.FechaCierreReal == null))
            .ToListAsync(cancellationToken);
    }
}
