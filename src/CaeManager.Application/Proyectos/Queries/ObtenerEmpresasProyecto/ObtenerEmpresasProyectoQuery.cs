using CaeManager.Application.Centros;
using CaeManager.Application.Common;
using CaeManager.Application.Documentos;
using CaeManager.Application.Empresas;
using CaeManager.Application.Trabajadores;
using CaeManager.Domain.Documentos;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Proyectos.Queries.ObtenerEmpresasProyecto;

public record ObtenerEmpresasProyectoQuery(Guid ProyectoId) : IRequest<IReadOnlyList<EmpresaProyectoDto>>;

/// <param name="DesdeRelacionSubcontrata">Ruta contextual del empleador, no un tipo permanente de Empresa.</param>
public record EmpresaProyectoDto(Guid Id, string RazonSocial, int TecnicosActivos,
    bool DesdeEmpresaDirecta, bool DesdeRelacionSubcontrata)
{
    /// <summary>Peor estado de los documentos de la propia Empresa (TGSS, RC…). <c>null</c> si no tiene ninguno.</summary>
    public EstadoDocumento? EstadoDocumental { get; init; }

    /// <summary>Los documentos de la Empresa que explican su estado y los demás que piden atención, del más grave al menos.</summary>
    public IReadOnlyList<IncidenciaDocumentalDto> Incidencias { get; init; } = [];

    /// <summary>Documentos operativos de la Empresa, para la cifra «n de m».</summary>
    public int DocumentosRegistrados { get; init; }

    /// <summary>De ellos, los que están al día.</summary>
    public int DocumentosVigentes { get; init; }
}

/// <summary>
/// Empresas empleadoras derivadas de los técnicos activos del Proyecto, con ambas rutas contextuales
/// cuando una Empresa aparece como empleadora directa y por Relación de Subcontrata.
/// Misma autoridad de entrada que ObtenerTecnicosProyecto; también se comprueba el Centro.
/// Cada Empresa lleva el estado y la cifra de SUS documentos (no los de sus técnicos: esos son la pestaña
/// Técnicos), con el mismo cálculo que el listado de Empresas. Los query contexts conservan Tenant
/// propietario y baja lógica. No guarda Empresas en el Proyecto ni crea asignaciones.
/// </summary>
public class ObtenerEmpresasProyectoQueryHandler(
    IProyectosQueryContext proyectosContext, ITrabajadoresQueryContext trabajadoresContext,
    IEmpresasQueryContext empresasContext, ICentrosQueryContext centrosContext,
    IAlcanceDatosService alcanceDatos, ICalculoEstadoDocumentalService calculoEstadoDocumental)
    : IRequestHandler<ObtenerEmpresasProyectoQuery, IReadOnlyList<EmpresaProyectoDto>>
{
    public async Task<IReadOnlyList<EmpresaProyectoDto>> Handle(ObtenerEmpresasProyectoQuery request, CancellationToken cancellationToken)
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
            select new { EmpresaId = empresa.Id, empresa.RazonSocial, TrabajadorId = trabajador.Id, DesdeRelacionSubcontrata = trabajador.EmpresaId == null })
            .ToListAsync(cancellationToken);
        if (participantes.Count == 0) return [];

        var empresas = participantes.GroupBy(p => p.EmpresaId)
            .Select(grupo => new EmpresaProyectoDto(grupo.Key, grupo.First().RazonSocial,
                grupo.Select(p => p.TrabajadorId).Distinct().Count(),
                grupo.Any(p => !p.DesdeRelacionSubcontrata), grupo.Any(p => p.DesdeRelacionSubcontrata)))
            .OrderBy(e => e.RazonSocial).ToList();

        // Estado y cifra de los documentos de cada Empresa: dos lecturas para toda la lista.
        var ids = empresas.Select(e => e.Id).ToList();
        var estados = await calculoEstadoDocumental.CalcularPeorEstadoAsync(AmbitoAplicacion.Empresa, ids, cancellationToken);
        var desgloses = await calculoEstadoDocumental.CalcularDesgloseAsync(AmbitoAplicacion.Empresa, ids, cancellationToken);
        return empresas
            .Select(e => desgloses.TryGetValue(e.Id, out var desglose)
                ? e with
                {
                    EstadoDocumental = estados.TryGetValue(e.Id, out var estado) ? estado : null,
                    Incidencias = desglose.Incidencias,
                    DocumentosRegistrados = desglose.DocumentosRegistrados,
                    DocumentosVigentes = desglose.DocumentosVigentes
                }
                : e)
            .ToList();
    }
}