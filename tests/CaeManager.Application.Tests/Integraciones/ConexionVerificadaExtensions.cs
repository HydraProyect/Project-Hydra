using CaeManager.Domain.Integraciones;

namespace CaeManager.Application.Tests.Integraciones;

/// <summary>
/// Una conexión de Microsoft 365 solo ingiere y envía con la identidad del buzón verificada dentro de
/// <see cref="ConexionIntegracion.VentanaVerificacionIdentidad"/>. Los tests que no van de eso la dejan verificada.
/// </summary>
public static class ConexionVerificadaExtensions
{
    public static ConexionIntegracion Verificada(this ConexionIntegracion conexion)
    {
        if (conexion.IdentidadBuzon is null)
            conexion.FijarIdentidadBuzon("inbox-" + conexion.Id.ToString("N"));
        conexion.MarcarIdentidadVerificada(DateTime.UtcNow);
        return conexion;
    }
}
