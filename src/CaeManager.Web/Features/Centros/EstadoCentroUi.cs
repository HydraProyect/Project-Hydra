using CaeManager.Domain.Centros;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.Documentos;
using CaeManager.Web.Features.Documentos.Recursos;

namespace CaeManager.Web.Features.Centros;

/// <summary>
/// Traduce EstadoCentro a color/etiqueta. Vive en un solo sitio para que la
/// tabla de Centros y el Workspace nunca puedan mostrar el mismo estado con
/// colores o textos distintos — mismo criterio que EstadoDocumentoUi.
///
/// <para>
/// <b>Las ramas por defecto no degradan a favorable.</b> Un valor sin traducir
/// se rotulaba «Vigente» con tono neutro: un centro en un estado nuevo que
/// nadie hubiera añadido aquí se pintaba como si estuviera al día. Ahora lo
/// desconocido se muestra como tal y en rojo.
/// </para>
/// </summary>
public static class EstadoCentroUi
{
    public static TonoBadge Tono(EstadoCentro estado) => estado switch
    {
        EstadoCentro.Vigente => TonoBadge.Exito,
        // Urgente y Próximo se rotulan los dos «Por vencer» (2026-10-08) y comparten tono: una misma pastilla no
        // puede salir en dos colores. Lo urgente se sigue distinguiendo por el orden y por el motivo.
        EstadoCentro.Proximo => TonoBadge.Advertencia,
        EstadoCentro.Urgente => TonoBadge.Advertencia,
        EstadoCentro.Vencido => TonoBadge.Peligro,
        EstadoCentro.Faltante => TonoBadge.Peligro,
        EstadoCentro.Bloqueado => TonoBadge.Peligro,
        // P1-X2: neutro, nunca Exito — el Centro no está "al día", es que no
        // se le exige documentación. Pintarlo en verde sería un verde falso.
        EstadoCentro.SinGestionCae => TonoBadge.Neutro,
        _ => TonoBadge.Peligro
    };

    /// <summary>
    /// Botones de la franja de estado de <c>/centros</c>, de peor a mejor (Bloqueante, Vencido, Faltante, lo que
    /// está por vencer, lo correcto): lo que el Gestor CAE busca cuando filtra es lo que le urge. «Por vencer»
    /// marca Urgente y Próximo a la vez. Los rótulos salen de <see cref="Texto(EstadoCentro)"/>, así que cada
    /// botón filtra exactamente las filas que llevan su rótulo. Se construye en cada lectura (textos localizados).
    /// </summary>
    public static IReadOnlyList<OpcionFranjaEstado> Franja =>
    [
        new(Texto(EstadoCentro.Bloqueado), Tono(EstadoCentro.Bloqueado), nameof(EstadoCentro.Bloqueado)),
        new(Texto(EstadoCentro.Vencido), Tono(EstadoCentro.Vencido), nameof(EstadoCentro.Vencido)),
        new(Texto(EstadoCentro.Faltante), Tono(EstadoCentro.Faltante), nameof(EstadoCentro.Faltante)),
        new(Texto(EstadoCentro.Proximo), Tono(EstadoCentro.Proximo), nameof(EstadoCentro.Urgente), nameof(EstadoCentro.Proximo)),
        new(Texto(EstadoCentro.Vigente), Tono(EstadoCentro.Vigente), nameof(EstadoCentro.Vigente)),
        new(Texto(EstadoCentro.SinGestionCae), Tono(EstadoCentro.SinGestionCae), nameof(EstadoCentro.SinGestionCae))
    ];

    /// <summary>
    /// La selección de estados que llega de la URL reducida a nombres de <see cref="EstadoCentro"/>; lo demás se
    /// descarta. Cadena vacía si no queda ninguno.
    /// </summary>
    public static string SeleccionValida(string? seleccion) =>
        SeleccionEstados.Unir(SeleccionEstados.Separar<EstadoCentro>(seleccion).Select(e => e.ToString())) ?? string.Empty;

    /// <summary>Lo correcto no lleva pastilla de color en un listado: punto verde y texto gris.</summary>
    public static bool EsCorrecto(EstadoCentro estado, int? cumplimientoPorcentaje) =>
        estado == EstadoCentro.Vigente && !EsSinDatos(estado, cumplimientoPorcentaje);

    public static string Texto(EstadoCentro estado) => estado switch
    {
        EstadoCentro.Vigente => "Vigente",
        // Una sola pastilla «Por vencer» para Urgente y Próximo, y «Pendiente» para Faltante (2026-10-08): el
        // estado de código no cambia, solo el rótulo. Mismas claves que EstadoDocumentoUi.
        EstadoCentro.Proximo or EstadoCentro.Urgente => EstadoDocumentoUi.PorVencer,
        EstadoCentro.Vencido => "Vencido",
        EstadoCentro.Faltante => TextosVigenciaDocumento.Texto("Pendiente"),
        // Solo lo causa la plataforma del Cliente empresarial (D-7). «Bloqueado» es un estado del Trabajador (2026-10-03):
        // el Centro nunca se rotula «Acceso bloqueado» por un documento; su detalle por Trabajador va aparte.
        EstadoCentro.Bloqueado => "Bloqueo de la plataforma CAE",
        EstadoCentro.SinGestionCae => "No requiere gestión CAE",
        _ => "Estado desconocido"
    };

    /// <summary>
    /// D-17: «Vigente» afirma «todo al día», y con el denominador de cumplimiento a 0
    /// (<paramref name="cumplimientoPorcentaje"/> <c>null</c>: ningún Trabajador×TipoDocumento
    /// obligatorio aplicable) no se ha medido nada. Ese caso se rotula «Sin datos», en neutro.
    /// El resto de estados no cambian: Próximo, Vencido, Faltante o Bloqueado ya dicen algo medido.
    /// </summary>
    public static bool EsSinDatos(EstadoCentro estado, int? cumplimientoPorcentaje) =>
        estado == EstadoCentro.Vigente && cumplimientoPorcentaje is null;

    public static TonoBadge Tono(EstadoCentro estado, int? cumplimientoPorcentaje) =>
        EsSinDatos(estado, cumplimientoPorcentaje) ? TonoBadge.Neutro : Tono(estado);

    public static string Texto(EstadoCentro estado, int? cumplimientoPorcentaje) =>
        EsSinDatos(estado, cumplimientoPorcentaje) ? "Sin datos" : Texto(estado);
}
