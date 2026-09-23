namespace CaeManager.Infrastructure.Autorizacion;

/// <summary>
/// Cuánto sirve <see cref="AlcanceDatosService"/> un alcance memoizado antes de volver a
/// resolverlo contra la base. Es la cota declarada de cuánto tarda un circuito de Blazor Server
/// ya abierto en dejar de LEER con un alcance revocado desde fuera de él (Asignación de Cartera
/// cerrada por otro usuario o llegada a su fin de vigencia). La escritura no depende de ella: se
/// autoriza siempre con el alcance recién resuelto (ver <c>InvalidacionAlcanceBehavior</c>).
///
/// <para>
/// Configurable con <c>Alcance:CaducidadMemoizacionSegundos</c>, acotada a [1, 60] s: la
/// configuración puede acortar la cota, nunca alargarla. Por defecto (y como máximo) 60 s, la misma cota
/// que ya tienen la desactivación de una cuenta (<c>Sesion:IntervaloRevalidacionSegundos</c>) y
/// la revocación de un Workspace operativo derivado (<c>Circuit:RevalidacionIntervaloSegundos</c>).
/// El coste es volver a resolver el alcance como mucho una vez por ventana, Tenant y circuito que
/// lea datos.
/// </para>
/// </summary>
public sealed class CaducidadAlcanceOptions
{
    public const string ClaveConfiguracion = "Alcance:CaducidadMemoizacionSegundos";

    /// <summary>Techo de la cota: 60 s, decisión del propietario (2026-09-23).</summary>
    public const int MaximoSegundos = 60;

    public static readonly TimeSpan CaducidadPorDefecto = TimeSpan.FromSeconds(MaximoSegundos);

    /// <summary>
    /// Convierte el valor configurado en caducidad, acotado a [1, <see cref="MaximoSegundos"/>] s:
    /// un valor mayor (o un cero o negativo) no puede dejar la memoización sin cota.
    /// </summary>
    public static TimeSpan DesdeSegundos(int segundos) =>
        TimeSpan.FromSeconds(Math.Clamp(segundos, 1, MaximoSegundos));

    public TimeSpan Caducidad { get; set; } = CaducidadPorDefecto;
}
