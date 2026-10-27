using System.Text;
using System.Text.Json;
using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace CaeManager.Infrastructure.DependencyInjection;

/// <summary>
/// Presencia en el contenedor = el chat de Vega responde con el doble de pruebas y no con
/// Anthropic. Solo lo registra <see cref="AsistenteDePruebasExtensions.AddAsistenteDePruebas"/>.
/// La UI lo lee para mostrar Consulta PRL sin que haga falta una <c>Anthropic:ApiKey</c>: poner
/// una clave ficticia en <c>AnthropicOptions</c> encendería también la extracción, la detección
/// de correo y el proveedor documental de Anthropic, que sí saldrían a la red.
/// </summary>
public sealed class MarcaAsistenteDePruebas;

public static class AsistenteDePruebasExtensions
{
    /// <summary>Solo la fija el arnés E2E (tests/CaeManager.E2ETests/WebAppFixture.cs).</summary>
    public const string ClaveConfiguracion = "AsistenteIa:ProveedorFalsoActivo";

    /// <summary>
    /// Sustituye <see cref="IAsistenteIaService"/> por un doble sin red cuando
    /// <see cref="ClaveConfiguracion"/> está activa. Falla cerrado: con la clave activa fuera de
    /// Development o Test, o sin <c>DatosPrueba:Activo</c>, la composición lanza y la app no
    /// arranca. Staging y producción arrancan como Production (Dockerfile).
    /// </summary>
    public static IServiceCollection AddAsistenteDePruebas(
        this IServiceCollection services, IConfiguration configuration, IHostEnvironment entorno)
    {
        if (!configuration.GetValue<bool>(ClaveConfiguracion))
            return services;

        if ((!entorno.IsDevelopment() && !entorno.IsEnvironment("Test"))
            || !configuration.GetValue<bool>("DatosPrueba:Activo"))
            throw new InvalidOperationException(
                $"El asistente falso ({ClaveConfiguracion}) exige Development o Test y DatosPrueba:Activo.");

        services.RemoveAll<IAsistenteIaService>();
        services.AddSingleton<IAsistenteIaService, AsistenteDePruebas>();
        services.AddSingleton<MarcaAsistenteDePruebas>();
        return services;
    }

    private sealed class AsistenteDePruebas : IAsistenteIaService
    {
        public Task<Result<string>> PreguntarAsync(
            IReadOnlyList<MensajeChatDto> historial, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Base64 conserva exactamente lo recibido aunque Application restaure los marcadores al volver.
            var recibido = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(historial)));
            return Task.FromResult(Result.Exito($"Respuesta del doble de Vega. Historial recibido: {recibido}"));
        }
    }
}
