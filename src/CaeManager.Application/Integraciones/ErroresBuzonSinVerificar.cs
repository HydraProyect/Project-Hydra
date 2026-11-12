using CaeManager.Domain.Common;
using CaeManager.Domain.Integraciones;

namespace CaeManager.Application.Integraciones;

/// <summary>
/// Error único de todo envío (responder, mensaje nuevo, migración a correo y los que eligen buzón general) desde una
/// conexión de Microsoft 365 Habilitada cuya identidad de buzón no está verificada dentro de
/// <see cref="ConexionIntegracion.VentanaVerificacionIdentidad"/>.
/// </summary>
public static class ErroresBuzonSinVerificar
{
    public const string Codigo = "Integraciones.Microsoft365.BuzonSinVerificar";

    public const string Mensaje =
        "No podemos enviar desde este buzón ahora: falta comprobar que su dirección sigue siendo el mismo buzón. Se vuelve a comprobar en la renovación diaria; si el aviso sigue, revisa la conexión en Conexiones.";

    public static Error Crear() => Error.Crear(Codigo, Mensaje);
}
