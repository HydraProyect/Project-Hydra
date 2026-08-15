using CaeManager.Application.DocumentosIa.Common;
using CaeManager.Infrastructure.AsistenteIa;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CaeManager.IntegrationTests.RegresionPromptsIa;

/// <summary>
/// Suite de regresión de prompts (MACRO_PLAN § 6.6, "cada cambio de
/// prompt/modelo corre contra el corpus en CI como cualquier otro test"):
/// llama a la ruta real de extracción estructurada de cada
/// <see cref="IDocumentAIProvider"/> configurado (Anthropic/Gemini/Mistral,
/// nunca un doble) contra <see cref="CorpusRegresionIa"/> y compara el
/// resultado contra lo esperado. Sin esto, 6.1 (validación automática con
/// umbral de confianza) no tendría red de seguridad: un prompt que empeora
/// solo se detectaría a ojo, revisando /auditoria-ia después de que ya
/// afectó a documentos reales.
///
/// Cada método corre contra un proveedor distinto y se salta a sí mismo si
/// ese proveedor no tiene clave real configurada en este entorno (ver
/// <see cref="TheorySiHayClaveIaAttribute"/>) — no bloquea CI por falta de
/// credenciales, pero exige exactamente cero cambios de código para
/// activarse en cuanto la clave exista: basta con exportar
/// <c>Anthropic__ApiKey</c> / <c>Gemini__ApiKey</c> / <c>MistralOcr__ApiKey</c>
/// (mismas variables que usaría la app en producción, ver AnthropicOptions
/// et al.) en el entorno donde corran los tests.
/// </summary>
public class RegresionPromptsIaTests
{
    [TheorySiHayClaveIa(AnthropicOptions.SeccionConfiguracion)]
    [MemberData(nameof(CorpusRegresionIa.ComoTheoryData), MemberType = typeof(CorpusRegresionIa))]
    public async Task Anthropic_extrae_los_campos_esperados_del_corpus(DocumentoCorpusIa documento) =>
        await EjecutarYVerificarAsync(CrearProveedor<AnthropicDocumentAIProvider>(), documento);

    [TheorySiHayClaveIa(GeminiOptions.SeccionConfiguracion)]
    [MemberData(nameof(CorpusRegresionIa.ComoTheoryData), MemberType = typeof(CorpusRegresionIa))]
    public async Task Gemini_extrae_los_campos_esperados_del_corpus(DocumentoCorpusIa documento) =>
        await EjecutarYVerificarAsync(CrearProveedor<GeminiDocumentAIProvider>(), documento);

    [TheorySiHayClaveIa(MistralOcrOptions.SeccionConfiguracion)]
    [MemberData(nameof(CorpusRegresionIa.ComoTheoryData), MemberType = typeof(CorpusRegresionIa))]
    public async Task MistralChat_extrae_los_campos_esperados_del_corpus(DocumentoCorpusIa documento) =>
        await EjecutarYVerificarAsync(CrearProveedor<MistralOcrDocumentAIProvider>(), documento);

    /// <summary>
    /// Contenedor de DI mínimo, no la app entera: lee las mismas secciones
    /// de configuración que <c>InfrastructureServiceCollectionExtensions</c>
    /// registra en producción, mediante variables de entorno (sin
    /// appsettings.Development.json ni user-secrets para estas opciones en
    /// este repo — ver MACRO_PLAN § 6.6). Deliberadamente no se hace
    /// <c>Dispose</c> del ServiceProvider: haría inservible el HttpClient
    /// resuelto antes de que termine la llamada async que lo usa.
    /// </summary>
    private static TProvider CrearProveedor<TProvider>() where TProvider : class, IDocumentAIProvider
    {
        var configuracion = new ConfigurationBuilder().AddEnvironmentVariables().Build();

        var servicios = new ServiceCollection();
        servicios.AddSingleton<IConfiguration>(configuracion);
        servicios.AddLogging(builder => builder.AddProvider(NullLoggerProvider.Instance));
        servicios.Configure<AnthropicOptions>(configuracion.GetSection(AnthropicOptions.SeccionConfiguracion));
        servicios.Configure<GeminiOptions>(configuracion.GetSection(GeminiOptions.SeccionConfiguracion));
        servicios.Configure<MistralOcrOptions>(configuracion.GetSection(MistralOcrOptions.SeccionConfiguracion));
        servicios.AddHttpClient<TProvider>();

        return servicios.BuildServiceProvider().GetRequiredService<TProvider>();
    }

    private static async Task EjecutarYVerificarAsync(IDocumentAIProvider proveedor, DocumentoCorpusIa documento)
    {
        var resultado = await proveedor.ExtraerEstructuradoAsync(documento.Texto, documento.TipoEsperado);

        resultado.EsExitoso.Should().BeTrue(
            resultado.EsFallido
                ? $"la extracción de \"{documento.Nombre}\" falló: {resultado.Error.Codigo} — {resultado.Error.Mensaje}"
                : string.Empty);

        var extraido = resultado.Valor;

        // Umbral deliberadamente bajo (ver DocumentoCorpusIa.ConfianzaMinimaEsperada):
        // el objetivo no es afinar el modelo, es detectar una caída brusca de
        // confianza que delate un prompt roto — no penalizar variación normal.
        extraido.ConfianzaGeneral.Should().BeGreaterThanOrEqualTo(
            documento.ConfianzaMinimaEsperada,
            $"confianza sospechosamente baja en \"{documento.Nombre}\" — posible regresión de prompt/modelo");

        foreach (var (campo, esperado) in documento.CamposExactos)
        {
            extraido.Campos.GetValueOrDefault(campo).Should().Be(
                esperado, $"el campo \"{campo}\" de \"{documento.Nombre}\" es estructurado y sin ambigüedad de redacción");
        }

        foreach (var (campo, fragmentoEsperado) in documento.CamposQueDebenContener)
        {
            var valor = extraido.Campos.GetValueOrDefault(campo);
            valor.Should().NotBeNullOrWhiteSpace($"el campo \"{campo}\" de \"{documento.Nombre}\" debía extraerse");
            valor!.Should().ContainEquivalentOf(
                fragmentoEsperado, $"el campo \"{campo}\" de \"{documento.Nombre}\" debía mencionar \"{fragmentoEsperado}\"");
        }
    }
}
