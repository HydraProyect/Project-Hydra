namespace CaeManager.Infrastructure.Persistence.Seed;

/// <summary>
/// Fila única de configuración, con los umbrales reales de la hoja
/// "Parametros" del Excel original (ver DATABASE.md).
/// </summary>
public static class ParametroSistemaSeedData
{
    public static readonly Guid IdUnico = new("20000000-0000-0000-0000-000000000001");
    public const int UmbralAmbarDias = 30;
    public const int UmbralRojoDias = 15;
}
