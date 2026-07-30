using CaeManager.Application.Common;

namespace CaeManager.Web.Services;

/// <summary>
/// Lee el Delegated Workspace activo desde una cookie en vez de guardarlo
/// solo en memoria — necesario porque cambiar de cliente activo requiere un
/// reload completo del navegador (ver <c>SelectorClienteActivo.razor.cs</c>
/// y el endpoint <c>/cuenta/cliente-activo/{tenantId}</c>): un valor
/// puramente en memoria vive en el scope de DI del circuito de Blazor
/// Server, y ese scope se destruye por completo en cada reload — con solo
/// memoria, la elección nunca sobreviviría al propio mecanismo que la
/// aplica (bug real encontrado al probar el selector: cambiar de cliente
/// con <c>forceLoad: true</c> revertía siempre al tenant de origen). La
/// cookie sí sobrevive al reload, así que <c>ITenantActual</c> puede
/// resolver el nuevo tenant desde la primera petición del circuito nuevo.
///
/// La cookie nunca se trata como autorización por sí sola: quien la escribe
/// (el endpoint, no este servicio) ya revalidó contra
/// <c>DelegacionTenant</c>/<c>AsignacionOperadorDelegado</c> antes de
/// fijarla — este servicio solo la lee, sin repetir esa comprobación en
/// cada resolución síncrona de <c>ITenantActual.TenantId</c> (esa propiedad
/// es síncrona porque EF Core la evalúa dentro de <c>HasQueryFilter</c>; una
/// consulta a base de datos en cada evaluación del filtro global sería una
/// regresión de rendimiento severa).
/// </summary>
public class ClienteActivoSeleccionado(IHttpContextAccessor httpContextAccessor) : IClienteActivoSeleccionado
{
    public const string NombreCookie = "cae_cliente_activo";

    private Guid? _tenantIdSeleccionado;
    private bool _leidoDeCookie;

    public Guid? TenantIdSeleccionado
    {
        get
        {
            if (!_leidoDeCookie)
            {
                var valorCookie = httpContextAccessor.HttpContext?.Request.Cookies[NombreCookie];
                if (Guid.TryParse(valorCookie, out var tenantIdCookie))
                    _tenantIdSeleccionado = tenantIdCookie;

                _leidoDeCookie = true;
            }

            return _tenantIdSeleccionado;
        }
    }
}
