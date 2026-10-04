using CaeManager.Application.Bandeja.Queries.ObtenerMiTrabajoAgregado;

namespace CaeManager.Web.Tests;

/// <summary>
/// Trocea un agregado ya construido en las partes que entrega <see cref="ObtenerMiTrabajoPorPartesQuery"/>: apertura y
/// una por Tenant (consultado o no), en el orden del agregado. Solo para los mediadores falsos de las pruebas de la página.
/// </summary>
internal static class PartesDeMiTrabajo
{
    public static async IAsyncEnumerable<ParteMiTrabajoDto> De(
        MiTrabajoAgregadoDto datos, Func<CancellationToken, Task>? entreParte = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var total = datos.Tenants.Count + datos.NoConsultados.Count;
        yield return new ParteMiTrabajoDto(total, 0);
        var completados = 0;
        foreach (var tenant in datos.Tenants)
        {
            if (entreParte is not null) await entreParte(cancellationToken);
            yield return new ParteMiTrabajoDto(total, ++completados, tenant);
        }

        foreach (var noConsultado in datos.NoConsultados)
        {
            if (entreParte is not null) await entreParte(cancellationToken);
            yield return new ParteMiTrabajoDto(total, ++completados, NoConsultado: noConsultado);
        }
    }
}
