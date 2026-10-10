using CaeManager.Application.Centros;
using CaeManager.Application.Common;
using CaeManager.Application.Configuracion;
using CaeManager.Application.Documentos.Queries.ObtenerDocumentos;
using CaeManager.Application.Empresas;
using CaeManager.Application.Proyectos;
using CaeManager.Application.TiposDocumento;
using CaeManager.Application.Trabajadores;
using CaeManager.Application.Vehiculos;
using CaeManager.Domain.Documentos;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Documentos.Queries.ObtenerResumenDocumentosPorTipo;

/// <summary>
/// Las cabeceras del listado de Documentos agrupado por Tipo de documento: por cada tipo con algún Documento que
/// pase los filtros de vista y el alcance del usuario, cuántos tiene y cuántos en cada estado. Describe lo que se
/// lista al abrir el grupo, que es <see cref="ObtenerDocumentosQuery"/> con estos mismos filtros y el
/// <see cref="TipoDocumentoId"/> del grupo: por eso cuenta DESPUÉS del filtro de estado (a diferencia de la franja
/// de estado, <c>ResultadoPaginado.RecuentosPorEstado</c>, que cuenta sin él).
///
/// <para>
/// Lleva exactamente los filtros de vista de <see cref="ObtenerDocumentosQuery"/> —todos sus parámetros menos el
/// orden, la paginación y el interruptor de los recuentos—, con el mismo significado; ver allí cada uno.
/// </para>
/// </summary>
public record ObtenerResumenDocumentosPorTipoQuery(
    Guid? TrabajadorId = null,
    AmbitoAplicacion? Ambito = null,
    string? Busqueda = null,
    EstadoDocumento? Estado = null,
    IReadOnlyCollection<EstadoDocumento>? Estados = null,
    Guid? PropietarioId = null,
    DateOnly? FechaVencimientoHasta = null,
    Guid? DocumentoId = null,
    Guid? TipoDocumentoId = null,
    Guid? ProveedorPlataformaCaeId = null)
    : IRequest<IReadOnlyList<ResumenDocumentosDeTipoDto>>
{
    internal FiltrosDeDocumentos Filtros() => new(
        TrabajadorId, Ambito, Busqueda, Estado, Estados, PropietarioId, FechaVencimientoHasta, DocumentoId,
        TipoDocumentoId, ProveedorPlataformaCaeId);
}

/// <summary>
/// Un grupo del listado de Documentos agrupado por Tipo de documento, entero (todas sus páginas).
/// </summary>
/// <param name="TipoDocumentoId">El grupo: el valor de <c>ObtenerDocumentosQuery.TipoDocumentoId</c> que lista sus Documentos.</param>
/// <param name="Total">Documentos del tipo que pasan los filtros y el alcance.</param>
/// <param name="PorEstado">
/// Documentos por estado. Solo lleva los estados con algún Documento; su suma es <paramref name="Total"/>.
/// </param>
public record ResumenDocumentosDeTipoDto(
    Guid TipoDocumentoId,
    string TipoDocumentoNombre,
    int Total,
    IReadOnlyDictionary<EstadoDocumento, int> PorEstado);

/// <summary>
/// Cuenta en SQL: un <c>GROUP BY</c> (tipo, clave de estado) sobre <see cref="DocumentosFiltrados.Filtrados"/>, la
/// misma consulta que pagina el listado. Nunca materializa Documentos: lo que vuelve de la base es una fila por
/// tipo y estado presentes.
/// </summary>
public class ObtenerResumenDocumentosPorTipoQueryHandler(IConfiguracionQueryContext configuracionContext, IDocumentosQueryContext documentosContext, IEmpresasQueryContext empresasContext, IProyectosQueryContext proyectosContext, ITiposDocumentoQueryContext tiposDocumentoContext, ITrabajadoresQueryContext trabajadoresContext, IVehiculosQueryContext vehiculosContext, IAlcanceDatosService alcanceDatos, ICentrosQueryContext centrosContext)
    : IRequestHandler<ObtenerResumenDocumentosPorTipoQuery, IReadOnlyList<ResumenDocumentosDeTipoDto>>
{
    public async Task<IReadOnlyList<ResumenDocumentosDeTipoDto>> Handle(
        ObtenerResumenDocumentosPorTipoQuery request, CancellationToken cancellationToken)
    {
        var documentos = await DocumentosFiltrados.ComponerAsync(
            new FuentesDeDocumentos(
                configuracionContext, documentosContext, empresasContext, proyectosContext, tiposDocumentoContext,
                trabajadoresContext, vehiculosContext, alcanceDatos, centrosContext),
            request.Filtros(),
            cancellationToken);

        // El nombre se ordena en la base, con la misma intercalación con la que el listado ordena esa columna.
        var filas = await documentos.Filtrados
            .GroupBy(documentos.ClaveDeEstado.Con((FilaDocumento x, int clave) =>
                new { x.TipoDocumentoId, x.TipoDocumentoNombre, Clave = clave }))
            .Select(grupo => new
            {
                grupo.Key.TipoDocumentoId,
                grupo.Key.TipoDocumentoNombre,
                grupo.Key.Clave,
                Filas = grupo.Count()
            })
            .OrderBy(fila => fila.TipoDocumentoNombre)
            .ThenBy(fila => fila.TipoDocumentoId)
            .ToListAsync(cancellationToken);

        return ResumenDocumentosPorTipo.Montar(
            filas.Select(fila => new RecuentoDeTipoYEstado(fila.TipoDocumentoId, fila.TipoDocumentoNombre, fila.Clave, fila.Filas)));
    }
}

/// <summary>Una fila del <c>GROUP BY</c>: cuántos Documentos de un tipo tienen una clave de estado.</summary>
internal sealed record RecuentoDeTipoYEstado(Guid TipoDocumentoId, string TipoDocumentoNombre, int ClaveDeEstado, int Filas);

internal static class ResumenDocumentosPorTipo
{
    /// <summary>
    /// Junta las filas del <c>GROUP BY</c> en un grupo por tipo y los ordena: primero el tipo cuyo peor Documento
    /// es más grave —la escala de <see cref="EstadoDocumentalFiltro.ClaveOrden"/>, la misma del orden por
    /// severidad del listado— y, a igual gravedad, en el orden en que llegan (por nombre, de la base).
    /// </summary>
    public static IReadOnlyList<ResumenDocumentosDeTipoDto> Montar(IEnumerable<RecuentoDeTipoYEstado> recuentos) =>
        recuentos
            .GroupBy(recuento => (recuento.TipoDocumentoId, recuento.TipoDocumentoNombre))
            .Select(tipo => new
            {
                PeorClave = tipo.Min(recuento => recuento.ClaveDeEstado),
                Resumen = new ResumenDocumentosDeTipoDto(
                    tipo.Key.TipoDocumentoId,
                    tipo.Key.TipoDocumentoNombre,
                    tipo.Sum(recuento => recuento.Filas),
                    tipo.ToDictionary(recuento => EstadoDe(recuento.ClaveDeEstado), recuento => recuento.Filas))
            })
            // OrderBy es estable: a igual gravedad se conserva el orden de llegada.
            .OrderBy(tipo => tipo.PeorClave)
            .Select(tipo => tipo.Resumen)
            .ToList();

    private static EstadoDocumento EstadoDe(int clave) =>
        EstadoDocumentalFiltro.EstadoDeClave(clave)
        ?? throw new InvalidOperationException($"La clave de estado {clave} no corresponde a ningún estado de un Documento.");
}
