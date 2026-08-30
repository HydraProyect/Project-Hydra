using CaeManager.Application.Common;
using CaeManager.Web.Exportacion;
using FluentAssertions;
using Xunit;

namespace CaeManager.Web.Tests;

/// <summary>
/// Módulo 9 (auditoría 2026-08-30): paginar por lotes acota lo que EF
/// materializa de golpe, pero sin un techo agregado un tenant con
/// suficientes filas seguía pudiendo forzar un XLWorkbook sin límite en
/// memoria. Estos casos prueban que el techo corta antes de agotar la
/// fuente, no solo que el caso normal sigue funcionando.
/// </summary>
public class PaginadorExportacionTests
{
    [Fact]
    public async Task Con_menos_elementos_que_el_maximo_se_devuelven_todos()
    {
        var fuente = Enumerable.Range(1, 120).ToList();

        var resultado = await PaginadorExportacion
            .PaginarAsync(ObtenerPagina(fuente), tamanoLote: 50, maximoElementos: 1000)
            .ToListAsync();

        resultado.Should().BeEquivalentTo(fuente, opciones => opciones.WithStrictOrdering());
    }

    [Fact]
    public async Task Con_mas_elementos_que_el_maximo_se_corta_con_excepcion_sin_agotar_la_fuente()
    {
        var fuente = Enumerable.Range(1, 10_000).ToList();

        var enumerable = PaginadorExportacion.PaginarAsync(ObtenerPagina(fuente), tamanoLote: 50, maximoElementos: 120);

        var accion = async () => await enumerable.ToListAsync();

        await accion.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task El_limite_por_defecto_deja_pasar_un_lote_tipico_sin_cortar()
    {
        var fuente = Enumerable.Range(1, 2_000).ToList();

        var resultado = await PaginadorExportacion.PaginarAsync(ObtenerPagina(fuente)).ToListAsync();

        resultado.Should().HaveCount(2_000);
    }

    private static Func<int, int, Task<ResultadoPaginado<int>>> ObtenerPagina(IReadOnlyList<int> fuente) =>
        (pagina, tamanoPagina) =>
        {
            var elementos = fuente.Skip((pagina - 1) * tamanoPagina).Take(tamanoPagina).ToList();
            return Task.FromResult(new ResultadoPaginado<int>(elementos, fuente.Count, pagina, tamanoPagina));
        };
}

file static class AsyncEnumerableExtensions
{
    public static async Task<List<T>> ToListAsync<T>(this IAsyncEnumerable<T> fuente)
    {
        var resultado = new List<T>();
        await foreach (var item in fuente)
            resultado.Add(item);
        return resultado;
    }
}
