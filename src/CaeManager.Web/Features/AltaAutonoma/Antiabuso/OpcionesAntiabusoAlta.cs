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
    /// Consultas al día de un mismo CIF o NIF en el paso «Empresa» (§ 4.1 del plan: 3), sumando todas las IP
    /// (<see cref="CupoConsultasCifAlta"/>).
    /// </summary>
    public int ConsultasPorDiaPorCif { get; set; } = 3;

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

    /// <summary>Hashes por segundo medidos con Node 24 y <c>crypto.subtle</c> por lotes (ver <see cref="DificultadBits"/>).</summary>
    public const double HashesPorSegundoMedidos = 24_000;

    /// <summary>
    /// Veces la búsqueda media que la vigencia del reto tiene que cubrir. La búsqueda es geométrica: la probabilidad de
    /// pasar de k veces la media es e^-k, así que con 20 un reto caduca a medio resolver menos de una vez entre 400
    /// millones al ritmo medido, y aún cubre de sobra un equipo varias veces más lento.
    /// </summary>
    public const int HolguraVigenciaSobreBusquedaMedia = 20;

    /// <summary>Segundos de búsqueda media del reto al ritmo medido: 2^bits intentos entre los hashes por segundo.</summary>
    public double SegundosBusquedaMedia => Math.Pow(2, DificultadBits) / HashesPorSegundoMedidos;

    /// <summary>
    /// Cuánto antes que el servidor da el guion por caducado el reto: cubre la latencia entre emitirlo y pintarlo,
    /// porque el guion cuenta la vigencia desde que arranca. El componente pinta ya la vigencia efectiva.
    /// </summary>
    public static readonly TimeSpan MargenCaducidadGuion = TimeSpan.FromSeconds(10);

    /// <summary>Vigencia que cuenta el guion del navegador: la del servidor menos el margen.</summary>
    public TimeSpan VigenciaRetoEnGuion => VigenciaReto - MargenCaducidadGuion;

    public bool EsValida() =>
        IniciosPorHoraPorIp is >= 1 and <= 10_000
        && IniciosPorDiaPorIp >= IniciosPorHoraPorIp && IniciosPorDiaPorIp <= 100_000
        && ConsultasPorDiaPorCif is >= 1 and <= 1_000
        && DificultadBits is >= 8 and <= 28
        && VigenciaReto >= TimeSpan.FromMinutes(1) && VigenciaReto <= TimeSpan.FromHours(2)
        && TiempoMinimoEnvio >= TimeSpan.Zero && TiempoMinimoEnvio <= TimeSpan.FromMinutes(1)
        // Dificultad, tiempo mínimo y vigencia van juntos: si el reto caduca en el navegador antes de poder resolverse
        // y de pasar el tiempo mínimo, ningún envío es aceptable (la trampa de tiempo lo rechaza antes y el reto
        // caducado después). Se suman, aunque corran a la vez, para quedar del lado seguro.
        && VigenciaRetoEnGuion.TotalSeconds >= TiempoMinimoEnvio.TotalSeconds + HolguraVigenciaSobreBusquedaMedia * SegundosBusquedaMedia
        // El sello de tiempo tiene que durar al menos lo que el reto: si caducara antes, el envío que el guion libera
        // con el reto aún vigente lo rechazaría la trampa de tiempo.
        && VigenciaSelloFormulario > TiempoMinimoEnvio && VigenciaSelloFormulario >= VigenciaReto
        && VigenciaSelloFormulario <= TimeSpan.FromHours(24);
}
