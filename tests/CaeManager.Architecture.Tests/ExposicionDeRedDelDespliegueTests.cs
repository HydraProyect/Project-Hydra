using FluentAssertions;
using Xunit;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// Higiene de exposición de red (auditoría IT, S-D-6 y S-D-10): el Postgres de
/// desarrollo con credenciales públicas solo escucha en loopback, y la app
/// desplegada filtra el Host en vez de aceptar cualquiera (<c>AllowedHosts: "*"</c>
/// queda solo como valor de desarrollo en appsettings.json).
/// </summary>
public class ExposicionDeRedDelDespliegueTests
{
    [Fact]
    public void El_postgres_de_desarrollo_solo_publica_su_puerto_en_loopback()
    {
        var puertos = File.ReadAllLines(Path.Combine(RaizDelRepositorio(), "docker-compose.yml"))
            .Select(l => l.Trim())
            .Where(l => !l.StartsWith('#') && l.StartsWith("- \"", StringComparison.Ordinal) && l.Contains(':'))
            .ToList();

        puertos.Should().NotBeEmpty("el compose de desarrollo publica el puerto de Postgres");
        puertos.Should().OnlyContain(l => l.StartsWith("- \"127.0.0.1:", StringComparison.Ordinal),
            "postgres/postgres es una credencial pública: publicar en todas las interfaces expone la base de " +
            "desarrollo a la red local del equipo del desarrollador.");
    }

    [Theory]
    [InlineData("docker-compose.produccion.yml")]
    [InlineData("docker-compose.staging.yml")]
    public void La_app_desplegada_filtra_el_host_y_nunca_acepta_cualquiera(string fichero)
    {
        var lineasActivas = File.ReadAllLines(Path.Combine(RaizDelRepositorio(), "deploy", "local", fichero))
            .Select(l => l.Trim())
            .Where(l => !l.StartsWith('#'))
            .Where(l => l.StartsWith("AllowedHosts:", StringComparison.Ordinal))
            .ToList();

        lineasActivas.Should().ContainSingle(
            "el servicio app-azul (del que hereda app-verde) fija AllowedHosts por entorno");
        lineasActivas[0].Should().NotContain("*", "un comodín deja la app aceptando cualquier cabecera Host");
        lineasActivas[0].Should().Contain("localhost",
            "el healthcheck del contenedor y las sondas por docker exec llaman a http://localhost:8080/salud");
        lineasActivas[0].Should().Contain(fichero.Contains("produccion", StringComparison.Ordinal) ? "${DOMINIO}" : "staging.talveg.es",
            "sin el dominio público en AllowedHosts la app rechazaría con 400 todo el tráfico que llega por Caddy");
    }

    /// <summary>S-C-3: el cliente de WhatsApp Cloud API se registra con el manejador sin redirecciones.</summary>
    [Fact]
    public void El_cliente_http_de_whatsapp_se_registra_sin_seguir_redirecciones()
    {
        var registro = File.ReadAllText(Path.Combine(RaizDelRepositorio(), "src", "CaeManager.Infrastructure",
            "DependencyInjection", "InfrastructureServiceCollectionExtensions.cs"));

        registro.Should().Contain("ConfigurePrimaryHttpMessageHandler(WhatsAppCloudApiClient.CrearManejadorSinRedirecciones)",
            "el HttpClient de WhatsApp lleva el Bearer de la línea; no debe seguir un 3xx a un destino sin validar.");
    }

    /// <summary>
    /// El cliente de CTAIMACAE.net lleva la clave de suscripción en una cabecera propia, que .NET reenvía al
    /// seguir un 3xx (solo limpia <c>Authorization</c>), y el alta no es idempotente del otro lado: sin
    /// redirecciones, sin el pipeline de reintentos y con el valor de todas las cabeceras tachado en el log.
    /// </summary>
    [Fact]
    public void El_cliente_http_de_ctaima_no_sigue_redirecciones_no_reintenta_y_tacha_sus_cabeceras() =>
        ComprobarRegistroDelCliente("CtaimaCaeClient", "Ctaima", ["ConfigurePrimaryHttpMessageHandler", "RedactLoggedHeaders"]);

