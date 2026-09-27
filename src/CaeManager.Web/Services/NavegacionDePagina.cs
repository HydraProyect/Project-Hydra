namespace CaeManager.Web.Services;

/// <summary>
/// Si una petición es una página que el usuario va a ver: una navegación del
/// navegador o una navegación mejorada de Blazor. La usan el aviso de fin de
/// acceso (<see cref="RevalidacionClienteActivoMiddleware.PuedePintarElAviso"/>)
/// y el Tenant beneficiario por defecto (<see cref="TenantBeneficiarioPorDefectoMiddleware"/>).
///
/// <para>
/// Por orden: la navegación mejorada se reconoce por su marca en
/// <c>Accept</c> (es un <c>fetch</c>, así que su <c>Sec-Fetch-Dest</c> no
/// dice <c>document</c>). Si el navegador envía <c>Sec-Fetch-Dest</c>, solo
/// <c>document</c> es una página: un <c>fetch</c> de fondo que pida
/// <c>text/html</c> no lo es (hallazgo de Codex en #876). Sin esa cabecera
/// (contexto no seguro, cliente que no la envía) se recurre a que pida
/// <c>text/html</c>, que los recursos y las llamadas de fondo de Blazor no
/// piden.
/// </para>
/// </summary>
public static class NavegacionDePagina
{
    public static bool Es(HttpRequest peticion)
    {
        if (peticion.Path.StartsWithSegments("/_blazor", StringComparison.OrdinalIgnoreCase))
            return false;

        var aceptados = peticion.Headers.Accept;
        if (aceptados.Any(valor => valor?.Contains("blazor-enhanced-nav", StringComparison.OrdinalIgnoreCase) == true))
            return true;

        var destino = peticion.Headers["Sec-Fetch-Dest"];
        if (destino.Count > 0)
            return string.Equals(destino.ToString(), "document", StringComparison.OrdinalIgnoreCase);

        return aceptados.Any(valor => valor?.Contains("text/html", StringComparison.OrdinalIgnoreCase) == true);
    }
}
