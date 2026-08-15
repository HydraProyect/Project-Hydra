using CaeManager.Application.Common;

namespace CaeManager.Web.Exportacion;

/// <summary>
/// Pagina cualquier Query de listado en lotes acotados en vez de pedir todo
/// de una vez con <c>TamanoPagina: int.MaxValue</c> — el patrón que usaban
/// los endpoints de exportación a Excel (Asignaciones, Auditoría, Centros,
/// Clientes, Empresas, Incidencias) y que la auditoría de madurez del 01-08
/// señaló como vector de presión de memoria (Horizonte 2.7 del plan). Evita
/// materializar toda la tabla del tenant en un único <c>List&lt;T&gt;</c> a
/// la vez que se construye el libro de Excel.
/// </summary>
public static class PaginadorExportacion
{
    /// <summary>
    /// Tamaño de lote razonable para exports: acota lo que EF materializa de
    /// golpe sin disparar el número de consultas para el tamaño típico de
    /// una lista de tenant.
    /// </summary>
    public const int TamanoLotePorDefecto = 500;

    public static async IAsyncEnumerable<T> PaginarAsync<T>(
        Func<int, int, Task<ResultadoPaginado<T>>> obtenerPagina,
        int tamanoLote = TamanoLotePorDefecto)
    {
        var pagina = 1;
        while (true)
        {
            var resultado = await obtenerPagina(pagina, tamanoLote);
            foreach (var item in resultado.Elementos)
                yield return item;

            if (resultado.Elementos.Count < tamanoLote)
                yield break;

            pagina++;
        }
    }
}
