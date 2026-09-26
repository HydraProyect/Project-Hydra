using CaeManager.Domain.Common;

namespace CaeManager.Web.Components;

/// <summary>
/// «Deshacer» de una eliminación en lote (FS-09, auditoría UX de flujos sin
/// salida, 2026-09-24): restaura uno a uno los ids que el lote sí eliminó con el
/// <c>Restaurar{Tipo}Command</c> de la página. Uno a uno y en serie porque cada
/// restauración es su propio comando (autorización, alcance y auditoría por
/// entidad) y el circuito comparte un único DbContext.
/// </summary>
public static class RestauracionEnLote
{
    public sealed record Resultado(int Restaurados, IReadOnlyList<string> Errores);

    public static async Task<Resultado> RestaurarAsync(IReadOnlyList<Guid> ids, Func<Guid, Task<Result>> restaurar)
    {
        var restaurados = 0;
        var errores = new List<string>();

        foreach (var id in ids)
        {
            var resultado = await restaurar(id);

            if (resultado.EsExitoso)
                restaurados++;
            else
                errores.Add(resultado.Error.Mensaje);
        }

        return new Resultado(restaurados, errores);
    }
}
