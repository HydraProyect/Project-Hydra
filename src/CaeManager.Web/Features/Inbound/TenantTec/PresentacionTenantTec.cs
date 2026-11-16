using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.Inbound.TenantTec.Recursos;
using CaeManager.Web.Features.Inbound.TenantTec.Servicios;
using Microsoft.Extensions.Localization;

namespace CaeManager.Web.Features.Inbound.TenantTec;

/// <summary>
/// Cómo se rotula y se colorea lo que el Tenant TEC ve en sus pantallas: un único sitio para que la lista,
/// el detalle y el aviso digan lo mismo de un mismo estado.
/// </summary>
public static class PresentacionTenantTec
{
    public const string FormatoFecha = "dd/MM/yyyy";

    public static string Fecha(DateOnly fecha) => fecha.ToString(FormatoFecha);

    public static string FechaHora(DateTimeOffset fecha) => fecha.ToString("dd/MM/yyyy · HH:mm");

    public static string Estado(IStringLocalizer<TextosInboundTenantTec> textos, EstadoRequerimientoTec estado) => estado switch
    {
        EstadoRequerimientoTec.Rechazado => textos["EstadoRechazado"],
        EstadoRequerimientoTec.PendienteDeSubir => textos["EstadoPendienteSubir"],
        EstadoRequerimientoTec.Incompleto => textos["EstadoIncompleto"],
        EstadoRequerimientoTec.PendienteDeValidar => textos["EstadoPendienteValidar"],
        _ => textos["EstadoValidado"],
    };

    public static TonoBadge TonoEstado(EstadoRequerimientoTec estado) => estado switch
    {
        EstadoRequerimientoTec.Rechazado => TonoBadge.Peligro,
        EstadoRequerimientoTec.PendienteDeSubir => TonoBadge.Advertencia,
        EstadoRequerimientoTec.Incompleto => TonoBadge.Advertencia,
        EstadoRequerimientoTec.PendienteDeValidar => TonoBadge.Info,
        _ => TonoBadge.Exito,
    };

    public static string Vigencia(IStringLocalizer<TextosInboundTenantTec> textos, VigenciaRequerimientoTec vigencia) => vigencia switch
    {
        VigenciaRequerimientoTec.Vigente => textos["VigVigente"],
        VigenciaRequerimientoTec.Proximo => textos["VigProximo"],
        VigenciaRequerimientoTec.Urgente => textos["VigUrgente"],
        VigenciaRequerimientoTec.Vencido => textos["VigVencido"],
        VigenciaRequerimientoTec.VencidoEnTolerancia => textos["VigTolerancia"],
        _ => textos["VigSin"],
    };

    public static TonoBadge TonoVigencia(VigenciaRequerimientoTec vigencia) => vigencia switch
    {
        VigenciaRequerimientoTec.Vigente => TonoBadge.Exito,
        VigenciaRequerimientoTec.Proximo => TonoBadge.Advertencia,
        VigenciaRequerimientoTec.Urgente => TonoBadge.Peligro,
        VigenciaRequerimientoTec.Vencido => TonoBadge.Peligro,
        VigenciaRequerimientoTec.VencidoEnTolerancia => TonoBadge.Tolerancia,
        _ => TonoBadge.Neutro,
    };

    public static string Entidad(IStringLocalizer<TextosInboundTenantTec> textos, EntidadRequerimientoTec entidad) => entidad switch
    {
        EntidadRequerimientoTec.Trabajador => textos["EntidadTrabajador"],
        EntidadRequerimientoTec.Vehiculo => textos["EntidadVehiculo"],
        _ => textos["EntidadEmpresa"],
    };

    /// <summary>Un requerimiento que aún admite un documento nuevo (no está a la espera de validación ni validado).</summary>
    public static bool AdmiteSubida(RequerimientoTec requerimiento) =>
        requerimiento.Accion == AccionRequerimientoTec.Subir;
}
