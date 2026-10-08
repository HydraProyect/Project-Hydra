using System.Globalization;
using CaeManager.Application.Documentos;
using CaeManager.Domain.Documentos;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.Documentos.Recursos;

namespace CaeManager.Web.Features.Documentos;

/// <summary>
/// Traduce EstadoDocumento a color/etiqueta. Vive en un solo sitio para que
/// Documentos y Alertas nunca puedan mostrar el mismo estado con colores o
/// textos distintos.
///
/// <para>
/// <b>Las ramas por defecto no degradan a favorable.</b> Antes, un valor sin
/// traducir se rotulaba «No aplica» con tono neutro — es decir, un estado
/// nuevo que alguien olvidara añadir aquí se pintaba como si no hubiera nada
/// que hacer. Un fallo así no avisa: enseña calma donde debería enseñar
/// alarma. Ahora cada valor se declara y lo desconocido se muestra como tal,
/// en rojo.
/// </para>
/// </summary>
public static class EstadoDocumentoUi
{
    public static TonoBadge Tono(EstadoDocumento estado) => estado switch
    {
        EstadoDocumento.Vigente => TonoBadge.Exito,
        EstadoDocumento.Proximo => TonoBadge.Advertencia,
        // Urgente y Próximo se rotulan igual («Por vencer», decisión del 2026-10-08), así que llevan también el
        // mismo tono: dos colores para un mismo rótulo serían dos pastillas. Lo urgente se distingue por el orden,
        // por el tinte de la fila y por los días que dice el motivo, no por el color de la pastilla.
        EstadoDocumento.Urgente => TonoBadge.Advertencia,
        EstadoDocumento.Vencido => TonoBadge.Peligro,
        EstadoDocumento.Faltante => TonoBadge.Peligro,
        EstadoDocumento.SinCaducidad => TonoBadge.Neutro,
        // Vigencia sin anotar: pide acción del Gestor CAE, pero no es un
        // vencimiento conocido — ámbar, no rojo ni neutro.
        EstadoDocumento.SinConfirmar => TonoBadge.Advertencia,
        // Vencido pero todavía válido para acceder a este Centro: su gravedad está entre Urgente y Vencido, así que lleva un
        // tono propio entre el ámbar y el rojo (decisión de Chris, 2026-10-04), no el ámbar de Próximo y Sin confirmar.
        EstadoDocumento.EnTolerancia => TonoBadge.Tolerancia,
        _ => TonoBadge.Peligro
    };

    /// <summary>
    /// Rótulo de un estado en la interfaz. Vocabulario único (decisión del propietario, 2026-10-08):
    /// <see cref="EstadoDocumento.Urgente"/> y <see cref="EstadoDocumento.Proximo"/> se leen los dos «Por vencer», y
    /// <see cref="EstadoDocumento.Faltante"/> se lee «Pendiente». Los estados de código no cambian: el filtro, el
    /// orden y el tinte de fila siguen distinguiendo lo urgente de lo próximo.
    /// </summary>
    public static string Texto(EstadoDocumento estado) => estado switch
    {
        EstadoDocumento.Vigente => "Vigente",
        EstadoDocumento.Proximo => PorVencer,
        EstadoDocumento.Urgente => PorVencer,
        EstadoDocumento.Vencido => "Vencido",
        EstadoDocumento.Faltante => TextosVigenciaDocumento.Texto("Pendiente"),
        EstadoDocumento.SinCaducidad => "Sin caducidad",
        EstadoDocumento.SinConfirmar => TextosVigenciaDocumento.Texto("SinConfirmar"),
        EstadoDocumento.EnTolerancia => TextosVigenciaDocumento.Texto("EnTolerancia"),
        _ => "Estado desconocido"
    };

    /// <summary>
    /// El rótulo de un estado cuando se conoce hasta cuándo aguanta la tolerancia: «Vencido · en tolerancia hasta dd/MM».
    /// Sin fecha, o con cualquier otro estado, es el de <see cref="Texto(EstadoDocumento)"/>.
    /// </summary>
    public static string Texto(EstadoDocumento estado, DateOnly? enToleranciaHasta) =>
        estado == EstadoDocumento.EnTolerancia && enToleranciaHasta is { } hasta
            ? string.Format(CultureInfo.CurrentCulture, TextosVigenciaDocumento.Texto("VencidoEnToleranciaHasta"), hasta.ToString("dd/MM", CultureInfo.InvariantCulture))
            : Texto(estado);

