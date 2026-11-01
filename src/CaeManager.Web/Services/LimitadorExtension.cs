using System.Globalization;
using System.Threading.RateLimiting;
using CaeManager.Infrastructure.Autenticacion;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.RateLimiting;

namespace CaeManager.Web.Services;

/// <summary>
/// Cupo global de <c>/extension/*</c> (OWASP API4, ASVS V11.1.4): por cuenta si el token se descifra y está vigente, por
/// IP en cualquier otro caso. Corre antes de autenticar, así que el token solo reparte el cupo y nunca autoriza: un
/// token falsificado o caducado no se descifra y cae a la IP del emisor, de modo que no puede gastar el cupo de otra
/// cuenta. El cupo es por cuenta y sesión de inicio (usuario y sid), como en <see cref="LimitadorEntregaExtension"/>: no
/// se consulta la base, así que un token revocado pero vigente (hasta 8 h) aún se descifra, pero solo agota el cupo de
/// su propia sesión y no el de la sesión viva del Gestor CAE legítimo. Emitir otro token desde la misma sesión no da
/// un cupo nuevo.
/// El contador vive en memoria por instancia y se reinicia con cada despliegue.
/// </summary>
public static class LimitadorExtension
{
    private const int LongitudMaximaToken = 1024;

    public static RateLimitPartition<string>? ParticionGlobal(
        HttpContext contexto, int limitePorMinuto, IDataProtectionProvider proteccion)
    {
        if (!contexto.Request.Path.StartsWithSegments("/extension"))
            return null;

        const string esquema = "Extension ";
        var cabecera = contexto.Request.Headers.Authorization.ToString();
        var token = cabecera.StartsWith(esquema, StringComparison.OrdinalIgnoreCase)
            ? cabecera[esquema.Length..].Trim() : "";
        var carga = LeerCargaAcotada(token, proteccion);
        var clave = carga is null
            ? $"extension:ip:{LimitadorApiPublica.IpDelCliente(contexto)}"
            : $"extension:usuario:{carga.UsuarioId:N}:{carga.Sid:N}";

        return RateLimitPartition.GetFixedWindowLimiter(clave, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = limitePorMinuto,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
        });
    }

    /// <summary>
    /// Un token real mide unos 300 caracteres; el tope evita descifrar cabeceras arbitrariamente grandes antes de
    /// autenticar. Cualquier fallo al descifrar (clave ausente, almacén de claves caído) cuenta por IP: el limitador
    /// no puede convertir un fallo de descifrado en un 500.
    /// </summary>
    private static CargaUtilTokenExtension? LeerCargaAcotada(string token, IDataProtectionProvider proteccion)
    {
        if (token.Length == 0 || token.Length > LongitudMaximaToken)
            return null;
        try
        {
            return TokenSesionExtension.Leer(proteccion, token);
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static ValueTask RechazarAsync(OnRejectedContext contexto, CancellationToken cancelacion)
    {
        if (contexto.HttpContext.Request.Path.StartsWithSegments("/extension") &&
            contexto.Lease.TryGetMetadata(MetadataName.RetryAfter, out var espera))
            contexto.HttpContext.Response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling(espera.TotalSeconds))
                .ToString(CultureInfo.InvariantCulture);
        return ValueTask.CompletedTask;
    }
}
