namespace CaeManager.Web.Services;

/// <summary>
/// Cada cuánto vuelven a preguntar a la base la sesión y el circuito de Blazor si
/// siguen valiendo (hallazgo S-B-7, auditoría 2026-10-28; ASVS V3.3). Cada
/// intervalo es la ventana máxima que una sesión ya abierta sobrevive a una
/// revocación, así que no basta con un buen valor por defecto: tiene techo, y
/// un valor fuera de rango impide arrancar (<c>ValidateOnStart</c>) en vez de
/// recortarse en silencio o abrir la ventana sin límite.
///
/// <list type="bullet">
/// <item><c>Sesion:IntervaloRevalidacionSegundos</c> — sello de seguridad de la
/// cookie y del circuito (cuenta desactivada, sello rotado): de 1 a
/// <see cref="SesionMaximoSegundos"/>, 60 s por defecto.</item>
/// <item><c>Circuit:RevalidacionIntervaloSegundos</c> — Asignación de Cartera,
/// Encargo de administración, Sesión Privilegiada y su concesión, que
/// <see cref="RevalidacionCircuitoActivoHandler"/> revalida en cada tick: de 1 a
/// <see cref="CircuitoMaximoSegundos"/>, 60 s por defecto.</item>
/// </list>
///
/// Los dos techos son de 60 s, igual que el valor por defecto: es la cota que el
/// propietario fijó el 2026-09-23 para la memoización del alcance
/// (<c>CaducidadAlcanceOptions.MaximoSegundos</c>), que esa clase declara también para
/// la desactivación de una cuenta y la revocación de un Workspace operativo
/// derivado, y la invariante I3 del contrato del selector de Tenant beneficiario. La
/// configuración puede acortar la ventana, nunca alargarla.
/// </summary>
public sealed class IntervalosRevalidacionOptions
{
    public const string ClaveSesion = "Sesion:IntervaloRevalidacionSegundos";
    public const string ClaveCircuito = "Circuit:RevalidacionIntervaloSegundos";

    public const int SesionPorDefectoSegundos = 60;
    public const int SesionMaximoSegundos = 60;
    public const int CircuitoPorDefectoSegundos = RevalidacionCircuitoActivoHandler.IntervaloPorDefectoSegundos;
    public const int CircuitoMaximoSegundos = 60;

    public int SesionSegundos { get; set; } = SesionPorDefectoSegundos;
    public int CircuitoSegundos { get; set; } = CircuitoPorDefectoSegundos;
}

public static class RegistroIntervalosRevalidacion
{
    public static IServiceCollection AddIntervalosRevalidacion(this IServiceCollection services, IConfiguration configuracion)
    {
        services.AddOptions<IntervalosRevalidacionOptions>()
            .Configure(o =>
            {
                o.SesionSegundos = configuracion.GetValue(
                    IntervalosRevalidacionOptions.ClaveSesion, IntervalosRevalidacionOptions.SesionPorDefectoSegundos);
                o.CircuitoSegundos = configuracion.GetValue(
                    IntervalosRevalidacionOptions.ClaveCircuito, IntervalosRevalidacionOptions.CircuitoPorDefectoSegundos);
            })
            .Validate(
                o => o.SesionSegundos is >= 1 and <= IntervalosRevalidacionOptions.SesionMaximoSegundos,
                $"{IntervalosRevalidacionOptions.ClaveSesion} debe estar entre 1 y {IntervalosRevalidacionOptions.SesionMaximoSegundos} segundos.")
            .Validate(
                o => o.CircuitoSegundos is >= 1 and <= IntervalosRevalidacionOptions.CircuitoMaximoSegundos,
                $"{IntervalosRevalidacionOptions.ClaveCircuito} debe estar entre 1 y {IntervalosRevalidacionOptions.CircuitoMaximoSegundos} segundos.")
            .ValidateOnStart();
        return services;
    }
}
