using CaeManager.Domain.Documentos;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.Documentos.Recursos;
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
    public static string Texto(EstadoDocumento estado) => TextosVigenciaDocumento.Texto(Clave(estado));

    public static TonoBadge Tono(EstadoDocumento estado) => estado switch
    {
        EstadoDocumento.Vencido or EstadoDocumento.Faltante => TonoBadge.Peligro,
        EstadoDocumento.EnTolerancia => TonoBadge.Tolerancia,
        EstadoDocumento.Urgente or EstadoDocumento.Proximo or EstadoDocumento.SinConfirmar => TonoBadge.Advertencia,
        // Lo que no pide nada es lo que el porcentaje cuenta como al día: se pregunta al punto único, no se copia.
        _ when CumplimientoDocumental.EsConforme(estado) => TonoBadge.Exito,
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
    /// Clave del contador de estado, que es también la de su rótulo en <see cref="TextosVigenciaDocumento"/>: agrupa
    /// los estados que comparten rótulo, para que el filtro no enseñe dos «Por vencer» ni dos «Vigente».
    /// </summary>
    public static string Clave(EstadoDocumento estado) => estado switch
    {
        EstadoDocumento.Vencido => "Ficha360Vencido",
        EstadoDocumento.Faltante => "Ficha360Pendiente",
        EstadoDocumento.EnTolerancia => "EnTolerancia",
        EstadoDocumento.Urgente or EstadoDocumento.Proximo => "Ficha360PorVencer",
        EstadoDocumento.SinConfirmar => "SinConfirmar",
        _ when CumplimientoDocumental.EsConforme(estado) => "Ficha360Vigente",
        _ => "Ficha360Desconocido"
    };

    private const string PrefijoDeClave = "Ficha360";

    /// <summary>Las claves que pueden viajar en la URL, en el orden de gravedad. «Desconocido» no es un contador que se marque.</summary>
    private static readonly string[] ClavesDeContador =
        ["Ficha360Vencido", "Ficha360Pendiente", "EnTolerancia", "Ficha360PorVencer", "SinConfirmar", "Ficha360Vigente"];

    /// <summary>Lo que <c>?estado=</c> lleva por cada contador, y la <see cref="Clave"/> a la que corresponde.</summary>
    private static readonly IReadOnlyDictionary<string, string> ClavePorValorDeUrl =
        ClavesDeContador.ToDictionary(ValorDeUrl, clave => clave, StringComparer.Ordinal);

    private static string ValorDeUrl(string clave) =>
        clave.StartsWith(PrefijoDeClave, StringComparison.Ordinal) ? clave[PrefijoDeClave.Length..] : clave;

    /// <summary>
    /// Los contadores marcados que llegan en <c>?estado=</c> (<c>Vencido,PorVencer</c>: los mismos nombres que usa
    /// Tipo de documento 360), como <see cref="Clave"/>. Se lee con <see cref="SeleccionEstados.Separar(string?)"/>; lo
    /// que no es un contador conocido no filtra.
    /// </summary>
    public static IReadOnlySet<string> ClavesDesdeUrl(string? valor) =>
        SeleccionEstados.Separar(valor)
            .Where(ClavePorValorDeUrl.ContainsKey)
            .Select(v => ClavePorValorDeUrl[v])
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>La selección de contadores tal como viaja en <c>?estado=</c>, en el orden de gravedad; <c>null</c> si no hay ninguno.</summary>
    public static string? ClavesEnUrl(IReadOnlySet<string> claves) =>
        SeleccionEstados.Unir(ClavesDeContador.Where(claves.Contains).Select(ValorDeUrl));

    /// <summary>La acción que pide el estado; <c>null</c> si no pide ninguna.</summary>
    public static AccionDocumentoFicha360? Accion(EstadoDocumento estado) => estado switch
    {
        EstadoDocumento.Vencido or EstadoDocumento.EnTolerancia or EstadoDocumento.Urgente => AccionDocumentoFicha360.Renovar,
        EstadoDocumento.Faltante => AccionDocumentoFicha360.Subir,
        EstadoDocumento.SinConfirmar => AccionDocumentoFicha360.Confirmar,
        _ => null
    };
}
