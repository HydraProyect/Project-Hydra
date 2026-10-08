using CaeManager.Domain.Documentos;
using CaeManager.Web.Components.DesignSystem;
using TonoDeFila = CaeManager.Web.Components.DesignSystem.TonoFila;

namespace CaeManager.Web.Features.Documentos;

/// <summary>Lo que el botón de una fila de documento ofrece hacer en una página 360 nueva.</summary>
public enum AccionDocumentoFicha360
{
    Renovar,
    Subir,
    Confirmar
}

/// <summary>
/// Vocabulario de estado de documento de las páginas 360 nuevas (mockups «… 360 página» del 2026-10-08):
/// «Pendiente» en vez de «Falta», «Por vencer» para Próximo y Urgente, y «Vigente» también para lo que no caduca.
/// </summary>
/// <remarks>
/// No sustituye a <see cref="EstadoDocumentoUi"/>, que sigue siendo el de listados, paneles y alertas: aquel distingue
/// Próximo de Urgente por rótulo y color; aquí los dos son «Por vencer» y el urgente se distingue por la fila teñida.
/// Como en <see cref="EstadoDocumentoUi"/>, lo desconocido no degrada a favorable.
/// </remarks>
public static class EstadoDocumentoFicha360
{
    public static string Texto(EstadoDocumento estado) => estado switch
    {
        EstadoDocumento.Vencido => "Vencido",
        EstadoDocumento.Faltante => "Pendiente",
        EstadoDocumento.EnTolerancia => "En tolerancia",
        EstadoDocumento.Urgente or EstadoDocumento.Proximo => "Por vencer",
        EstadoDocumento.SinConfirmar => "Sin confirmar",
        EstadoDocumento.Vigente or EstadoDocumento.SinCaducidad => "Vigente",
        _ => "Estado desconocido"
    };

    public static TonoBadge Tono(EstadoDocumento estado) => estado switch
    {
        EstadoDocumento.Vencido or EstadoDocumento.Faltante => TonoBadge.Peligro,
        EstadoDocumento.EnTolerancia => TonoBadge.Tolerancia,
        EstadoDocumento.Urgente or EstadoDocumento.Proximo or EstadoDocumento.SinConfirmar => TonoBadge.Advertencia,
        EstadoDocumento.Vigente or EstadoDocumento.SinCaducidad => TonoBadge.Exito,
        _ => TonoBadge.Peligro
    };

    /// <summary>
    /// Tinte de la fila: rojo para lo que ya impide (Vencido, Pendiente), ámbar para lo que está a punto
    /// (En tolerancia, Por vencer urgente). El resto de estados no tiñe.
    /// </summary>
    public static TonoDeFila? TonoFila(EstadoDocumento estado) => estado switch
    {
        EstadoDocumento.Vencido or EstadoDocumento.Faltante => TonoDeFila.Peligro,
        EstadoDocumento.EnTolerancia or EstadoDocumento.Urgente => TonoDeFila.Advertencia,
        _ => null
    };

    /// <summary>
    /// Clave del contador de estado: agrupa los estados que comparten rótulo, para que el filtro no enseñe dos
    /// «Por vencer» ni dos «Vigente».
    /// </summary>
    public static string Clave(EstadoDocumento estado) => Texto(estado);

    /// <summary>La acción que pide el estado; <c>null</c> si no pide ninguna.</summary>
    public static AccionDocumentoFicha360? Accion(EstadoDocumento estado) => estado switch
    {
        EstadoDocumento.Vencido or EstadoDocumento.EnTolerancia or EstadoDocumento.Urgente => AccionDocumentoFicha360.Renovar,
        EstadoDocumento.Faltante => AccionDocumentoFicha360.Subir,
        EstadoDocumento.SinConfirmar => AccionDocumentoFicha360.Confirmar,
        _ => null
    };
}
