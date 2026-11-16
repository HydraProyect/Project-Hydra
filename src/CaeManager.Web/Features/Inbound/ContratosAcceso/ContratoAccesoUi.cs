using CaeManager.Domain.Inbound.ContratosAcceso;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.Inbound.ContratosAcceso.Recursos;
using Microsoft.Extensions.Localization;

namespace CaeManager.Web.Features.Inbound.ContratosAcceso;

/// <summary>
/// Presentación común de un contrato de acceso en el listado, el detalle y el alta: rótulo y tono del estado, tipo, vigencia
/// y franja de estados. Una sola tabla para que las tres pantallas no se contradigan.
/// </summary>
public static class ContratoAccesoUi
{
    /// <summary>Días de antelación con los que la vigencia avisa «Vence en N días» (maqueta de Contratos de acceso).</summary>
    public const int DiasDeAvisoDeVencimiento = 30;

    /// <summary>Opciones de los formularios, expuestas aquí para que el markup no itere genéricos.</summary>
    public static IReadOnlyList<TipoContratoAcceso> Tipos { get; } = Enum.GetValues<TipoContratoAcceso>();

    public static IReadOnlyList<PropiaActividadContrato> PropiasActividad { get; } = Enum.GetValues<PropiaActividadContrato>();

    public static IReadOnlyList<int> Niveles { get; } = Enumerable.Range(0, CondicionesContratoAcceso.NivelSubcontrataMaximo + 1).ToArray();

    public static string ClaveEstado(EstadoContratoAcceso estado) => estado switch
    {
        EstadoContratoAcceso.PendienteDeConfirmar => "EstadoPendienteDeConfirmar",
        EstadoContratoAcceso.Activo => "EstadoActivo",
        EstadoContratoAcceso.Suspendido => "EstadoSuspendido",
        EstadoContratoAcceso.Finalizado => "EstadoFinalizado",
        EstadoContratoAcceso.Cancelado => "EstadoCancelado",
        EstadoContratoAcceso.Rechazado => "EstadoRechazado",
        _ => "EstadoActivo",
    };

    public static TonoBadge Tono(EstadoContratoAcceso estado) => estado switch
    {
        EstadoContratoAcceso.PendienteDeConfirmar => TonoBadge.Advertencia,
        EstadoContratoAcceso.Activo => TonoBadge.Exito,
        EstadoContratoAcceso.Suspendido => TonoBadge.Advertencia,
        EstadoContratoAcceso.Finalizado => TonoBadge.Neutro,
        EstadoContratoAcceso.Cancelado => TonoBadge.Neutro,
        EstadoContratoAcceso.Rechazado => TonoBadge.Peligro,
        _ => TonoBadge.Neutro,
    };

    public static string ClaveTipo(TipoContratoAcceso tipo) => tipo switch
    {
        TipoContratoAcceso.Fijo => "TipoFijo",
        TipoContratoAcceso.Recurrente => "TipoRecurrente",
        TipoContratoAcceso.Temporal => "TipoTemporal",
        TipoContratoAcceso.Puntual => "TipoPuntual",
        TipoContratoAcceso.Obra => "TipoObra",
        TipoContratoAcceso.Emergencia => "TipoEmergencia",
        _ => "TipoFijo",
    };

    public static string ClavePropiaActividad(PropiaActividadContrato valor) => valor switch
    {
        PropiaActividadContrato.Si => "PropiaActividadSi",
        PropiaActividadContrato.No => "PropiaActividadNo",
        _ => "PropiaActividadNoLoSe",
    };

