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

    /// <summary>
    /// Techo agregado por exportación (hallazgo del Módulo 9, auditoría
    /// 2026-08-30): paginar por lotes acota cuánto materializa EF de golpe,
    /// pero sin un techo total un tenant con cientos de miles de filas sigue
    /// pudiendo forzar un <c>XLWorkbook</c> de ClosedXML del mismo tamaño en
    /// memoria — ClosedXML no tiene modo forward-only, así que el libro
    /// entero vive en RAM hasta el <c>SaveAs</c>. Un valor muy por encima de
    /// cualquier tenant real de hoy, pero finito.
    /// </summary>
    public const int MaximoElementosPorDefecto = 50_000;

    public static async IAsyncEnumerable<T> PaginarAsync<T>(
        Func<int, int, Task<ResultadoPaginado<T>>> obtenerPagina,
        int tamanoLote = TamanoLotePorDefecto,
        int maximoElementos = MaximoElementosPorDefecto)
    {
        var pagina = 1;
        var total = 0;
        while (true)
        {
            var resultado = await obtenerPagina(pagina, tamanoLote);
            foreach (var item in resultado.Elementos)
            {
                if (++total > maximoElementos)
                    throw new InvalidOperationException(
                        $"La exportación supera el máximo de {maximoElementos} filas — acota el filtro antes de exportar.");

                yield return item;
            }

            if (resultado.Elementos.Count < tamanoLote)
                yield break;

            pagina++;
        }
    }
}
