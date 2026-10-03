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
        EstadoDocumento.Urgente => TonoBadge.Peligro,
        EstadoDocumento.Vencido => TonoBadge.Peligro,
        EstadoDocumento.Faltante => TonoBadge.Peligro,
        EstadoDocumento.SinCaducidad => TonoBadge.Neutro,
        // Vigencia sin anotar: pide acción del Gestor CAE, pero no es un
        // vencimiento conocido — ámbar, no rojo ni neutro.
        EstadoDocumento.SinConfirmar => TonoBadge.Advertencia,
        _ => TonoBadge.Peligro
    };

    public static string Texto(EstadoDocumento estado) => estado switch
    {
        EstadoDocumento.Vigente => "Vigente",
        EstadoDocumento.Proximo => "Próximo",
        EstadoDocumento.Urgente => "Urgente",
        EstadoDocumento.Vencido => "Vencido",
        EstadoDocumento.Faltante => "Falta",
        EstadoDocumento.SinCaducidad => "Sin caducidad",
        EstadoDocumento.SinConfirmar => TextosVigenciaDocumento.Texto("SinConfirmar"),
        _ => "Estado desconocido"
    };

    /// <summary>
    /// Texto de una celda de vigencia cuando el documento NO tiene fecha de vencimiento. Una fecha ausente son
    /// dos cosas distintas (el modelo eliminó el «nulo = no caduca»): «Sin caducidad» solo si está confirmado que no
    /// caduca; «Sin confirmar» si nadie ha anotado hasta cuándo vale (cuenta como al día, con aviso, decisión del
    /// propietario del 2026-10-01). Cualquier otro estado sin fecha (un hueco, un rechazo) no tiene vigencia que
    /// rotular: «—».
    /// </summary>
    public static string TextoSinFechaDeVencimiento(EstadoDocumento? estado) => estado switch
    {
        EstadoDocumento.SinCaducidad => Texto(EstadoDocumento.SinCaducidad),
        EstadoDocumento.SinConfirmar => Texto(EstadoDocumento.SinConfirmar),
        _ => "—"
    };

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

    public static string TextoDocumental(EstadoDocumento? estado) =>
        estado is null ? "Sin documentos" : Texto(estado.Value);

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
