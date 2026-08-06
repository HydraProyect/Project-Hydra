using CaeManager.Application.Asignaciones;
using CaeManager.Application.Centros;
using CaeManager.Application.Common;
using CaeManager.Application.Documentos;
using CaeManager.Application.Empresas;
using CaeManager.Application.Trabajadores;
using CaeManager.Domain.Documentos;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Empresas.Queries.ObtenerEmpresas;

/// <summary>
/// <paramref name="EstadoDocumental"/> es el filtro de estado de la pantalla:
/// una Empresa no tiene estado propio, se deriva del peor estado de vigencia
/// de sus Documentos (ver <see cref="ICalculoEstadoDocumentalService"/>).
/// </summary>
public record ObtenerEmpresasQuery(
    string? Busqueda, int Pagina = 1, int TamanoPagina = 20,
    string? OrdenarPor = null, bool Descendente = false, string? EstadoDocumental = null)
    : IRequest<ResultadoPaginado<EmpresaListaDto>>;

/// <param name="CumplimientoPorcentaje">
/// % de cumplimiento agregado de la Empresa (Centro 360, PLAN-EJECUCION-UX.md
/// § 0.8/0.11) — mismo cálculo que <c>ObtenerCumplimientoEmpresaQuery</c>,
/// batcheado aquí para toda la página en vez de una consulta por fila.
/// <c>null</c> cuando la Empresa no tiene actividad en ningún Centro o
/// ninguno de esos Centros tiene requisitos aplicables.
/// </param>
public record EmpresaListaDto(
    Guid Id, string RazonSocial, string? Cif, DateTime CreadoEnUtc,
    EstadoDocumento? EstadoDocumental = null, int? CumplimientoPorcentaje = null);

