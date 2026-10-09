using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// <b>En <c>src/</c> solo un fichero fabrica un contexto HTTP con una identidad
/// dentro: la autoverificación de la siembra del piloto Outbound.</b>
///
/// <para>
/// <c>CurrentUserService</c> y <c>TenantActual</c> recurren al
/// <c>IHttpContextAccessor</c> cuando no hay circuito de Blazor: quien pueda
/// publicar ahí un <c>DefaultHttpContext</c> con un <c>ClaimsPrincipal</c> suyo
/// actúa como ese usuario ante toda la capa de aplicación. La autoverificación
/// lo necesita para medir con las consultas de las pantallas (que exigen un
/// usuario) y lo acota: método privado, solo las cuentas sembradas del piloto,
/// comprobación previa en la base, retirada en un <c>finally</c>. Un segundo
/// sitio que hiciera lo mismo sería una vía de impersonación sin esas guardas.
/// </para>
///
/// <para>
/// Se vigilan las dos mitades del mecanismo por separado: construir el contexto
/// y asignarlo al accesor. Mira <c>*.cs</c> y <c>*.razor</c>, texto completo
/// (comentarios incluidos: nombrar el tipo en un comentario de otro fichero
/// también obliga a mirar este trinquete).
/// </para>
/// </summary>
public class IdentidadFabricadaSoloEnLaAutoverificacionDelPilotoTests
{
    private const string UnicoFichero = "src/CaeManager.Infrastructure/Persistence/Seed/PilotoOutboundAutoverificacion.cs";

    /// <summary>
    /// Toda asignación a algo que se llame exactamente <c>HttpContext</c>: la directa
    /// (<c>accesor.HttpContext = …</c>, también <c>??=</c>) y la del inicializador de objeto
    /// (<c>new HttpContextAccessor { HttpContext = … }</c>), que no lleva punto delante y por eso
    /// se le escapaba a la expresión anterior. No casa comparaciones (<c>==</c>), miembros con
    /// cuerpo de expresión (<c>=&gt;</c>) ni identificadores que solo terminan igual.
    /// </summary>
    private static readonly Regex AsignacionAlAccesor = new(@"(?<!\w)HttpContext\s*(?:\?\?)?=(?![=>])", RegexOptions.Compiled);

    [Fact]
    public void Solo_la_autoverificacion_del_piloto_construye_un_contexto_HTTP()
    {
        FicherosQueCasan(texto => texto.Contains("DefaultHttpContext", StringComparison.Ordinal))
            .Should().Equal([UnicoFichero],
                "un DefaultHttpContext construido en src/ fuera de la autoverificación del piloto es una identidad fabricada sin sus guardas");
    }

    [Fact]
    public void Solo_la_autoverificacion_del_piloto_asigna_el_contexto_HTTP_del_accesor()
    {
        FicherosQueCasan(texto => AsignacionAlAccesor.IsMatch(texto))
            .Should().Equal([UnicoFichero],
                "asignar IHttpContextAccessor.HttpContext publica una identidad para CurrentUserService y TenantActual");
    }

    [Fact]
    public void En_ese_fichero_el_contexto_se_construye_una_vez_y_se_retira_en_un_finally()
    {
        var texto = File.ReadAllText(Path.Combine(RaizDelRepositorio(), UnicoFichero.Replace('/', Path.DirectorySeparatorChar)));

        Regex.Matches(texto, @"new DefaultHttpContext\b").Count.Should().Be(1, "una sola construcción, en el método privado");
        AsignacionAlAccesor.Matches(texto).Count.Should().Be(2, "la publicación y su retirada, nada más");
        Regex.IsMatch(texto, @"finally\s*\{\s*accesor\.HttpContext = null;\s*\}").Should().BeTrue(
            "la identidad se retira pase lo que pase en la lectura");
        Regex.IsMatch(texto, @"private static async Task<T> ComoCuentaDelPilotoAsync<T>\(").Should().BeTrue(
            "el método que construye la identidad es privado: no hay entrada que la construya para otra cuenta");
    }

    /// <summary>Control positivo del detector: las formas de publicar un contexto en el accesor que tiene que ver.</summary>
    [Theory]
    [InlineData("accesor.HttpContext = contexto;")]
    [InlineData("accesor.HttpContext=contexto;")]
    [InlineData("accesor.HttpContext ??= contexto;")]
    [InlineData("var accesor = new HttpContextAccessor { HttpContext = contexto };")]
    [InlineData("var accesor = new HttpContextAccessor\n{\n    HttpContext = contexto\n};")]
    [InlineData("var otro = new Envoltorio { Nombre = \"x\", HttpContext = contexto };")]
    public void El_detector_casa_la_asignacion_directa_y_la_del_inicializador_de_objeto(string texto) =>
        AsignacionAlAccesor.IsMatch(texto).Should().BeTrue($"«{texto}» publica un contexto HTTP y el trinquete tiene que verlo");

    /// <summary>Control negativo: leer, comparar o declarar no es publicar; si casaran, el trinquete señalaría medio <c>src/</c>.</summary>
    [Theory]
    [InlineData("if (accesor.HttpContext == null) return;")]
    [InlineData("if (accesor.HttpContext != null) return;")]
    [InlineData("var usuario = accesor.HttpContext?.User;")]
    [InlineData("public HttpContext? HttpContext => accesor.HttpContext;")]
    [InlineData("[CascadingParameter] public HttpContext? HttpContext { get; set; } = default!;")]
    [InlineData("HttpContext contexto = otro;")]
    [InlineData("var contexto = new DefaultHttpContext { User = identidad };")]
    [InlineData("var miHttpContext = contexto;")]
    public void El_detector_no_casa_lecturas_comparaciones_ni_declaraciones(string texto) =>
        AsignacionAlAccesor.IsMatch(texto).Should().BeFalse($"«{texto}» no asigna el contexto HTTP de ningún accesor");

    /// <summary>Control positivo: el instrumento enumera <c>src/</c> y ve un patrón que existe en más de un fichero.</summary>
    [Fact]
    public void El_instrumento_ve_los_ficheros_de_src()
    {
        FicherosQueCasan(texto => texto.Contains("IHttpContextAccessor", StringComparison.Ordinal))
            .Should().HaveCountGreaterThan(3, "CurrentUserService, TenantActual y otros lo usan: si no los ve, no está mirando src/");
        FicherosQueCasan(texto => texto.Contains("@page", StringComparison.Ordinal))
            .Should().NotBeEmpty("también recorre los .razor");
    }

    private static List<string> FicherosQueCasan(Func<string, bool> casa)
    {
        var raiz = RaizDelRepositorio();
        return new[] { "*.cs", "*.razor" }
            .SelectMany(patron => Directory.EnumerateFiles(Path.Combine(raiz, "src"), patron, SearchOption.AllDirectories))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") &&
                        !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Where(f => casa(File.ReadAllText(f)))
            .Select(f => Path.GetRelativePath(raiz, f).Replace('\\', '/'))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();
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
