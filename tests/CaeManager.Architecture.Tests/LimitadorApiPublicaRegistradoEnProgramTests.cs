using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// S-A-6: el cupo de la API pública no puede salir de <c>HttpContext.User</c>. El limitador corre
/// antes de <c>UseAuthorization</c> y el esquema por defecto es la cookie, de modo que el principal
/// de una clave API nunca existe a esa altura y todo el tráfico con clave compartía un único cupo.
/// El comportamiento del reparto lo prueba <c>LimitadorApiPublicaTests</c> en Web.Tests; este
/// trinquete vigila el cableado de <c>Program.cs</c> (el proyecto no tiene un
/// <c>WebApplicationFactory</c> que lo observe), sobre el código sin comentarios.
/// </summary>
public class LimitadorApiPublicaRegistradoEnProgramTests
{
    [Fact]
    public void El_limitador_global_aplica_el_cupo_y_el_rechazo_de_la_extension()
    {
        var codigo = ProgramSinComentarios();
        // Anclada al argumento limiteExtension y antes del GetNoLimiter: si el bloque bajara, un GET a /extension quedaria sin cupo.
        Regex.IsMatch(codigo, @"LimitadorExtension\.ParticionGlobal\(\s*contexto\s*,\s*limiteExtension\s*,[^;]*?is\s*\{\s*\}\s+(\w+)\)\s*return\s+\1\s*;[\s\S]*?GetNoLimiter\(\s*""sin-limite""\s*\)")
            .Should().BeTrue();
        Regex.IsMatch(codigo, @"var\s+limiteExtension\s*=\s*builder\.Configuration\.GetValue\(\s*""RateLimiting:Extension:LimiteGlobalPorMinuto""\s*,\s*300\s*\)")
            .Should().BeTrue();
        codigo.Should().Contain("opciones.OnRejected = LimitadorExtension.RechazarAsync;");
    }

    [Fact]
    public void La_politica_ApiPublica_reparte_el_cupo_con_LimitadorApiPublica_y_no_con_el_principal()
    {
        var codigo = ProgramSinComentarios();

        var politica = Regex.Match(codigo,
            @"AddPolicy\(LimitadorApiPublica\.Politica,.*?\);", RegexOptions.Singleline);

        politica.Success.Should().BeTrue("la política ApiPublica debe registrarse con la constante de LimitadorApiPublica");
        politica.Value.Should().Contain("LimitadorApiPublica.ParticionPorLlamante(")
            .And.NotContain(".User", "particionar por HttpContext.User agrupa todo el tráfico con clave en un único cupo")
            .And.NotContain("sin-tenant");
    }

    [Fact]
    public void El_limitador_global_devuelve_el_techo_por_IP_de_la_API()
    {
        var codigo = ProgramSinComentarios();

        Regex.IsMatch(codigo,
                @"LimitadorApiPublica\.ParticionGlobal\([^)]*\)\s+is\s*\{\s*\}\s+(\w+)\)\s*return\s+\1\s*;")
            .Should().BeTrue("sin devolver el techo por IP, rotar claves inventadas daría un cupo nuevo en cada petición");
    }

    [Fact]
    public void El_grupo_de_la_API_v1_aplica_la_politica_de_limitacion()
    {
        var codigo = ProgramSinComentarios();

        Regex.IsMatch(codigo, @"MapGroup\(""/api/v1""\)[^;]*\.RequireRateLimiting\((""ApiPublica""|LimitadorApiPublica\.Politica)\)")
            .Should().BeTrue("sin RequireRateLimiting el grupo /api/v1 solo tendría el techo por IP");
    }

    [Fact]
    public void Los_recursos_de_la_API_v1_pasan_por_el_filtro_de_alcance_y_el_canje_del_token_tiene_cupo_propio()
    {
        var codigo = ProgramSinComentarios();

        Regex.IsMatch(codigo, @"MapGroup\(""/api/v1""\)[^;]*\.AddEndpointFilter<AlcanceIntegracionEndpointFilter>\(\)\s*\.AddEndpointFilter<RastroUsoApiEndpointFilter>\(\)\s*;\s*apiV1\.MapRecursosApiV1\(\)")
            .Should().BeTrue("sin el filtro en el grupo, el metadato de alcance de cada ruta no lo mira nadie, y el rastro va detrás: lo denegado no ha leído nada");
        Regex.Matches(codigo, @"apiV1\.Map\w+").Should().ContainSingle("un recurso mapeado fuera de MapRecursosApiV1 no lo recorre RutasApiV1DeclaranAlcanceTests");
        Regex.IsMatch(codigo, @"AddPolicy\(LimitadorApiPublica\.PoliticaToken,\s*httpContext\s*=>\s*LimitadorApiPublica\.ParticionToken\(")
            .Should().BeTrue();
        Regex.IsMatch(codigo, @"\.AddAuthenticationSchemes\(\s*ApiKeyAuthenticationSchemeOptions\.NombreEsquema,\s*ClaveIntegracionAuthenticationSchemeOptions\.NombreEsquema\)\s*\.RequireAuthenticatedUser\(\)")
            .Should().BeTrue("la política ApiPublica fija sus dos esquemas: sin ellos aceptaría la cookie de Identity");

        var token = File.ReadAllText(Path.Combine(RaizDelRepositorio(), "src", "CaeManager.Web", "Api", "V1", "TokenIntegracionEndpoints.cs"));
        token.Should().Contain("RequireRateLimiting(LimitadorApiPublica.PoliticaToken)");
    }

    [Fact]
    public void Ninguna_ruta_de_la_API_v1_se_mapea_fuera_del_grupo()
    {
        var web = Path.Combine(RaizDelRepositorio(), "src", "CaeManager.Web");
        var mapeos = Directory.EnumerateFiles(web, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .SelectMany(f => Regex.Matches(File.ReadAllText(f), @"\.Map\w*\(\s*\$?@?""/?api(?:/v1[^""]*)?""").Select(m => Regex.Replace(m.Value, @"\s+", "")))
            .ToList();

        mapeos.Should().BeEquivalentTo(
            [@".MapGroup(""/api/v1""", @".MapPost(""/api/v1/token""", @".MapOpenApi(""/api/v1/openapi.json"""],
            "una ruta con su propio mapeo no la recorre RutasApiV1DeclaranAlcanceTests ni pasa por el filtro de alcance");
    }

    [Fact]
    public void El_documento_OpenAPI_exige_clave_salvo_en_el_entorno_del_escaneo()
    {
        Regex.IsMatch(ProgramSinComentarios(), @"if\s*\(app\.Environment\.IsEnvironment\(""Escaneo""\)\)\s*documentoOpenApi\.AllowAnonymous\(\);\s*else\s*documentoOpenApi\.RequireAuthorization\(""ApiPublica""\);")
            .Should().BeTrue("sin anunciar la API, su descripción no se sirve a un anónimo (OWASP API9:2023)");
    }

    private static string ProgramSinComentarios()
    {
        var program = File.ReadAllText(Path.Combine(RaizDelRepositorio(), "src", "CaeManager.Web", "Program.cs"));
        return string.Join('\n', program.Split('\n').Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));
    }

    private static string RaizDelRepositorio()
    {
        var actual = new DirectoryInfo(AppContext.BaseDirectory);

        while (actual is not null && !File.Exists(Path.Combine(actual.FullName, "CaeManager.slnx")))
            actual = actual.Parent;

        return actual?.FullName ?? throw new InvalidOperationException(
            "No se encontró CaeManager.slnx subiendo desde " + AppContext.BaseDirectory +
            " — este test necesita el árbol fuente del repositorio.");
    }
}
