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
        "Solo quien es principal de esa empresa puede dar acceso de apoyo.");

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
        "Quien te propuso el apoyo ya no es el principal de esa empresa; la propuesta se ha anulado.");

    public static readonly Error AnuladaDestinatarioNoDisponible = Error.Crear(
        "PropuestaApoyo.AnuladaDestinatarioNoDisponible",
        "Tu cuenta ya no es de Gestor CAE activo; la propuesta de apoyo se ha anulado.");

    public static readonly Error AnuladaOperacionNoVigente = Error.Crear(
        "PropuestaApoyo.AnuladaOperacionNoVigente",
        "Tu organización ya no gestiona esa empresa; la propuesta de apoyo se ha anulado.");

    public static readonly Error AnuladaYaEnCartera = Error.Crear(
        "PropuestaApoyo.AnuladaYaEnCartera",
        "Ya tienes esa empresa en tu cartera; la propuesta de apoyo se ha anulado.");

    public static readonly Error AnuladaFechaDeFinPasada = Error.Crear(
        "PropuestaApoyo.AnuladaFechaDeFinPasada",
        "La fecha de fin de ese apoyo ya ha pasado; la propuesta se ha anulado.");

    public static readonly Error FechaDeFinNoValida = Error.Crear(
        "PropuestaApoyo.FechaDeFinNoValida", "La fecha de fin del apoyo no puede ser anterior a hoy.");

    public static readonly Error ApoyoNoEncontrado = Error.Crear(
        "PropuestaApoyo.ApoyoNoEncontrado", "No encontramos ese acceso de apoyo.");

    public static readonly Error EresElPrincipal = Error.Crear(
        "PropuestaApoyo.EresElPrincipal",
        "Ahora eres el principal de esa empresa: ya no puedes desasignarte. Pide a tu Coordinador CAE que designe a otra persona.");

    public static readonly Error ApoyoEsAhoraPrincipal = Error.Crear(
        "PropuestaApoyo.ApoyoEsAhoraPrincipal",
        "Esa persona es ahora el principal de la empresa: su cartera ya no es de apoyo y no se retira desde aquí.");

    public static readonly Error SoloRetirasLoQueConcediste = Error.Crear(
        "PropuestaApoyo.SoloRetirasLoQueConcediste", "Solo puedes retirar el acceso de apoyo que diste tú.");

    public static readonly Error YaNoEresElPrincipal = Error.Crear(
        "PropuestaApoyo.YaNoEresElPrincipal",
        "Ya no eres el principal de esa empresa: ese acceso de apoyo lo puede retirar un Coordinador CAE.");

    public static readonly Error ApoyoFueraDeTuEquipo = Error.Crear(
        "PropuestaApoyo.ApoyoFueraDeTuEquipo",
        "Ni esa persona ni quien le dio el acceso están en tu equipo: no puedes revocar ese apoyo.");

    /// <summary>El error con que el destinatario se entera de que su aceptación anuló la propuesta.</summary>
    public static Error DeAnulacion(MotivoAnulacionPropuestaApoyo motivo) => motivo switch
    {
        MotivoAnulacionPropuestaApoyo.FechaDeFinPasada => AnuladaFechaDeFinPasada,
        MotivoAnulacionPropuestaApoyo.YaEnCartera => AnuladaYaEnCartera,
        MotivoAnulacionPropuestaApoyo.OperacionNoVigente => AnuladaOperacionNoVigente,
        MotivoAnulacionPropuestaApoyo.DestinatarioNoDisponible => AnuladaDestinatarioNoDisponible,
        _ => AnuladaProponenteYaNoEsPrincipal,
    };
}