    /// <summary>
    /// ¿Ya venció el Documento? Es verdad para <see cref="EstadoDocumento.Vencido"/> y también para
    /// <see cref="EstadoDocumento.EnTolerancia"/>, que es un vencido que en ese Centro todavía vale para acceder: dejarlo fuera de
    /// un recuento o de un filtro de «vencidos» lo escondería.
    /// </summary>
    public static bool HaVencido(EstadoDocumento estado) =>
        estado is EstadoDocumento.Vencido or EstadoDocumento.EnTolerancia;

    /// <summary>
    /// Texto de una celda de vigencia cuando el documento NO tiene fecha de vencimiento. Una fecha ausente son
    /// dos cosas distintas (el modelo eliminó el «nulo = no caduca»): «Sin caducidad» solo si está confirmado que no
    /// caduca; «Sin confirmar» si nadie ha anotado hasta cuándo vale (cuenta como al día, con aviso, decisión del
    /// propietario del 2026-10-01). Cualquier otro estado sin fecha (un hueco, un rechazo) no tiene vigencia que
    /// rotular: «—».
    /// </summary>
    public static string TextoSinFechaDeVencimiento(EstadoDocumento? estado) =>
        RotulaSinFechaDeVencimiento(estado) ? Texto(estado!.Value) : "—";

    /// <summary>
    /// Si un documento sin fecha de vencimiento lleva rótulo propio en su celda de vigencia («Sin caducidad» o
    /// «Sin confirmar») en vez de «—». Es la única definición de qué estados sin fecha llevan rótulo en la celda de vigencia: la usan
    /// <see cref="TextoSinFechaDeVencimiento"/> y, para decidir si la celda de Excel lleva texto o queda vacía,
    /// la exportación de Reportes.
    /// </summary>
    public static bool RotulaSinFechaDeVencimiento(EstadoDocumento? estado) =>
        estado is EstadoDocumento.SinCaducidad or EstadoDocumento.SinConfirmar;

    /// <summary>
    /// Aviso ámbar de una celda de vigencia «Sin confirmar» (cuenta como al día, con aviso; decisión del propietario del
    /// 2026-10-01). Lo comparten Centro 360, Trabajador 360 y Subcontrata 360: es la misma regla que
    /// <see cref="TextoSinFechaDeVencimiento"/> y se mantiene en el mismo sitio. <c>null</c> para cualquier otro estado.
    /// </summary>
    public static string? ClaseAvisoVigenciaSinConfirmar(EstadoDocumento? estado) =>
        estado == EstadoDocumento.SinConfirmar ? "celda-documento-vigencia-sin-confirmar" : null;

    /// <summary>
    /// Clase de la celda de vigencia de las tablas de documentos requeridos (Centro 360 y Subcontrata 360): estilo neutro
    /// de la columna más, si procede, el aviso de <see cref="ClaseAvisoVigenciaSinConfirmar"/>.
    /// </summary>
    public static string ClaseCeldaVigencia(EstadoDocumento? estado) =>
        ClaseAvisoVigenciaSinConfirmar(estado) is { } aviso
            ? $"celda-documento-vigencia {aviso}"
            : "celda-documento-vigencia";

    /// <summary>
    /// Estado documental derivado de Trabajador/Empresa/Vehículo, donde null
    /// significa "no tiene ningún documento todavía" — ver
    /// <see cref="ICalculoEstadoDocumentalService"/>.
    /// </summary>
    public static TonoBadge TonoDocumental(EstadoDocumento? estado) =>
        estado is null ? TonoBadge.Neutro : Tono(estado.Value);

