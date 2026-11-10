using CaeManager.Domain.Documentos;
using CaeManager.Domain.Gestiones;

namespace CaeManager.Web.Features.Gestiones.Components;

/// <summary>
/// Claves de texto y tonos de la pantalla de Gestiones automáticas (Gestion Pasos TALVEG.dc.html). Una sola tabla para
/// el listado y el detalle, de modo que un estado se llame igual en los dos sitios.
/// </summary>
public static class PresentacionGestionAutomatica
{
    public static string ClaveEstado(EstadoGestionAutomatica e) => e switch
    {
        EstadoGestionAutomatica.EnCurso => "AutoEstadoEnCurso",
        EstadoGestionAutomatica.PendienteDeValidar => "AutoEstadoPendienteDeValidar",
        EstadoGestionAutomatica.Cerrada => "AutoEstadoCerrada",
        _ => "AutoEstadoSustituida"
    };

    public static string TonoEstado(EstadoGestionAutomatica e) => e switch
    {
        EstadoGestionAutomatica.EnCurso => "aviso",
        EstadoGestionAutomatica.PendienteDeValidar => "info",
        EstadoGestionAutomatica.Cerrada => "exito",
        _ => "neutro"
    };

    /// <summary>Texto corto del paso: la lista de la izquierda y la pastilla del paso abierto (CORTO de la maqueta).</summary>
    public static string ClavePasoCorto(EstadoAcreditacion e) => e switch
    {
        EstadoAcreditacion.PendienteDeSubir => "PasoPendienteDeSubir",
        EstadoAcreditacion.Subida => "PasoPendienteDeValidar",
        EstadoAcreditacion.Aceptada => "PasoValidado",
        EstadoAcreditacion.Rechazada => "PasoRechazado",
        _ => "PasoNoSePide"
    };

    /// <summary>Clase de la pastilla (bp-*) y del punto/color del paso.</summary>
    public static string TonoPaso(EstadoAcreditacion e) => e switch
    {
        EstadoAcreditacion.PendienteDeSubir => "subir",
        EstadoAcreditacion.Subida => "val",
        EstadoAcreditacion.Aceptada => "ok",
        EstadoAcreditacion.Rechazada => "rech",
        _ => "sin"
    };
}
