using CaeManager.Domain.Centros;
using CaeManager.Web.Components.DesignSystem;

namespace CaeManager.Web.Features.Centros;

/// <summary>
/// Traduce EstadoCentro a color/etiqueta. Vive en un solo sitio para que la
/// tabla de Centros y el Workspace nunca puedan mostrar el mismo estado con
/// colores o textos distintos — mismo criterio que EstadoDocumentoUi.
/// </summary>
public static class EstadoCentroUi
{
    public static TonoBadge Tono(EstadoCentro estado) => estado switch
    {
        EstadoCentro.Vigente => TonoBadge.Exito,
        EstadoCentro.Proximo => TonoBadge.Advertencia,
        EstadoCentro.Urgente => TonoBadge.Peligro,
        EstadoCentro.Vencido => TonoBadge.Peligro,
        EstadoCentro.Faltante => TonoBadge.Peligro,
        EstadoCentro.Bloqueado => TonoBadge.Peligro,
        _ => TonoBadge.Neutro
    };

    /// <summary>
    /// Opciones del filtro de estado de <c>/centros</c>, de peor a mejor: lo
    /// que el gestor busca cuando filtra es lo que le urge, no lo que está en
    /// orden. Se derivan de <see cref="Texto"/> para que un cambio de
    /// etiqueta no tenga que replicarse aquí.
    /// </summary>
    public static IReadOnlyList<OpcionEstado> Opciones { get; } =
        new[]
        {
            EstadoCentro.Bloqueado,
            EstadoCentro.Faltante,
            EstadoCentro.Vencido,
            EstadoCentro.Urgente,
            EstadoCentro.Proximo,
            EstadoCentro.Vigente
        }
        .Select(e => new OpcionEstado(e.ToString(), Texto(e)))
        .ToList();

    public static string Texto(EstadoCentro estado) => estado switch
    {
        EstadoCentro.Vigente => "Vigente",
        EstadoCentro.Proximo => "Próximo",
        EstadoCentro.Urgente => "Urgente",
        EstadoCentro.Vencido => "Vencido",
        EstadoCentro.Faltante => "Falta documentación",
        EstadoCentro.Bloqueado => "Bloqueado",
        _ => "Vigente"
    };
}
