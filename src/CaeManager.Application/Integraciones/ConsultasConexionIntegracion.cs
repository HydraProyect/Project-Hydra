using System.Linq.Expressions;
using CaeManager.Domain.Integraciones;

namespace CaeManager.Application.Integraciones;

/// <summary>
/// La parte de verificación de <see cref="ConexionIntegracion.EsOperativaVerificada"/> en forma traducible a SQL, para
/// las consultas que eligen o recorren conexiones sin cargarlas. El estado (Habilitada) lo filtra cada consulta.
/// </summary>
public static class ConsultasConexionIntegracion
{
    /// <summary>
    /// Verificación vigente: en Microsoft 365, identidad comprobada hace menos de
    /// <see cref="ConexionIntegracion.VentanaVerificacionIdentidad"/> y no en el futuro (con el mismo margen de reloj que
    /// el dominio). Una conexión que no es de Microsoft 365 no se verifica y cuenta como vigente.
    /// </summary>
    public static Expression<Func<ConexionIntegracion, bool>> VerificadaVigente(DateTime ahoraUtc)
    {
        var limite = ahoraUtc - ConexionIntegracion.VentanaVerificacionIdentidad;
        var futuro = ahoraUtc.AddMinutes(5);
        return c => c.Proveedor != ProveedorIntegracion.Microsoft365
            || (c.IdentidadVerificadaUtc != null && c.IdentidadVerificadaUtc > limite && c.IdentidadVerificadaUtc <= futuro);
    }

}
