using CaeManager.Application.Centros.Queries.ObtenerCentros;
using Microsoft.Extensions.Localization;

namespace CaeManager.Web.Features.Centros;

/// <summary>
/// Cómo se dice, en la lista de Centros y en el Centro 360, qué documentos están pendientes en la plataforma CAE del Centro
/// (decisión del propietario, 2026-10-10). El motivo visible NOMBRA el documento, de quién es y qué le falta en la plataforma
/// —«RNT — Empresa — sin subir a la plataforma»—, no solo cuenta: el Gestor CAE tiene que ver qué bloquea sin abrir nada.
/// Con más de uno nombra el primero y cuenta el resto; la ventana de contexto los lista todos. El texto de cada línea es la
/// descripción de la causa del Centro (<c>CalculoEstadoCentroService</c>): aquí no se recalcula nada.
/// </summary>
public static class TextoPendientesEnPlataforma
{
    /// <summary>El primer pendiente con su documento y su estado en la plataforma, y «y N más» si hay más.</summary>
    public static string Motivo(IStringLocalizer textos, IReadOnlyList<IncidenciaCentroDto> pendientes) =>
        pendientes.Count == 1
            ? pendientes[0].Descripcion
            : textos["MotivoPendientesPlataformaYMas", pendientes[0].Descripcion, pendientes.Count - 1].Value;

    /// <summary>«1 documento pendiente en la plataforma», «N documentos pendientes en la plataforma».</summary>
    public static string Titulo(IStringLocalizer textos, int cantidad) =>
        cantidad == 1 ? textos["TituloPendientesPlataformaUno"].Value : textos["TituloPendientesPlataformaVarios", cantidad].Value;

    /// <summary>Nombre accesible de la ventana: empieza por lo que se ve (WCAG 2.5.3) y sigue con el título.</summary>
    public static string Etiqueta(IStringLocalizer textos, IReadOnlyList<IncidenciaCentroDto> pendientes) =>
        $"{Motivo(textos, pendientes)}. {Titulo(textos, pendientes.Count)}";
}