public class ObtenerEmpresasQueryHandler(
    IEmpresasQueryContext dbContext, IAlcanceDatosService alcanceDatos,
    ICalculoEstadoDocumentalService calculoEstadoDocumental,
    IAsignacionesQueryContext asignacionesContext, ITrabajadoresQueryContext trabajadoresContext,
    ICalculoEstadoCentroService calculoEstadoCentro)
    : IRequestHandler<ObtenerEmpresasQuery, ResultadoPaginado<EmpresaListaDto>>
{
    public async Task<ResultadoPaginado<EmpresaListaDto>> Handle(ObtenerEmpresasQuery request, CancellationToken cancellationToken)
    {
        var consulta = dbContext.Empresas.AsQueryable();

        var empresaIdsVisibles = await alcanceDatos.ObtenerEmpresaIdsVisiblesAsync(cancellationToken);
        if (empresaIdsVisibles is not null)
            consulta = consulta.Where(e => empresaIdsVisibles.Contains(e.Id));

        if (!string.IsNullOrWhiteSpace(request.Busqueda))
        {
            var busqueda = request.Busqueda.ToUpper();
            consulta = consulta.Where(e => e.RazonSocial.ToUpper().Contains(busqueda));
        }

        // Filtrar u ordenar por el estado documental obliga a conocerlo de
        // todas las filas antes de paginar — ver ObtenerCentrosQuery.
        var necesitaEstadoCompleto =
            !string.IsNullOrWhiteSpace(request.EstadoDocumental) ||
            string.Equals(request.OrdenarPor, nameof(EmpresaListaDto.EstadoDocumental), StringComparison.Ordinal);

        if (necesitaEstadoCompleto)
        {
            var todas = await consulta
                .Select(e => new EmpresaListaDto(e.Id, e.RazonSocial, e.Cif, e.CreadoEnUtc))
                .ToListAsync(cancellationToken);

            var estadosTodas = await calculoEstadoDocumental.CalcularPeorEstadoAsync(
                AmbitoAplicacion.Empresa, todas.Select(e => e.Id).ToList(), cancellationToken);

            var conEstado = todas
                .Select(e => e with { EstadoDocumental = estadosTodas.GetValueOrDefault(e.Id) })
                .Where(e => EstadoDocumentalFiltro.Coincide(e.EstadoDocumental, request.EstadoDocumental));

            var ordenadas = (request.Descendente
                    ? conEstado.OrderByDescending(e => EstadoDocumentalFiltro.ClaveOrden(e.EstadoDocumental))
                    : conEstado.OrderBy(e => EstadoDocumentalFiltro.ClaveOrden(e.EstadoDocumental)))
                .ThenBy(e => e.RazonSocial).ThenBy(e => e.Id)
                .ToList();

            var paginaConEstado = ordenadas.Skip((request.Pagina - 1) * request.TamanoPagina).Take(request.TamanoPagina).ToList();
            var cumplimientoConEstado = await CalcularCumplimientoPorEmpresaAsync(
                paginaConEstado.Select(e => e.Id).ToList(), cancellationToken);

            return new ResultadoPaginado<EmpresaListaDto>(
                paginaConEstado.Select(e => e with { CumplimientoPorcentaje = cumplimientoConEstado.GetValueOrDefault(e.Id) }).ToList(),
                ordenadas.Count, request.Pagina, request.TamanoPagina);
        }

        var total = await consulta.CountAsync(cancellationToken);

        // Lista blanca de columnas ordenables — ver ObtenerClientesQuery.
        var ordenada = (request.OrdenarPor, request.Descendente) switch
        {
            (nameof(EmpresaListaDto.RazonSocial), true) => consulta.OrderByDescending(e => e.RazonSocial),
            (nameof(EmpresaListaDto.Cif), false) => consulta.OrderBy(e => e.Cif),
            (nameof(EmpresaListaDto.Cif), true) => consulta.OrderByDescending(e => e.Cif),
            (nameof(EmpresaListaDto.CreadoEnUtc), false) => consulta.OrderBy(e => e.CreadoEnUtc),
            (nameof(EmpresaListaDto.CreadoEnUtc), true) => consulta.OrderByDescending(e => e.CreadoEnUtc),
            _ => consulta.OrderBy(e => e.RazonSocial)
        };
        // Desempate estable: sin un criterio total, PostgreSQL puede devolver
        // las filas empatadas en distinto orden entre una página y otra, y al
        // paginar en SQL eso hace que una fila aparezca dos veces o no
        // aparezca nunca. El Id no se ordena nunca por sí solo — solo cierra
        // el orden que haya elegido el usuario.
        ordenada = ordenada.ThenBy(e => e.Id);

        var elementos = await ordenada
            .Skip((request.Pagina - 1) * request.TamanoPagina)
            .Take(request.TamanoPagina)
            .Select(e => new EmpresaListaDto(e.Id, e.RazonSocial, e.Cif, e.CreadoEnUtc))
            .ToListAsync(cancellationToken);

        // Solo para las de la página: los badges de la tabla, no un filtro.
        var idsPagina = elementos.Select(e => e.Id).ToList();
        var estados = await calculoEstadoDocumental.CalcularPeorEstadoAsync(AmbitoAplicacion.Empresa, idsPagina, cancellationToken);
        var cumplimiento = await CalcularCumplimientoPorEmpresaAsync(idsPagina, cancellationToken);

        return new ResultadoPaginado<EmpresaListaDto>(
            elementos.Select(e => e with
            {
                EstadoDocumental = estados.GetValueOrDefault(e.Id),
                CumplimientoPorcentaje = cumplimiento.GetValueOrDefault(e.Id)
            }).ToList(),
            total, request.Pagina, request.TamanoPagina);
    }

    /// <summary>
    /// Mismo cálculo que <c>ObtenerCumplimientoEmpresaQuery</c> (Empresa →
    /// Trabajadores → Asignaciones activas → Centro, suma AlDia/Requeridos,
    /// no media de porcentajes) pero para N Empresas de una sola vez: una
    /// consulta de actividad y una llamada a
    /// <see cref="ICalculoEstadoCentroService.CalcularCumplimientoAsync"/>
    /// sobre la unión de Centros, en vez de repetir ambas por fila.
    /// </summary>
    private async Task<Dictionary<Guid, int?>> CalcularCumplimientoPorEmpresaAsync(
        IReadOnlyList<Guid> empresaIds, CancellationToken cancellationToken)
    {
        if (empresaIds.Count == 0) return new Dictionary<Guid, int?>();

        var centrosPorEmpresa = await (
            from asignacion in asignacionesContext.Asignaciones
            where asignacion.FechaBaja == null
            join trabajador in trabajadoresContext.Trabajadores on asignacion.TrabajadorId equals trabajador.Id
            where trabajador.EmpresaId != null && empresaIds.Contains(trabajador.EmpresaId.Value)
            select new { EmpresaId = trabajador.EmpresaId!.Value, asignacion.CentroId })
            .Distinct()
            .ToListAsync(cancellationToken);

        var centroIds = centrosPorEmpresa.Select(x => x.CentroId).Distinct().ToList();
        var fracciones = centroIds.Count == 0
            ? new Dictionary<Guid, FraccionCumplimiento>()
            : await calculoEstadoCentro.CalcularCumplimientoAsync(centroIds, cancellationToken);

        return centrosPorEmpresa
            .GroupBy(x => x.EmpresaId)
            .ToDictionary(g => g.Key, g =>
            {
                var deLaEmpresa = g.Select(x => fracciones.GetValueOrDefault(x.CentroId, new FraccionCumplimiento(0, 0))).ToList();
                var alDia = deLaEmpresa.Sum(f => f.AlDia);
                var requeridos = deLaEmpresa.Sum(f => f.Requeridos);
                return requeridos == 0 ? (int?)null : (int)Math.Round(alDia * 100.0 / requeridos);
            });
    }
}
