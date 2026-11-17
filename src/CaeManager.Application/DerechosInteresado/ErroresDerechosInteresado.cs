using CaeManager.Domain.Common;

namespace CaeManager.Application.DerechosInteresado;

public static class ErroresDerechosInteresado
{
    public static readonly Error SinCapacidad = Error.Crear(
        "SolicitudDerecho.SinCapacidad",
        "Tramitar y resolver solicitudes de derechos es del Administrador de la organización o de quien tenga la capacidad «Derechos del interesado».");

    public static readonly Error NoEncontrada = Error.Crear(
        "SolicitudDerecho.NoEncontrada",
        "No encontramos esta solicitud.");

    public static readonly Error TrabajadorNoEncontrado = Error.Crear(
        "Trabajador.NoEncontrado",
        "No encontramos este trabajador.");

    public static Error PasoNoPermitido(string detalle) => Error.Crear("SolicitudDerecho.PasoNoPermitido", detalle);

    public static Error DatosNoValidos(string detalle) => Error.Crear("SolicitudDerecho.DatosNoValidos", detalle);
}
