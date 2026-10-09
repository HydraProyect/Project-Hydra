using System.Text.Json.Serialization;

namespace CaeManager.Application.Common;

/// <summary>Envoltorio estándar para toda Query de listado (ver Project-Hydra-Negocio/tecnico/CODING_STANDARDS.md).</summary>
public record ResultadoPaginado<T>(IReadOnlyList<T> Elementos, int TotalElementos, int Pagina, int TamanoPagina)
{
    public int TotalPaginas => TamanoPagina == 0 ? 0 : (int)Math.Ceiling(TotalElementos / (double)TamanoPagina);

    /// <summary>
    /// Filas por estado de la lista con todos los filtros de la petición MENOS el de estado, para la franja de
    /// estado del listado: cada cifra dice cuántas filas quedarían al filtrar solo por ese estado. La clave es el
    /// nombre del estado de código (el mismo valor que viaja en la URL). <c>null</c> si la consulta no los calcula
    /// o no se le pidieron: no es «cero filas».
    /// </summary>
    public IReadOnlyDictionary<string, int>? RecuentosPorEstado { get; init; }

    /// <summary>
    /// Filas que pasan los demás filtros, sin el de estado. Solo lo rellenan las consultas cuyos recuentos NO
    /// suman el total (una misma fila cuenta en varios estados, como en Clientes empresariales); donde los
    /// estados parten la lista, el total es la suma de <see cref="RecuentosPorEstado"/> y esto queda en <c>null</c>.
    /// </summary>
    public int? TotalSinFiltroDeEstado { get; init; }

    /// <summary>
    /// Resumen de cada grupo de la lista AGRUPADA, calculado sobre todas las filas que pasan los filtros de la
    /// petición (también el de estado) y el alcance del usuario, no solo sobre la página: es lo que la cabecera de
    /// grupo del listado enseña como total y como recuento por estado. La clave es la del grupo (en Centros, el Id
    /// del Cliente empresarial). <c>null</c> si la consulta no lo calcula o no se le pidió: no es «ningún grupo».
    ///
    /// <para>
    /// No viaja en el JSON de la API pública (<c>/api/v1</c> devuelve este envoltorio tal cual): es un dato de
    /// presentación del listado agrupado, no parte de ese contrato.
    /// </para>
    /// </summary>
    [JsonIgnore]
    public IReadOnlyDictionary<string, ResumenDeGrupo>? ResumenPorGrupo { get; init; }
}

/// <summary>
/// Un grupo de un listado agrupado, entero: cuántas filas tiene y cuántas en cada estado.
/// </summary>
/// <param name="Total">Filas del grupo que pasan los filtros y el alcance, en todas las páginas.</param>
/// <param name="PorEstado">
/// Filas por estado de código (la misma clave que <see cref="ResultadoPaginado{T}.RecuentosPorEstado"/>). Solo
/// lleva los estados con alguna fila; su suma es <paramref name="Total"/>.
/// </param>
public record ResumenDeGrupo(int Total, IReadOnlyDictionary<string, int> PorEstado);