    /// <summary>
    /// Rótulo del peor estado de un propietario. Lo que está bien se dice de una sola forma, «Sin incidencias»:
    /// en un agregado, «Vigente» y «Sin caducidad» responden lo mismo (no hay nada que hacer) y eran dos verdes.
    /// </summary>
    public static string TextoDocumental(EstadoDocumento? estado) =>
        estado is null ? "Sin documentos"
        : EsCorrecto(estado.Value) ? SinIncidencias
        : Texto(estado.Value);

    /// <summary>Rótulo único de Urgente y Próximo en la interfaz.</summary>
    public static string PorVencer => TextosVigenciaDocumento.Texto("PorVencer");

    /// <summary>Rótulo de un propietario (Trabajador, Empresa, Vehículo, Centro…) al que no hay nada que reclamar.</summary>
    public static string SinIncidencias => TextosVigenciaDocumento.Texto("SinIncidencias");

    /// <summary>
    /// ¿El estado no pide ninguna acción? En los listados lo correcto no lleva pastilla de color (punto verde y texto
    /// gris, <c>EstadoFila</c>): el color queda para lo que hay que atender.
    /// </summary>
    public static bool EsCorrecto(EstadoDocumento estado) =>
        estado is EstadoDocumento.Vigente or EstadoDocumento.SinCaducidad;

    /// <summary>Como <see cref="EsCorrecto(EstadoDocumento)"/>; «sin documentos» (<c>null</c>) no es correcto, es desconocido.</summary>
    public static bool EsCorrecto(EstadoDocumento? estado) => estado is { } valor && EsCorrecto(valor);

    /// <summary>
    /// Motivo que acompaña a la pastilla de un Documento, sin repetir lo que la pastilla ya dice: cuánto hace que
    /// venció, cuánto le queda, o qué falta para saberlo. <c>null</c> si el estado no necesita explicación.
    /// </summary>
    public static string? MotivoDeDocumento(EstadoDocumento estado, DateOnly? fechaVencimiento, DateOnly hoy)
    {
        if (estado == EstadoDocumento.SinConfirmar)
            return TextosVigenciaDocumento.Texto("MotivoFaltaFechaVencimiento");

        if (fechaVencimiento is not { } vence)
            return null;

        var dias = vence.DayNumber - hoy.DayNumber;
        return estado switch
        {
            EstadoDocumento.Vencido or EstadoDocumento.EnTolerancia => ConDias(-dias, "MotivoVencioHoy", "MotivoHaceUnDia", "MotivoHaceDias"),
            EstadoDocumento.Urgente or EstadoDocumento.Proximo => ConDias(dias, "MotivoCaducaHoy", "MotivoCaducaEnUnDia", "MotivoCaducaEnDias"),
            _ => null
        };
    }

    private static string ConDias(int dias, string claveHoy, string claveUno, string claveVarios) => dias switch
    {
        <= 0 => TextosVigenciaDocumento.Texto(claveHoy),
        1 => TextosVigenciaDocumento.Texto(claveUno),
        _ => string.Format(CultureInfo.CurrentCulture, TextosVigenciaDocumento.Texto(claveVarios), dias)
    };

    /// <summary>
    /// Opciones del filtro de estado documental, de peor a mejor: al filtrar,
    /// lo que el gestor busca es lo que le urge. Mismas opciones en las tres
    /// pantallas — es la misma pregunta sobre tres tablas distintas. Se
    /// construye en cada lectura: un texto localizado no se congela en la
    /// cultura de quien la leyó primero.
    /// </summary>
    public static IReadOnlyList<OpcionEstado> OpcionesDocumentales =>
    [
        new(nameof(EstadoDocumento.Vencido), "Vencido"),
        new(nameof(EstadoDocumento.Urgente), "Urgente"),
        new(nameof(EstadoDocumento.Proximo), "Próximo"),
        new(nameof(EstadoDocumento.SinConfirmar), TextosVigenciaDocumento.Texto("SinConfirmar")),
        new(nameof(EstadoDocumento.Vigente), "Vigente"),
        new(nameof(EstadoDocumento.SinCaducidad), "Sin caducidad"),
        new(EstadoDocumentalFiltro.SinDocumentos, "Sin documentos")
    ];
}
