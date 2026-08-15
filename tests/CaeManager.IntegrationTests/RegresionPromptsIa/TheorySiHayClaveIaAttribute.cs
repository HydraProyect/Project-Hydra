namespace CaeManager.IntegrationTests.RegresionPromptsIa;

/// <summary>
/// <see cref="TheoryAttribute"/> que se salta a sí misma (en vez de fallar)
/// cuando la clave real del proveedor no está configurada en este entorno —
/// mismo patrón "inerte por defecto" que ya sigue cada
/// <c>IDocumentAIProvider</c> en producción (ver AnthropicDocumentAIProvider
/// et al.), aplicado ahora al propio test (MACRO_PLAN § 6.6: la suite de
/// regresión de prompts debe existir lista para correr, no bloquear CI por
/// falta de credenciales).
///
/// xUnit v2 no soporta un Skip dinámico en <see cref="TheoryAttribute"/>
/// directamente (la propiedad Skip normalmente se fija en el propio
/// atributo del método) — el truco estándar es evaluarlo en el constructor
/// de una subclase, que sí corre en tiempo de descubrimiento de tests, leyendo
/// el entorno real de esa ejecución.
///
/// <paramref name="seccionConfiguracion"/> debe coincidir exactamente con
/// <c>AnthropicOptions.SeccionConfiguracion</c>/<c>GeminiOptions.SeccionConfiguracion</c>/
/// <c>MistralOcrOptions.SeccionConfiguracion</c> — la variable de entorno
/// comprobada es <c>{seccionConfiguracion}__ApiKey</c>, la convención
/// estándar de .NET para que <c>IConfiguration</c> resuelva
/// <c>{seccionConfiguracion}:ApiKey</c> desde el entorno (mismo mecanismo
/// que usa la app en producción — sin appsettings.Development.json ni
/// user-secrets configurados para estas opciones en este repo).
/// </summary>
public sealed class TheorySiHayClaveIaAttribute : TheoryAttribute
{
    public TheorySiHayClaveIaAttribute(string seccionConfiguracion)
    {
        var variableEntorno = $"{seccionConfiguracion}__ApiKey";

        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(variableEntorno)))
        {
            Skip = $"Requiere una clave real de {seccionConfiguracion} en la variable de entorno " +
                   $"\"{variableEntorno}\" — no configurada en este entorno. Ver MACRO_PLAN § 6.6 " +
                   "y docs/ARQUITECTURA-IA-DOCUMENTAL.md § 4.1.";
        }
    }
}
