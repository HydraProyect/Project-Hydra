using CaeManager.Domain.Documentos;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.Visitas.Recursos;
using Microsoft.Extensions.Localization;

namespace CaeManager.Web.Features.Visitas;

/// <summary>
/// Vocabulario de estado de un documento en la página Visita 360 (decisión del 2026-10-08
/// para las páginas 360): «Pendiente» y no «Falta»; «Por vencer» reúne Urgente y Próximo;
/// un documento que no caduca está «Vigente». Solo decide el rótulo, el tono y la clave con
/// la que se agrupan los contadores: el estado lo calcula el Domain y su orden de gravedad
/// es <see cref="SeveridadEstadoDocumento.Rango"/>.
/// </summary>
public static class EstadoDocumentoVisitaUi
{
    /// <summary>Clave del contador: los estados que se leen igual comparten contador.</summary>
    public static string Clave(EstadoDocumento estado) => estado switch
    {
        EstadoDocumento.Vencido => "vencido",
        EstadoDocumento.Faltante => "pendiente",
        EstadoDocumento.EnTolerancia => "en-tolerancia",
        EstadoDocumento.Urgente or EstadoDocumento.Proximo => "por-vencer",
        EstadoDocumento.SinConfirmar => "sin-confirmar",
        _ => "vigente"
    };

    public static string Texto(EstadoDocumento estado, IStringLocalizer<TextosVisitas> textos) => Clave(estado) switch
    {
        "vencido" => textos["Estado360Vencido"].Value,
        "pendiente" => textos["Estado360Pendiente"].Value,
        "en-tolerancia" => textos["Estado360EnTolerancia"].Value,
        "por-vencer" => textos["Estado360PorVencer"].Value,
        "sin-confirmar" => textos["Estado360SinConfirmar"].Value,
        _ => textos["Estado360Vigente"].Value
    };

    public static TonoBadge Tono(EstadoDocumento estado) => estado switch
    {
        EstadoDocumento.Vencido or EstadoDocumento.Faltante => TonoBadge.Peligro,
        EstadoDocumento.EnTolerancia => TonoBadge.Tolerancia,
        EstadoDocumento.Urgente or EstadoDocumento.Proximo or EstadoDocumento.SinConfirmar => TonoBadge.Advertencia,
        _ => TonoBadge.Exito
    };

    /// <summary>
    /// Fila teñida: en rojo lo vencido y lo que no se ha presentado; en ámbar lo que vence
    /// con plazo corto. El resto de filas no lleva tono.
    /// </summary>
    public static TonoFila? TonoDeFila(EstadoDocumento estado) => estado switch
    {
        EstadoDocumento.Vencido or EstadoDocumento.Faltante => TonoFila.Peligro,
        EstadoDocumento.Urgente => TonoFila.Advertencia,
        _ => null
    };
}
