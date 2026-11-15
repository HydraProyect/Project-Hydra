using CaeManager.Domain.Common;

namespace CaeManager.Application.Inbound.ContratosAcceso;

/// <summary>Errores comunes de las escrituras sobre un contrato de acceso.</summary>
public static class ContratoAccesoErrores
{
    /// <summary>Un único error para «no existe» y «no lo ves»: la respuesta no sirve de oráculo de qué contratos hay.</summary>
    public static Error NoEncontrado =>
        Error.Crear("ContratoAcceso.NoEncontrado", "El contrato de acceso no existe o no tienes acceso.");

    /// <summary>El historial de vigencia registra quién cambió el fin: sin usuario no se escribe.</summary>
    public static Error SinUsuario =>
        Error.Crear("ContratoAcceso.SinUsuario", "No se puede identificar al usuario que hace el cambio.");

    /// <summary>
    /// La escritura no cambia nada: se rechaza para que toda acción aceptada deje su fila de auditoría (sin cambio, EF no
    /// actualiza la fila y el interceptor no escribe ninguna).
    /// </summary>
    public static Error SinCambios =>
        Error.Crear("ContratoAcceso.SinCambios", "El contrato de acceso ya tiene esas condiciones.");

    /// <summary>La transición no la admite el estado del contrato (ver <c>EstadoContratoAcceso</c>).</summary>
    public static Error TransicionNoPermitida(string mensaje) =>
        Error.Crear("ContratoAcceso.TransicionNoPermitida", mensaje);
}
