using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CaeManager.Web.Features.AltaAutonoma.Antiabuso;

public static class AntiabusoAltaServiceCollectionExtensions
{
    /// <summary>
    /// Registra las piezas antiabuso del alta autónoma: opciones validadas al arrancar, reto de prueba de trabajo con su
    /// registro de usos, trampas del formulario y lista de dominios desechables. Todas son singletons sin dependencias
    /// de base de datos. La política del limitador (<see cref="LimitadorAlta"/>) se registra en <c>AddRateLimiter</c>.
    /// </summary>
    public static IServiceCollection AddAntiabusoAlta(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<OpcionesAntiabusoAlta>()
            .Bind(configuration.GetSection(OpcionesAntiabusoAlta.Seccion))
            .Validate(o => o.EsValida())
            .ValidateOnStart();

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<RegistroRetosUsados>();
        services.AddSingleton(sp => new CupoDiarioAlta(sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<OpcionesAntiabusoAlta>>()));
        services.AddSingleton(sp => new AlertaConsultasCifAlta(sp.GetRequiredService<ILogger<AlertaConsultasCifAlta>>()));
        services.AddSingleton<ServicioRetoPruebaTrabajo>();
        services.AddSingleton<ServicioTrampasFormulario>();
        services.AddSingleton(_ => new ValidadorDominiosDesechables());
        return services;
    }
}
