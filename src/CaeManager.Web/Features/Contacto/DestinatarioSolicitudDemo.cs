using CaeManager.Application.Contacto;

namespace CaeManager.Web.Features.Contacto;

/// <summary>
/// Dirección que recibe el correo de una solicitud de demo. Sale del ajuste
/// <c>Contacto:DestinatarioSolicitudDemo</c> (variable de entorno
/// <c>Contacto__DestinatarioSolicitudDemo</c>) y nunca de la petición: es una
/// configuración del servidor, no un dato del formulario. Si el ajuste falta, está
/// vacío o no es una dirección exacta, se usa <see cref="DireccionPorDefecto"/>:
/// una configuración rota no deja de entregar las solicitudes y nunca las manda a
/// una dirección inválida.
/// </summary>
public sealed class DestinatarioSolicitudDemo
{
    public const string ClaveConfiguracion = "Contacto:DestinatarioSolicitudDemo";

    public const string DireccionPorDefecto = "info@talveg.es";

    private DestinatarioSolicitudDemo(string direccion) => Direccion = direccion;

    public string Direccion { get; }

    /// <summary>
    /// El aviso de un valor no válido no lo incluye: puede ser una dirección personal
    /// mal escrita y los registros no llevan datos personales.
    /// </summary>
    public static DestinatarioSolicitudDemo Resolver(string? configurado, ILogger logger)
    {
        var valor = configurado?.Trim();
        if (string.IsNullOrEmpty(valor))
            return new DestinatarioSolicitudDemo(DireccionPorDefecto);

        if (!ValidadorSolicitudDemo.CorreoValido(valor))
        {
            logger.LogWarning(
                "El ajuste {Clave} no es una dirección de correo válida; las solicitudes de demo van a la dirección de reserva.",
                ClaveConfiguracion);
            return new DestinatarioSolicitudDemo(DireccionPorDefecto);
        }

        return new DestinatarioSolicitudDemo(valor);
    }
}
