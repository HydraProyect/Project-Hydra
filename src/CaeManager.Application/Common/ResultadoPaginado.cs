namespace CaeManager.Application.Common;

/// <summary>Envoltorio estándar para toda Query de listado (ver CODING_STANDARDS.md).</summary>
public record ResultadoPaginado<T>(IReadOnlyList<T> Elementos, int TotalElementos, int Pagina, int TamanoPagina)
{
    public int TotalPaginas => TamanoPagina == 0 ? 0 : (int)Math.Ceiling(TotalElementos / (double)TamanoPagina);
}
