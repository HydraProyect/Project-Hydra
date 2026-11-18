using System.Security.Cryptography;
using CaeManager.Domain.Operaciones;
using Microsoft.AspNetCore.DataProtection;

namespace CaeManager.Web.Services;

/// <summary>
/// El último papel (servicio de la Asignación de Operación) que el usuario eligió para cada Tenant beneficiario
/// (decisión D-4 de Chris del 2026-11-16): preferencia de interfaz guardada en su navegador, sin base de datos, ligada al
/// usuario y protegida con DataProtection para que no se pueda leer ni fabricar desde fuera. Solo se escribe tras un
/// cambio de Tenant ya autorizado por <c>/cuenta/cliente-activo</c>.
///
/// <para>
/// <b>No tiene valor de seguridad</b>: solo la lee el selector de la cabecera para preseleccionar el papel de las fijadas,
/// las recientes y los atajos Alt+1…5, que van por Tenant, y el servicio viaja en el formulario y el POST lo revalida. El
/// endpoint no la lee: un formulario sin <c>servicio</c> (Mi trabajo, Visión de cartera, enlaces a una ficha) sigue
/// abriendo Outbound. Un papel recordado que ya no está entre las entradas autorizadas se ignora y se usa la que haya;
/// una cookie de otro usuario, manipulada o de otra clave se lee como vacía.
/// </para>
/// </summary>
public static class PapelRecordadoSelectorTenant
{
    public const string NombreCookie = "cae_papel_recordado";
    public const string Proposito = "CaeManager.Web.PapelRecordadoSelectorTenant.v1";

    /// <summary>Cuántos Tenants recuerda: los más recientes; el resto vuelve al papel de siempre.</summary>
    public const int Maximo = 50;

    public static readonly TimeSpan Vigencia = TimeSpan.FromDays(30);

    public static void Registrar(
        HttpContext httpContext, IDataProtectionProvider proveedor, Guid usuarioId, Guid tenantId, ServicioCae servicio)
    {
        var previos = Leer(httpContext.Request, proveedor, usuarioId)
            .Where(p => p.Key != tenantId)
            .Select(p => (p.Key, p.Value));
        var lista = previos.Prepend((tenantId, servicio)).Take(Maximo);
        var claro = $"{usuarioId:N}|{string.Join(',', lista.Select(p => $"{p.Item1:N}:{Codigo(p.Item2)}"))}";

        httpContext.Response.Cookies.Append(
            NombreCookie,
            proveedor.CreateProtector(Proposito).Protect(claro),
            new CookieOptions
            {
                HttpOnly = true,
                Path = "/",
                Secure = httpContext.Request.IsHttps,
                SameSite = SameSiteMode.Lax,
                MaxAge = Vigencia,
            });
    }

    public static IReadOnlyDictionary<Guid, ServicioCae> Leer(HttpRequest peticion, IDataProtectionProvider proveedor, Guid usuarioId) =>
        Interpretar(peticion.Cookies[NombreCookie], proveedor, usuarioId);

    /// <summary>Vacío si no hay cookie, no se puede desproteger, es de otro usuario o está malformada.</summary>
    public static IReadOnlyDictionary<Guid, ServicioCae> Interpretar(string? valor, IDataProtectionProvider proveedor, Guid usuarioId)
    {
        if (string.IsNullOrEmpty(valor)) return new Dictionary<Guid, ServicioCae>();

        string claro;
        try
        {
            claro = proveedor.CreateProtector(Proposito).Unprotect(valor);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            // Manipulada, de otra clave o en claro: se lee como vacía.
            return new Dictionary<Guid, ServicioCae>();
        }

        var partes = claro.Split('|');
        if (partes.Length != 2 || !Guid.TryParseExact(partes[0], "N", out var dueno) || dueno != usuarioId)
            return new Dictionary<Guid, ServicioCae>();

        var resultado = new Dictionary<Guid, ServicioCae>();
        foreach (var entrada in partes[1].Split(',', StringSplitOptions.RemoveEmptyEntries).Take(Maximo))
        {
            var par = entrada.Split(':');
            if (par.Length == 2 && Guid.TryParseExact(par[0], "N", out var tenant) && Servicio(par[1]) is { } servicio)
                resultado.TryAdd(tenant, servicio);
        }
        return resultado;
    }

    private static string Codigo(ServicioCae servicio) => servicio == ServicioCae.Inbound ? "I" : "O";

    private static ServicioCae? Servicio(string codigo) => codigo switch
    {
        "O" => ServicioCae.Outbound,
        "I" => ServicioCae.Inbound,
        _ => null,
    };
}

/// <summary>Lee los papeles recordados de la petición HTTP del circuito; sin contexto HTTP devuelve vacío.</summary>
public interface ILectorPapelRecordadoSelectorTenant
{
    IReadOnlyDictionary<Guid, ServicioCae> Leer(Guid usuarioId);
}

public sealed class LectorPapelRecordadoSelectorTenant(
    IHttpContextAccessor httpContextAccessor, IDataProtectionProvider proveedor) : ILectorPapelRecordadoSelectorTenant
{
    public IReadOnlyDictionary<Guid, ServicioCae> Leer(Guid usuarioId) =>
        httpContextAccessor.HttpContext is { } contexto
            ? PapelRecordadoSelectorTenant.Leer(contexto.Request, proveedor, usuarioId)
            : new Dictionary<Guid, ServicioCae>();
}
