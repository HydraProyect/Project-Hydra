using CaeManager.Domain.Common;
using CaeManager.Domain.Operaciones;

namespace CaeManager.Application.Operaciones.ApoyoCartera;

/// <summary>
/// Códigos estables de la propuesta de apoyo (ADR-011 § 2.7, enmienda 2026-10-08): la campana
/// y «Dar acceso» los comparan literalmente. Cambiar uno es un cambio de contrato con ellos.
/// </summary>
public static class ErroresPropuestaApoyo
{
    public static readonly Error SinPermiso = Error.Crear(
        "PropuestaApoyo.SinPermiso", "No tienes permiso para esta acción sobre las propuestas de apoyo.");

    public static readonly Error NoEresElPrincipal = Error.Crear(
        "PropuestaApoyo.NoEresElPrincipal",
        "Solo el Gestor CAE principal de esa empresa puede dar acceso de apoyo.");

    public static readonly Error OperacionNoDisponible = Error.Crear(
        "PropuestaApoyo.OperacionNoDisponible", "Tu organización ya no gestiona esa empresa.");

    public static readonly Error DestinatarioNoValido = Error.Crear(
        "PropuestaApoyo.DestinatarioNoValido",
        "Esa persona no es un Gestor CAE activo de tu organización.");

    public static readonly Error DestinatarioYaEnCartera = Error.Crear(
        "PropuestaApoyo.DestinatarioYaEnCartera", "Esa persona ya tiene la empresa en su cartera.");

    public static readonly Error YaPendiente = Error.Crear(
        "PropuestaApoyo.YaPendiente", "Esa persona ya tiene una propuesta de apoyo pendiente para esa empresa.");

    public static readonly Error NoEncontrada = Error.Crear(
        "PropuestaApoyo.NoEncontrada", "No encontramos esa propuesta de apoyo.");

    public static readonly Error YaResuelta = Error.Crear(
        "PropuestaApoyo.YaResuelta", "Esa propuesta de apoyo ya no está pendiente.");

    public static readonly Error CambioMientrasDecidias = Error.Crear(
        "PropuestaApoyo.CambioMientrasDecidias",
        "La cartera de esa empresa cambió mientras respondías. No se ha cambiado nada; vuelve a intentarlo.");

    public static readonly Error AnuladaProponenteYaNoEsPrincipal = Error.Crear(
        "PropuestaApoyo.AnuladaProponenteYaNoEsPrincipal",
        "Quien te propuso el apoyo ya no es el Gestor CAE principal de esa empresa; la propuesta se ha anulado.");

    public static readonly Error AnuladaDestinatarioNoDisponible = Error.Crear(
        "PropuestaApoyo.AnuladaDestinatarioNoDisponible",
        "Tu cuenta ya no es de Gestor CAE activo; la propuesta de apoyo se ha anulado.");

    public static readonly Error AnuladaOperacionNoVigente = Error.Crear(
        "PropuestaApoyo.AnuladaOperacionNoVigente",
        "Tu organización ya no gestiona esa empresa; la propuesta de apoyo se ha anulado.");

    public static readonly Error AnuladaYaEnCartera = Error.Crear(
        "PropuestaApoyo.AnuladaYaEnCartera",
        "Ya tienes esa empresa en tu cartera; la propuesta de apoyo se ha anulado.");

    /// <summary>El error con que el destinatario se entera de que su aceptación anuló la propuesta.</summary>
    public static Error DeAnulacion(MotivoAnulacionPropuestaApoyo motivo) => motivo switch
    {
        MotivoAnulacionPropuestaApoyo.YaEnCartera => AnuladaYaEnCartera,
        MotivoAnulacionPropuestaApoyo.OperacionNoVigente => AnuladaOperacionNoVigente,
        MotivoAnulacionPropuestaApoyo.DestinatarioNoDisponible => AnuladaDestinatarioNoDisponible,
        _ => AnuladaProponenteYaNoEsPrincipal,
    };
}
