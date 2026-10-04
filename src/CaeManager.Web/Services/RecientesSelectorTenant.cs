namespace CaeManager.Web.Services;

/// <summary>
/// «Recientes» del selector de Tenant beneficiario: preferencia de interfaz del propio usuario, guardada
/// en su navegador (cookie), sin base de datos y sin cruzar Tenants. Solo guarda Ids de Tenants que el
/// usuario acaba de abrir con éxito, y va ligada al usuario (otra cuenta en el mismo navegador no la ve).
/// <b>No tiene valor de seguridad</b>: quien la lee la recorta siempre al conjunto autorizado vigente,
/// así que un Id manipulado o ya revocado no aparece, y mostrarlo tampoco autoriza abrirlo (el cambio
/// sigue siendo el POST revalidado de <c>/cuenta/cliente-activo</c>).
/// </summary>
public static class RecientesSelectorTenant
{
    public const string NombreCookie = "cae_tenants_recientes";
    public const int Maximo = 3;
    public static readonly TimeSpan Vigencia = TimeSpan.FromDays(30);

    public static void Registrar(HttpContext httpContext, Guid usuarioId, Guid tenantId)
    {
        var lista = Leer(httpContext.Request, usuarioId).Where(id => id != tenantId).Prepend(tenantId).Take(Maximo);
        httpContext.Response.Cookies.Append(
            NombreCookie,
            $"{usuarioId:N}|{string.Join(',', lista.Select(id => id.ToString("N")))}",
            new CookieOptions
            {
                HttpOnly = true,
                Path = "/",
                Secure = httpContext.Request.IsHttps,
                SameSite = SameSiteMode.Lax,
                MaxAge = Vigencia,
            });
    }

    /// <summary>Del más reciente al más antiguo; vacío si no hay cookie, es de otro usuario o está malformada.</summary>
    public static IReadOnlyList<Guid> Leer(HttpRequest peticion, Guid usuarioId) => Interpretar(peticion.Cookies[NombreCookie], usuarioId);

    public static IReadOnlyList<Guid> Interpretar(string? valor, Guid usuarioId)
    {
        if (string.IsNullOrEmpty(valor)) return [];

        var partes = valor.Split('|');
        if (partes.Length != 2 || !Guid.TryParseExact(partes[0], "N", out var dueno) || dueno != usuarioId) return [];

        return partes[1].Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => Guid.TryParseExact(p, "N", out var id) ? id : (Guid?)null)
            .OfType<Guid>()
            .Distinct()
            .Take(Maximo)
            .ToList();
    }
}

/// <summary>Lee los recientes de la petición HTTP del circuito; sin contexto HTTP devuelve vacío.</summary>
public interface ILectorRecientesSelectorTenant
{
    IReadOnlyList<Guid> Leer(Guid usuarioId);
}

public sealed class LectorRecientesSelectorTenant(IHttpContextAccessor httpContextAccessor) : ILectorRecientesSelectorTenant
{
    public IReadOnlyList<Guid> Leer(Guid usuarioId) =>
        httpContextAccessor.HttpContext is { } contexto ? RecientesSelectorTenant.Leer(contexto.Request, usuarioId) : [];
}
