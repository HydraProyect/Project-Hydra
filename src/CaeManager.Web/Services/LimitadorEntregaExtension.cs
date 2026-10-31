using System.Threading.RateLimiting;
using CaeManager.Infrastructure.Autenticacion;
using Microsoft.AspNetCore.DataProtection;

namespace CaeManager.Web.Services;

/// <summary>
/// Límite de ritmo de la entrega de archivos a la extensión de navegador (política <c>ExtensionEntrega</c>).
///
/// <para>
/// El limitador corre antes de <c>UseAuthorization</c> y el esquema de extensión aún no se ha ejecutado (ver
/// <see cref="LimitadorApiPublica"/>), así que la partición lee ella misma el token: descifrarlo no toca la base, y
/// solo se usa para repartir el cupo, nunca para autorizar. El cupo es por usuario y sesión de inicio, no por IP ni por
/// token: en una oficina varios Gestores CAE comparten IP, y emitir otro token desde la misma sesión no da un cupo nuevo.
/// Lleva la sesión porque aquí aún no se comprueba la revocación: un token revocado de una sesión anterior no agota el
/// cupo de la sesión viva del mismo usuario. Un token ilegible o ausente cuenta por IP.
/// </para>
///
/// <para>
/// Una entrega gasta dos permisos (ticket y archivo). El contador vive en memoria por instancia y se reinicia con cada
/// despliegue.
/// </para>
/// </summary>
public static class LimitadorEntregaExtension
{
    public const string Politica = "ExtensionEntrega";

    private const string EsquemaCabecera = "Extension ";

    public static RateLimitPartition<string> ParticionPorUsuario(
        HttpContext contexto, int limitePorMinuto, IDataProtectionProvider dataProtectionProvider)
    {
        var cabecera = contexto.Request.Headers.Authorization.ToString();
        var carga = cabecera.StartsWith(EsquemaCabecera, StringComparison.OrdinalIgnoreCase)
            ? TokenSesionExtension.Leer(dataProtectionProvider, cabecera[EsquemaCabecera.Length..].Trim())
            : null;

        var particion = carga is null
            ? $"ip:{LimitadorApiPublica.IpDelCliente(contexto)}"
            : $"usuario:{carga.UsuarioId:N}:{carga.Sid:N}";

        return RateLimitPartition.GetFixedWindowLimiter(
            particion,
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = limitePorMinuto,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            });
    }
}
