using CaeManager.Application.Configuracion;
using CaeManager.Application.TiposDocumento;
using CaeManager.Domain.Documentos;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Documentos;

/// <summary>
/// Carga los umbrales efectivos del Tenant del contexto para cada uno de sus TiposDocumento
/// (<see cref="UmbralesVencimientoPorTipo"/>): una lectura de Parámetros del sistema y otra de la vigencia de los tipos.
/// Todo lector que clasifique documentos en «Por vencer» parte de aquí, para que la regla de
/// <see cref="UmbralesVencimiento"/> sea la misma en todas las superficies.
/// </summary>
public static class UmbralesVencimientoDelTenant
{
    public static async Task<UmbralesVencimientoPorTipo> CargarAsync(
        IConfiguracionQueryContext configuracion, ITiposDocumentoQueryContext tipos, CancellationToken cancellationToken)
    {
        var parametros = await configuracion.ParametrosSistema
            .Select(p => new { p.UmbralAmbarDias, p.UmbralRojoDias })
            .SingleAsync(cancellationToken);
        return await CargarAsync(parametros.UmbralAmbarDias, parametros.UmbralRojoDias, tipos, cancellationToken);
    }

    /// <summary>Para el lector que ya ha leído Parámetros del sistema por otra razón.</summary>
    public static async Task<UmbralesVencimientoPorTipo> CargarAsync(
        int umbralAmbarDias, int umbralRojoDias, ITiposDocumentoQueryContext tipos, CancellationToken cancellationToken)
    {
        var vigencias = await tipos.TiposDocumento
            .Select(t => new { t.Id, t.VigenciaMeses })
            .ToDictionaryAsync(t => t.Id, t => t.VigenciaMeses, cancellationToken);
        return new UmbralesVencimientoPorTipo(umbralAmbarDias, umbralRojoDias, vigencias);
    }
}