    /// <summary>
    /// Estados de la franja. Los de la maqueta (Pendiente de confirmar, Activo, Finalizado y Rechazado) más los dos que el
    /// backend ya distingue y la maqueta no pinta (Suspendido y Cancelado): sin ellos la cifra de «Todos» no sumaría.
    /// </summary>
    public static IReadOnlyList<OpcionFranjaEstado> Franja(IStringLocalizer<TextosContratosAcceso> textos) =>
    [
        Opcion(textos, EstadoContratoAcceso.PendienteDeConfirmar),
        Opcion(textos, EstadoContratoAcceso.Activo),
        Opcion(textos, EstadoContratoAcceso.Suspendido),
        Opcion(textos, EstadoContratoAcceso.Finalizado),
        Opcion(textos, EstadoContratoAcceso.Cancelado),
        Opcion(textos, EstadoContratoAcceso.Rechazado),
    ];

    private static OpcionFranjaEstado Opcion(IStringLocalizer<TextosContratosAcceso> textos, EstadoContratoAcceso estado) =>
        new(textos[ClaveEstado(estado)].Value, Tono(estado), estado.ToString());

    /// <summary>
    /// Orden del listado (maqueta): Finalizado primero, después los vivos por fecha de fin (los indefinidos al final de
    /// ellos) y al final los que no llegaron a empezar (Rechazado y Cancelado).
    /// </summary>
    public static int RangoDeOrden(EstadoContratoAcceso estado) => estado switch
    {
        EstadoContratoAcceso.Finalizado => 0,
        EstadoContratoAcceso.Rechazado or EstadoContratoAcceso.Cancelado => 2,
        _ => 1,
    };

    /// <summary>La vigencia en palabras: «En vigor», «Vence en N días», «Terminó el …», «Empieza en N días», «Termina hoy» o «No empezó».</summary>
    public static string Vigencia(
        ContratoAccesoVista contrato, DateOnly hoy, IStringLocalizer<TextosContratosAcceso> textos)
    {
        if (contrato.Estado is EstadoContratoAcceso.Rechazado or EstadoContratoAcceso.Cancelado)
            return textos["VigenciaNoEmpezo"].Value;

        if (contrato.Estado == EstadoContratoAcceso.Finalizado)
            return textos["VigenciaTermino", Fecha(contrato.FechaCierreReal ?? contrato.FechaFin)].Value;

        if (hoy < contrato.FechaInicio)
            return textos["VigenciaEmpiezaEn", contrato.FechaInicio.DayNumber - hoy.DayNumber].Value;

        if (contrato.FechaFin is not { } fin)
            return textos["VigenciaEnVigor"].Value;

        var dias = fin.DayNumber - hoy.DayNumber;
        return dias switch
        {
            < 0 => textos["VigenciaTermino", Fecha(fin)].Value,
            0 => textos["VigenciaTerminaHoy"].Value,
            <= DiasDeAvisoDeVencimiento => textos["VigenciaVenceEn", dias].Value,
            _ => textos["VigenciaEnVigor"].Value,
        };
    }

    /// <summary>Rango «inicio – fin»; sin fin, «Desde inicio» (el contrato indefinido).</summary>
    public static string Rango(DateOnly inicio, DateOnly? fin, IStringLocalizer<TextosContratosAcceso> textos) =>
        fin is { } f
            ? textos["VigenciaRango", Fecha(inicio), Fecha(f)].Value
            : textos["VigenciaDesde", Fecha(inicio)].Value;

    public static string Fecha(DateOnly? fecha) => MascaraFecha.Formatear(fecha);

    /// <summary>«N de cupo» / «Sin límite» según el cupo de Trabajadores del contrato.</summary>
    public static string TextoCupo(int? cupo, IStringLocalizer<TextosContratosAcceso> textos) =>
        cupo is { } n ? textos["CupoLimitado", n].Value : textos["CupoSinLimite"].Value;
}

/// <summary>Los campos de un contrato que usa la presentación, para que no dependa de si viene de la fila o del detalle.</summary>
public sealed record ContratoAccesoVista(
    EstadoContratoAcceso Estado, DateOnly FechaInicio, DateOnly? FechaFin, DateOnly? FechaCierreReal);
