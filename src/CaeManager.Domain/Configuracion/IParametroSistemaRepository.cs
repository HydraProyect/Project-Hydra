namespace CaeManager.Domain.Configuracion;

/// <summary>
/// Fila única de configuración global (ver DATABASE.md, hoja "Parametros"
/// del Excel original) — por eso el repositorio no recibe Id, solo expone
/// "la" instancia.
/// </summary>
public interface IParametroSistemaRepository
{
    Task<ParametroSistema> ObtenerAsync(CancellationToken cancellationToken = default);
}
