using CaeManager.Domain.Comunicaciones;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.Comunicaciones.Recursos;
using Microsoft.Extensions.Localization;

namespace CaeManager.Web.Features.Comunicaciones;

/// <summary>
/// Cómo se ve cada <see cref="EstadoConversacion"/> en la bandeja: el texto del chip y del selector, y el tono del
/// <see cref="Badge"/>. Una sola fuente para la fila de la lista, la cabecera del detalle y los selectores, para que un estado
/// nuevo no se quede con el nombre del enum en pantalla («EsperandoAlCliente») en uno de ellos.
/// Los dos estados de espera usan el tono de advertencia del sistema de diseño: piden atención del Gestor CAE sin ser un error;
/// se distinguen por el texto, no por un color nuevo.
/// </summary>
public static class EstadoConversacionPresentacion
{
    /// <summary>Orden en que se ofrecen los estados en los selectores: de más a menos viva. No es el orden del enum, que guarda valores.</summary>
    public static IReadOnlyList<EstadoConversacion> EnOrdenDeSelector { get; } =
    [
        EstadoConversacion.Abierta,
        EstadoConversacion.Pendiente,
        EstadoConversacion.EsperandoAlCliente,
        EstadoConversacion.EnEsperaDeTercero,
        EstadoConversacion.Resuelta,
        EstadoConversacion.Cerrada
    ];

    /// <summary>Los estados a los que se puede pasar desde <paramref name="actual"/>, en el orden del selector — sale de la misma máquina de estados que valida el dominio.</summary>
    public static IEnumerable<EstadoConversacion> Siguientes(EstadoConversacion actual) =>
        EnOrdenDeSelector.Where(destino => TransicionesEstadoConversacion.EstaPermitida(actual, destino));

    /// <summary>El texto del estado en el idioma de la persona: la clave del recurso es el nombre del valor del enum.</summary>
    public static string Etiqueta(IStringLocalizer<TextosEstadosConversacion> textos, EstadoConversacion estado) =>
        textos[estado.ToString()].Value;

    public static TonoBadge Tono(EstadoConversacion estado) => estado switch
    {
        EstadoConversacion.Abierta => TonoBadge.Info,
        EstadoConversacion.Pendiente => TonoBadge.Info,
        EstadoConversacion.EsperandoAlCliente => TonoBadge.Advertencia,
        EstadoConversacion.EnEsperaDeTercero => TonoBadge.Advertencia,
        _ => TonoBadge.Neutro
    };
}
