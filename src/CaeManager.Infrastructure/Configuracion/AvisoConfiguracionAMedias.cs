using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Infrastructure.Configuracion;

/// <summary>Un gate que se ha quedado cerrado por configuración a medias, ya evaluado al componer el contenedor.</summary>
public sealed record AvisoConfiguracionAMedias(string Seccion, IReadOnlyList<string> Problemas, string Consecuencia);

public static class AvisoConfiguracionAMediasExtensions
{
    /// <summary>
    /// Deja dicho en el arranque que <paramref name="seccion"/> está a medias.
    /// Se llama justo después de decidir el gate y con las MISMAS opciones que lo
    /// decidieron, así que el aviso cuenta exactamente lo que el gate vio. No
    /// altera cuándo se registra ningún servicio: solo añade, y solo si hay
    /// algo que avisar, un único servicio de arranque compartido por todos los
    /// gates.
    /// </summary>
    public static IServiceCollection AvisarSiConfiguracionAMedias(
        this IServiceCollection services, string seccion, IOpcionesConGate opciones, string consecuencia)
    {
        var problemas = opciones.ProblemasDeConfiguracion();
        if (problemas.Count == 0)
            return services;

        services.AddSingleton(new AvisoConfiguracionAMedias(seccion, problemas, consecuencia));
        // AddHostedService es idempotente (TryAddEnumerable por dentro): varios gates a medias comparten un solo
        // servicio. Se registra con AddHostedService y no con TryAddEnumerable a mano porque el trinquete de actor
        // de sistema busca esa forma para dar por comprobada la declaración de la clase.
        services.AddHostedService<AvisoConfiguracionAMediasHostedService>();
        return services;
    }
}
