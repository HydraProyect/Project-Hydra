using Microsoft.AspNetCore.Components.Endpoints;
using Microsoft.AspNetCore.Routing.Template;

namespace CaeManager.Web.Services;

/// <summary>
/// Traduce la ruta a la que navega el circuito a la <b>plantilla</b> de la página
/// que la sirve (<c>/empresas/{Id:guid}</c>), nunca a la ruta concreta: el
/// registro por carga de página no puede llevar ids ni parámetros. Las
/// plantillas salen de los endpoints de componentes que registra
/// <c>MapRazorComponents</c>, así que no hay una segunda lista que mantener.
///
/// <para>
/// Se ordenan de más a menos específica (precedencia de entrada de ASP.NET Core
/// routing) y gana la primera que encaja. <see cref="TemplateMatcher"/> no
/// evalúa restricciones (<c>:guid</c>, <c>:int</c>): basta para etiquetar una
/// medición, no para enrutar. Lo que no encaja con ninguna sale como
/// <see cref="SinPlantilla"/>, sin la ruta.
/// </para>
/// </summary>
public sealed class PlantillasDeRutaDePagina
{
    public const string SinPlantilla = "(sin plantilla)";

    private readonly Lazy<IReadOnlyList<(TemplateMatcher Comparador, string Plantilla)>> _plantillas;

    public PlantillasDeRutaDePagina(EndpointDataSource fuente) =>
        _plantillas = new(() => Construir(fuente), LazyThreadSafetyMode.ExecutionAndPublication);

    public string Resolver(string ruta)
    {
        var camino = new PathString(ruta.StartsWith('/') ? ruta : "/" + ruta);
        foreach (var (comparador, plantilla) in _plantillas.Value)
        {
            if (comparador.TryMatch(camino, new RouteValueDictionary()))
                return plantilla;
        }

        return SinPlantilla;
    }

    private static IReadOnlyList<(TemplateMatcher, string)> Construir(EndpointDataSource fuente) =>
        fuente.Endpoints
            .OfType<RouteEndpoint>()
            .Where(e => e.Metadata.GetMetadata<ComponentTypeMetadata>() is not null && e.RoutePattern.RawText is not null)
            .Select(e => (Plantilla: new RouteTemplate(e.RoutePattern), e.RoutePattern))
            .OrderBy(p => RoutePrecedence.ComputeInbound(p.Plantilla))
            .Select(p => (new TemplateMatcher(p.Plantilla, new RouteValueDictionary(p.RoutePattern.Defaults)),
                Normalizar(p.RoutePattern.RawText!)))
            .DistinctBy(p => p.Item2)
            .ToList();

    private static string Normalizar(string plantilla) => plantilla.StartsWith('/') ? plantilla : "/" + plantilla;
}
