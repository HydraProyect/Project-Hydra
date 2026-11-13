using CaeManager.Application.Contacto;

namespace CaeManager.Web.Features.Contacto;

/// <summary>
/// Dirección que recibe el correo de una solicitud de demo. Sale del ajuste
/// <c>Contacto:DestinatarioSolicitudDemo</c> (variable de entorno
/// <c>Contacto__DestinatarioSolicitudDemo</c>) y nunca de la petición: es una
/// configuración del servidor, no un dato del formulario. Si el ajuste falta, está
/// vacío o no es una dirección exacta, se usa <see cref="DireccionPorDefecto"/>:
/// una configuración rota no deja de entregar las solicitudes y nunca las manda a
/// una dirección inválida. En los dos casos queda un aviso en el registro al arrancar.
/// </summary>
public sealed class DestinatarioSolicitudDemo
{
    public const string ClaveConfiguracion = "Contacto:DestinatarioSolicitudDemo";

    public const string DireccionPorDefecto = "info@talveg.es";

    private DestinatarioSolicitudDemo(string direccion) => Direccion = direccion;

    public string Direccion { get; }

    /// <summary>
    /// Avisa en el registro, una vez por proceso (se resuelve como singleton al mapear
    /// el endpoint), cuando el ajuste falta o no es válido y se usa la dirección de
    /// reserva. Ninguno de los dos avisos escribe el valor configurado: puede ser una
    /// dirección personal mal escrita y los registros no llevan datos personales.
    /// </summary>
    public static DestinatarioSolicitudDemo Resolver(string? configurado, ILogger logger)
    {
        var valor = configurado?.Trim();
        if (string.IsNullOrEmpty(valor))
        {
            logger.LogWarning("El ajuste {Clave} no está configurado; las solicitudes de demo van a la dirección de reserva. Con {Guarda}=SoloPermitidos, la de reserva tiene que estar también en la lista.", ClaveConfiguracion, "EnvioCorreo__Modo");
            return new DestinatarioSolicitudDemo(DireccionPorDefecto);
        }

        if (!ValidadorSolicitudDemo.CorreoValido(valor))
        {
            logger.LogWarning("El ajuste {Clave} no es una dirección de correo válida; las solicitudes de demo van a la dirección de reserva. Con {Guarda}=SoloPermitidos, la de reserva tiene que estar también en la lista.", ClaveConfiguracion, "EnvioCorreo__Modo");
            return new DestinatarioSolicitudDemo(DireccionPorDefecto);
        }

        return new DestinatarioSolicitudDemo(valor);
    }
}
