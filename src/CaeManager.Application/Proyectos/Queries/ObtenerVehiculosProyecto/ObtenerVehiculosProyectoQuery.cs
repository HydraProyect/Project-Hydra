using CaeManager.Application.Centros;
using CaeManager.Application.Common;
using CaeManager.Application.Documentos;
using CaeManager.Application.Empresas;
using CaeManager.Application.Trabajadores;
using CaeManager.Application.Vehiculos;
using CaeManager.Domain.Documentos;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Proyectos.Queries.ObtenerVehiculosProyecto;

public record ObtenerVehiculosProyectoQuery(Guid ProyectoId) : IRequest<IReadOnlyList<VehiculoProyectoDto>>;

/// <param name="EmpleadorId">Empresa a la que pertenece el Vehículo, por la ruta contextual con la que participa en el Proyecto.</param>
public record VehiculoProyectoDto(Guid Id, string Nombre, string Modelo, string NumeroPlaca, Guid EmpleadorId, string EmpleadorNombre)
{
    /// <summary>Peor estado de los documentos del Vehículo. <c>null</c> si no tiene ninguno.</summary>
    public EstadoDocumento? EstadoDocumental { get; init; }

    /// <summary>Los documentos del Vehículo que explican su estado y los demás que piden atención, del más grave al menos.</summary>
    public IReadOnlyList<IncidenciaDocumentalDto> Incidencias { get; init; } = [];

    /// <summary>Documentos operativos del Vehículo, para la cifra «n de m».</summary>
    public int DocumentosRegistrados { get; init; }

    /// <summary>De ellos, los que están al día.</summary>
    public int DocumentosVigentes { get; init; }
}

/// <summary>
/// Vehículos de las Empresas que participan en el Proyecto: las empleadoras de sus técnicos activos,
/// la misma derivación de <c>ObtenerEmpresasProyecto</c>, con el mismo gate de entrada (Cliente del
/// Proyecto visible y Centro visible) y el mismo alcance de Empresas y Subcontratas visibles. Un
/// Vehículo no se asigna a un Proyecto en el modelo —y tampoco a un Centro—, así que esta lectura es
/// contextual, como la del Centro (<c>ObtenerVehiculosConActividadDeCentro</c>): no dice que el
/// Vehículo esté en la obra, sino de quién es. Además se filtra por los Vehículos visibles del
/// usuario. No guarda nada ni crea relaciones; los query contexts conservan Tenant propietario y baja lógica.
/// </summary>
public class ObtenerVehiculosProyectoQueryHandler(
    IProyectosQueryContext proyectosContext, ITrabajadoresQueryContext trabajadoresContext,
    IEmpresasQueryContext empresasContext, ICentrosQueryContext centrosContext,
    IVehiculosQueryContext vehiculosContext, IAlcanceDatosService alcanceDatos,
    ICalculoEstadoDocumentalService calculoEstadoDocumental)
    : IRequestHandler<ObtenerVehiculosProyectoQuery, IReadOnlyList<VehiculoProyectoDto>>
{
    public async Task<IReadOnlyList<VehiculoProyectoDto>> Handle(ObtenerVehiculosProyectoQuery request, CancellationToken cancellationToken)
    {
        var proyecto = await proyectosContext.Proyectos.FirstOrDefaultAsync(p => p.Id == request.ProyectoId, cancellationToken);
        if (proyecto is null || !await ProyectoAutorizacion.VisibleAsync(proyecto.ClienteId, alcanceDatos, cancellationToken))
            return [];
        if (!await alcanceDatos.CentroVisibleAsync(proyecto.CentroId, cancellationToken)) return [];
        var centro = await centrosContext.Centros.FirstOrDefaultAsync(c => c.Id == proyecto.CentroId, cancellationToken);
        if (centro is null) return [];

        var empresaIdsVisibles = await alcanceDatos.ObtenerEmpresaIdsVisiblesAsync(cancellationToken);
        var subcontrataIdsVisibles = await alcanceDatos.ObtenerSubcontrataIdsVisiblesAsync(cancellationToken);
        var participantes = await (
            from tecnico in proyectosContext.ProyectosTecnicos
            where tecnico.ProyectoId == proyecto.Id && tecnico.FechaBaja == null
            join trabajador in trabajadoresContext.Trabajadores on tecnico.TrabajadorId equals trabajador.Id
            join empresa in empresasContext.Empresas on (trabajador.EmpresaId ?? trabajador.SubcontrataId) equals (Guid?)empresa.Id
            where trabajador.EmpresaId != null
                ? empresaIdsVisibles == null || empresaIdsVisibles.Contains(empresa.Id)
                : subcontrataIdsVisibles == null || subcontrataIdsVisibles.Contains(empresa.Id)
            select new { EmpresaId = empresa.Id, empresa.RazonSocial, DesdeRelacionSubcontrata = trabajador.EmpresaId == null })
            .Distinct()
            .ToListAsync(cancellationToken);
        if (participantes.Count == 0) return [];

        var empresaIds = participantes.Where(p => !p.DesdeRelacionSubcontrata).Select(p => p.EmpresaId).Distinct().ToList();
        var subcontrataIds = participantes.Where(p => p.DesdeRelacionSubcontrata).Select(p => p.EmpresaId).Distinct().ToList();
        var nombres = participantes.GroupBy(p => p.EmpresaId).ToDictionary(g => g.Key, g => g.First().RazonSocial);

        var vehiculoIdsVisibles = await alcanceDatos.ObtenerVehiculoIdsVisiblesAsync(cancellationToken);
        var vehiculos = await vehiculosContext.Vehiculos
            .Where(v => (v.EmpresaId != null && empresaIds.Contains(v.EmpresaId.Value))
                     || (v.SubcontrataId != null && subcontrataIds.Contains(v.SubcontrataId.Value)))
            .Where(v => vehiculoIdsVisibles == null || vehiculoIdsVisibles.Contains(v.Id))
            .OrderBy(v => v.Nombre).ThenBy(v => v.NumeroPlaca).ThenBy(v => v.Id)
            .Select(v => new { v.Id, v.Nombre, v.Modelo, v.NumeroPlaca, v.EmpresaId, v.SubcontrataId })
            .ToListAsync(cancellationToken);

        // Estado y cifra de los documentos de cada Vehículo (los mismos que enseña el listado de Vehículos).
        var ids = vehiculos.Select(v => v.Id).ToList();
        var estados = await calculoEstadoDocumental.CalcularPeorEstadoAsync(AmbitoAplicacion.Vehiculo, ids, cancellationToken);
        var desgloses = await calculoEstadoDocumental.CalcularDesgloseAsync(AmbitoAplicacion.Vehiculo, ids, cancellationToken);

        return vehiculos
            .Select(v => (Vehiculo: v, EmpleadorId: v.EmpresaId ?? v.SubcontrataId!.Value))
            .Select(x =>
            {
                var dto = new VehiculoProyectoDto(x.Vehiculo.Id, x.Vehiculo.Nombre, x.Vehiculo.Modelo, x.Vehiculo.NumeroPlaca,
                    x.EmpleadorId, nombres.GetValueOrDefault(x.EmpleadorId, string.Empty));
                return desgloses.TryGetValue(x.Vehiculo.Id, out var desglose)
                    ? dto with
                    {
                        EstadoDocumental = estados.TryGetValue(x.Vehiculo.Id, out var estado) ? estado : null,
                        Incidencias = desglose.Incidencias,
                        DocumentosRegistrados = desglose.DocumentosRegistrados,
                        DocumentosVigentes = desglose.DocumentosVigentes
                    }
                    : dto;
            })
            .ToList();
    }
}
