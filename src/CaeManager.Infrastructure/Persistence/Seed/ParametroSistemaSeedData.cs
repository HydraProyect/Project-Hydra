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
    public const int HorasAvisoVisita = 48;
    public const int HorasCriticasVisita = 24;

    public static readonly TimeOnly HoraInicioJornada = new(8, 0);
    public static readonly TimeOnly HoraFinJornada = new(18, 0);
    public const int HorasJornadaMensualGestor = 160;

    /// <summary>Apagada de fábrica — encenderla es una decisión de la consultora, con información previa a la plantilla (art. 87 LOPDGDD).</summary>
    public const bool MedicionTiempoActiva = false;
    public const int SegundosInactividadPausa = 120;
    public const bool ExcluirFueraDeJornadaEnMetricas = true;
}
