using System.Security.Claims;
using CaeManager.Domain.ApiKeys;
using CaeManager.Infrastructure.Autenticacion;

namespace CaeManager.Web.Api.V1;

/// <summary>
/// Utilidades compartidas por los endpoints de <c>/api/v1</c>.
/// </summary>
public static class ApiV1
{
    /// <summary>
    /// Límite duro de página para la API pública, más bajo que el que se
    /// permite desde la UI interna (<c>int.MaxValue</c> en exportaciones):
    /// un tercero no debería poder pedir el catálogo entero en una sola
    /// llamada sin paginar.
    /// </summary>
    public const int TamanoPaginaMaximo = 100;

    public static int TamanoPagina(int solicitado) =>
        solicitado <= 0 ? 20 : Math.Min(solicitado, TamanoPaginaMaximo);

    public static int Pagina(int solicitada) => solicitada < 1 ? 1 : solicitada;

    /// <summary>
    /// Mapea todos los recursos del grupo. Las rutas que pasen por aquí son las que recorre
    /// <c>RutasApiV1DeclaranAlcanceTests</c>.
    /// </summary>
    public static IEndpointRouteBuilder MapRecursosApiV1(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapClientesApiEndpoints();
        endpoints.MapCentrosApiEndpoints();
        endpoints.MapTrabajadoresApiEndpoints();
        endpoints.MapDocumentosApiEndpoints();
        endpoints.MapAsignacionesApiEndpoints();
        return endpoints;
    }

    /// <summary>
    /// ¿Llama la Clave de Soporte TALVEG? Mientras viva conserva lo que ya leía. Los alcances estrechan solo
    /// a quien no lo sea: lo que no se reconoce, se estrecha.
    /// </summary>
    public static bool EsClaveDeSoporte(ClaimsPrincipal usuario) =>
        usuario.Identity is { IsAuthenticated: true, AuthenticationType: ApiKeyAuthenticationSchemeOptions.NombreEsquema };

    public static bool TieneAlcance(ClaimsPrincipal usuario, string alcance) =>
        usuario.Identity is { IsAuthenticated: true, AuthenticationType: ClaveIntegracionAuthenticationSchemeOptions.NombreEsquema }
        && usuario.HasClaim(ClaveIntegracionAuthenticationSchemeOptions.TipoClaimAlcance, alcance);

    public static bool VeDatosPersonalesDeTrabajador(ClaimsPrincipal usuario) =>
        EsClaveDeSoporte(usuario) || TieneAlcance(usuario, AlcancesIntegracion.TrabajadoresDatosPersonalesLeer);
}
