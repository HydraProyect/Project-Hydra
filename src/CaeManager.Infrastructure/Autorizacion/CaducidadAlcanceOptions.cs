namespace CaeManager.Infrastructure.Autorizacion;

/// <summary>
/// Cuánto sirve <see cref="AlcanceDatosService"/> un alcance memoizado antes de volver a
/// resolverlo contra la base. Es la cota declarada de cuánto tarda un circuito de Blazor Server
/// ya abierto en dejar de LEER con un alcance revocado desde fuera de él (Asignación de Cartera
/// cerrada por otro usuario o llegada a su fin de vigencia). La escritura no depende de ella: se
/// autoriza siempre con el alcance recién resuelto (ver <c>InvalidacionAlcanceBehavior</c>).
///
/// <para>
/// Configurable con <c>Alcance:CaducidadMemoizacionSegundos</c>. Por defecto 60 s, la misma cota
/// que ya tienen la desactivación de una cuenta (<c>Sesion:IntervaloRevalidacionSegundos</c>) y
/// la revocación de un Workspace operativo derivado (<c>Circuit:RevalidacionIntervaloSegundos</c>).
/// El coste es volver a resolver el alcance como mucho una vez por ventana, Tenant y circuito que
/// lea datos.
/// </para>
/// </summary>
public sealed class CaducidadAlcanceOptions
{
    public const string ClaveConfiguracion = "Alcance:CaducidadMemoizacionSegundos";

    public static readonly TimeSpan CaducidadPorDefecto = TimeSpan.FromSeconds(60);

    public TimeSpan Caducidad { get; set; } = CaducidadPorDefecto;
}