    /// <summary>
    /// El cliente de dokify lleva el token en <c>Authorization</c> y el CIF o NIF por el que pregunta en la ruta: además del
    /// cerco del de CTAIMACAE.net, su registro apaga el log del HttpClient, que escribiría la ruta.
    /// </summary>
    [Fact]
    public void El_cliente_http_de_dokify_no_sigue_redirecciones_no_reintenta_tacha_sus_cabeceras_y_no_registra_sus_peticiones() =>
        ComprobarRegistroDelCliente("DokifyClient", "Dokify", ["ConfigurePrimaryHttpMessageHandler", "RedactLoggedHeaders", "RemoveAllLoggers"]);

    private static void ComprobarRegistroDelCliente(string cliente, string carpeta, string[] encadenados)
    {
        var tipo = $"CaeManager.Infrastructure.Integraciones.ConectoresPlataformaCae.{carpeta}.{cliente}";
        var contenido = File.ReadAllText(Path.Combine(RaizDelRepositorio(), "src", "CaeManager.Infrastructure",
            "DependencyInjection", "InfrastructureServiceCollectionExtensions.cs"));

        var inicio = contenido.IndexOf($"AddHttpClient<{tipo}>", StringComparison.Ordinal);
        inicio.Should().BeGreaterThan(0, $"{cliente} se registra con AddHttpClient en este fichero");
        var fin = contenido.IndexOf("AddTransient<CaeManager.Application.Integraciones.ConectoresPlataformaCae.IConectorPlataformaCae", inicio, StringComparison.Ordinal);
        fin.Should().BeGreaterThan(inicio, "el conector se registra justo después de su cliente");
        var registro = contenido[inicio..fin];

        registro.Should().Contain($"ConfigurePrimaryHttpMessageHandler({tipo}.CrearManejadorSinRedirecciones)");
        // Tachado total con predicado: RedactLoggedHeaders(lista) sustituye el predicado por defecto de .NET 9+
        // (que tacha todo) y dejaría en claro Set-Cookie, Location u Ocp-Apim-Trace-Location.
        registro.Should().Contain(".RedactLoggedHeaders(_ => true)",
            "el log del HttpClient de CTAIMACAE.net tacha el valor de todas las cabeceras, no solo la clave y el tenantId");
        registro.Split("RedactLoggedHeaders(").Length.Should().Be(2,
            "una segunda llamada a RedactLoggedHeaders, con lista o predicado, sustituiría el tachado total");
        // Ni el pipeline propio (AplicarResilienciaHttp), ni los de Microsoft.Extensions.Http.Resilience (estándar, de
        // cobertura o a medida), ni un DelegatingHandler añadido, que puede reintentar o reescribir cabeceras.
        foreach (var prohibido in new[]
                 {
                     "AplicarResilienciaHttp", "AddStandardResilienceHandler", "AddStandardHedgingHandler",
                     "AddResilienceHandler", "AddHttpMessageHandler", "ConfigureAdditionalHttpMessageHandlers",
                 })
            registro.Should().NotContain(prohibido, "un reintento tras un timeout puede dar de alta dos veces al mismo Trabajador");

        // La lista de arriba no ve un envoltorio con otro nombre que añada resiliencia, un segundo manejador primario que
        // gane al de sin redirecciones (ConfigurePrimaryHttpMessageHandler o UseSocketsHttpHandler) ni un AddLogger propio
        // que reciba las cabeceras sin tachar. Por eso la cadena del registro se fija entera: los métodos encadenados al
        // AddHttpClient (los que siguen a un salto de línea o a un paréntesis de cierre, con o sin «!» o «?») son
        // exactamente estos dos, una vez cada uno. Las llamadas de la lambda de configuración no encajan en la forma
        // (van tras un identificador); una línea de la lambda que empezara por punto sí encajaría y daría rojo, que es el
        // error del lado seguro. El instrumento es textual: el tramo no admite comentarios, que podrían esconder una
        // llamada entre «)» y «.» o cortar el tramo con un «AddTransient<…» falso, ni escapes \u en los identificadores.
        // Un análisis con Roslyn de la cadena de invocaciones queda como deuda declarada en la PR.
        registro.Should().NotContain("//").And.NotContain("/*").And.NotContain(@"\u",
            "un comentario o un escape unicode en el tramo burlaría la lista de abajo");
        System.Text.RegularExpressions.Regex.Matches(registro, @"(?m)(?:^\s*|\)\s*[!?]?)\s*\.\s*@?(\w+)\s*[<(]")
            .Select(m => m.Groups[1].Value)
            .Should().Equal(encadenados,
                $"{cliente} solo encadena lo que declara este test: su manejador sin redirecciones y el tachado total (y, en dokify, el log apagado)");
        // Y el builder no se guarda en una variable para seguir encadenándole métodos fuera del tramo: la línea empieza
        // por «services.» y lo anterior acaba una sentencia o un bloque, así que tampoco cabe un «var b =» en la línea
        // de encima.
        var inicioDeLinea = contenido.LastIndexOf('\n', inicio) + 1;
        contenido[inicioDeLinea..inicio].Trim().Should().Be("services.",
            "el registro empieza la sentencia; un «var b = services.AddHttpClient…» permitiría seguir configurándolo después");
        contenido[..inicioDeLinea].TrimEnd().Should().EndWith(";",
            "la sentencia anterior acaba antes del registro; si no, el AddHttpClient sería la continuación de una asignación");

        // En todo src, no solo en este fichero: un segundo AddHttpClient del cliente de CTAIMACAE.net, tipado o por el
        // nombre que .NET le da ("CtaimaCaeClient"), vuelve al mismo cliente con nombre, y su RedactLoggedHeaders o su
        // resiliencia se sumarían a las de este tramo sin estar en él. Un ConfigureHttpClientDefaults aplicaría
        // reintentos o un tachado parcial a todos los clientes, este incluido, y lo mismo sus equivalentes: configurar
        // HttpClientFactoryOptions (ConfigureAll o por nombre, que es lo que ConfigureHttpClientDefaults hace por dentro),
        // registrar un IHttpMessageHandlerBuilderFilter o activar AddExtendedHttpClientLogging, que registra las peticiones
        // de todos los clientes con su propio tachado. También sustituir las piezas de la factoría que montan el pipeline
        // (HttpMessageHandlerBuilder, IHttpMessageHandlerFactory, ITypedHttpClientFactory) o montar el cliente tipado
        // sobre otro cliente con nombre (AddTypedClient). Los globales se buscan además en los csproj y props de src,
        // donde un <Using Alias="…"> los renombraría para el código.
        var raizSrc = Path.Combine(RaizDelRepositorio(), "src");
        var fuentes = Directory.EnumerateFiles(raizSrc, "*.cs", SearchOption.AllDirectories)
            .Select(File.ReadAllText)
            .ToList();
        var fuentesYProyectos = fuentes
            .Concat(Directory.EnumerateFiles(raizSrc, "*.csproj", SearchOption.AllDirectories)
                .Concat(Directory.EnumerateFiles(raizSrc, "*.props", SearchOption.AllDirectories))
                .Select(File.ReadAllText))
            .ToList();
        var nombre = System.Text.RegularExpressions.Regex.Escape(cliente);
        fuentes.Sum(f => System.Text.RegularExpressions.Regex.Matches(f, @"AddHttpClient\s*(<[^>]*\b" + nombre + @"\b|\([^)]*\b" + nombre + @"\b)").Count)
            .Should().Be(1, $"{cliente} se configura en un único registro de todo src");
        foreach (var global in new[]
                 {
                     "ConfigureHttpClientDefaults", "HttpClientFactoryOptions", "IHttpMessageHandlerBuilderFilter",
                     "AddExtendedHttpClientLogging", "HttpMessageHandlerBuilder", "IHttpMessageHandlerFactory",
                     "ITypedHttpClientFactory", "AddTypedClient",
                 })
            fuentesYProyectos.Where(f => f.Contains(global, StringComparison.Ordinal))
                .Should().BeEmpty($"{global} alcanzaría a todos los HttpClient, también al de CTAIMACAE.net");
    }

    private static string RaizDelRepositorio()
    {
        var actual = new DirectoryInfo(AppContext.BaseDirectory);

        while (actual is not null && !File.Exists(Path.Combine(actual.FullName, "CaeManager.slnx")))
            actual = actual.Parent;

        return actual?.FullName ?? throw new InvalidOperationException(
            "No se encontró CaeManager.slnx subiendo desde " + AppContext.BaseDirectory);
    }
}
