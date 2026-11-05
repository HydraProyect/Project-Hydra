namespace CaeManager.Application.AltaAutonoma;

/// <summary>
/// Bandera del alta autónoma de Tenants propietarios (DA-10).
///
/// <b>Apagada por defecto y en todos los entornos</b> hasta que se cierren los requisitos previos de
/// producción del plan del alta autónoma. Mismo criterio que <c>RetencionDatosOptions.Activa</c>: el
/// código se fusiona sin abrir el registro, y abrirlo es un ajuste de configuración explícito.
/// </summary>
public class AltaAutonomaOptions
{
    public const string SeccionConfiguracion = "AltaAutonoma";

    /// <summary>Con false, el valor por defecto, ninguna parte del alta autónoma atiende peticiones.</summary>
    public bool Habilitada { get; set; }

    /// <summary>
    /// Directorio donde el remitente de desarrollo escribe los correos del alta (solo <c>Development</c>). Vacío:
    /// <c>talveg-correo-alta</c> en el directorio temporal del sistema.
    /// </summary>
    public string? DirectorioCorreoDesarrollo { get; set; }
}
