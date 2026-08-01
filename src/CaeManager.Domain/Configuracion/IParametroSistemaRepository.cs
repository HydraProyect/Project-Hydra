namespace CaeManager.Domain.Configuracion;

/// <summary>
/// Fila única de configuración global (ver DATABASE.md, hoja "Parametros"
/// del Excel original) — por eso el repositorio no recibe Id, solo expone
/// "la" instancia.
/// </summary>
public interface IParametroSistemaRepository
{
    Task<ParametroSistema> ObtenerAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Solo para aprovisionar la fila única de un tenant recién creado (ver
    /// CrearClienteDeleganteCommand) — el tenant #1 la recibe por HasData de
    /// migración y nunca pasa por aquí.
    /// </summary>
    void Agregar(ParametroSistema parametro);
}
