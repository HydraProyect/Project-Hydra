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
}
