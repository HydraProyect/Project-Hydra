using CaeManager.Infrastructure.Email;
using Microsoft.Extensions.Options;

namespace CaeManager.Web.Services;

public sealed class EnlacesPublicos(
    IOptions<SmtpEmailOptions> opciones, IHostEnvironment entorno, ILogger<EnlacesPublicos> logger)
{
    private int _avisado;

    public Uri? ObtenerBase(string basePeticion)
    {
        var configurada = opciones.Value.UrlBasePublica;
        if (string.IsNullOrWhiteSpace(configurada) && entorno.IsDevelopment())
            configurada = basePeticion;

        var basePublica = UrlBasePublica.Normalizar(configurada, exigirHttps: !entorno.IsDevelopment());
        if (basePublica is not null) return basePublica;

        // Con la validación de arranque (ValidadorUrlBasePublica) esto no debería
        // ocurrir; si ocurre, una vez basta: esta ruta la alcanza un anónimo.
        if (Interlocked.Exchange(ref _avisado, 1) == 0)
            logger.LogError("No se puede generar el enlace: falta una URL válida en Smtp:UrlBasePublica.");
        return null;
    }

    public string? Activacion(Guid usuarioId, string tokenCodificado, string basePeticion)
    {
        var basePublica = ObtenerBase(basePeticion);
        return basePublica is null ? null : Activacion(basePublica, usuarioId, tokenCodificado);
    }

    public static string Activacion(Uri basePublica, Guid usuarioId, string tokenCodificado) =>
        new Uri(basePublica, $"cuenta/restablecer-contrasena?userId={usuarioId}&code={Uri.EscapeDataString(tokenCodificado)}").AbsoluteUri;
}
