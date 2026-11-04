namespace CaeManager.Web.Features.AltaAutonoma.Antiabuso;

/// <summary>
/// Cifras del antiabuso del alta autónoma (sección <c>AltaAutonoma:Antiabuso</c>). Todas son HYPOTHESIS: se ajustan
/// con métricas reales del registro. Los valores por defecto son los del plan del alta autónoma (§ 4): 5 inicios por
/// hora y 20 por día por IP, envío no antes de 3 s desde que se pintó el formulario.
/// </summary>
public sealed class OpcionesAntiabusoAlta
{
    public const string Seccion = "AltaAutonoma:Antiabuso";

    /// <summary>Inicios de alta (POST) por IP en una ventana fija de una hora.</summary>
    public int IniciosPorHoraPorIp { get; set; } = 5;

    /// <summary>Inicios de alta (POST) por IP en una ventana fija de un día, contados solo entre los que pasan la hora.</summary>
    public int IniciosPorDiaPorIp { get; set; } = 20;

    /// <summary>
    /// Bits cero iniciales que exige el reto de prueba de trabajo. Cada bit duplica el trabajo esperado: con 2^n
    /// intentos de media, 15 bits son unos 33.000 hashes. Medido en un portátil con Node 24 y <c>crypto.subtle</c> por
    /// lotes, como el Web Worker: unos 24.000 hashes por segundo, así que 15 bits salen a 1,4 s de media (la búsqueda es
    /// geométrica: una de cada diez pasa de 3 s). El navegador no se midió: HYPOTHESIS de que queda en el mismo orden.
    /// </summary>
    public int DificultadBits { get; set; } = 15;

    /// <summary>Vida del reto: cubre el tiempo de rellenar el formulario, que lo resuelve al pintarse.</summary>
    public TimeSpan VigenciaReto { get; set; } = TimeSpan.FromMinutes(20);

    /// <summary>Tiempo mínimo entre el pintado del formulario y su envío; por debajo, lo envía un programa.</summary>
    public TimeSpan TiempoMinimoEnvio { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>Vida del sello de tiempo del formulario.</summary>
    public TimeSpan VigenciaSelloFormulario { get; set; } = TimeSpan.FromHours(2);

    public bool EsValida() =>
        IniciosPorHoraPorIp is >= 1 and <= 10_000
        && IniciosPorDiaPorIp >= IniciosPorHoraPorIp && IniciosPorDiaPorIp <= 100_000
        && DificultadBits is >= 8 and <= 28
        && VigenciaReto >= TimeSpan.FromMinutes(1) && VigenciaReto <= TimeSpan.FromHours(2)
        && TiempoMinimoEnvio >= TimeSpan.Zero && TiempoMinimoEnvio <= TimeSpan.FromMinutes(1)
        && VigenciaSelloFormulario > TiempoMinimoEnvio && VigenciaSelloFormulario <= TimeSpan.FromHours(24);
}
