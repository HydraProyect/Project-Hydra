using FluentAssertions;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// Los dos middlewares que corrigen el rol del principal están registrados, y
/// están <b>entre</b> <c>UseAuthentication</c> y <c>UseAuthorization</c>.
///
/// <para>
/// <b>Por qué hace falta este test.</b> Sus tests unitarios los ejercitan en
/// aislamiento: construyen el middleware, le pasan un <c>HttpContext</c> y
/// comprueban el principal resultante. Todos seguirían en verde si alguien
/// borrara la línea de <c>Program.cs</c> o la moviera detrás de
/// <c>UseAuthorization</c> — y en los dos casos vuelve la escalada de
/// privilegios entre tenants, porque las puertas <c>[Authorize(Roles = …)]</c>
/// ya habrían contestado con el rol del tenant equivocado. Es el hueco clásico
/// entre lo que un test dice medir y lo que de verdad observa: la corrección
/// del middleware no es la corrección del sistema si el sistema no lo llama.
/// </para>
///
/// <para>
/// <b>El orden no es preferencia, es la propiedad.</b> Después de
/// <c>UseAuthentication</c> porque antes no existe principal que corregir;
/// antes de <c>UseAuthorization</c> porque después ya no sirve de nada.
/// </para>
/// </summary>
public class OrdenDeLosMiddlewaresDeRolTests
{
    private const string ArchivoDelPipeline = "src/CaeManager.Web/Program.cs";

    [Theory]
    [InlineData("UseSesionPrivilegiadaSinRolDeNegocio",
        "una sesión privilegiada de plataforma conservaría el rol de su tenant de origen dentro del tenant visitado")]
    [InlineData("UseRolEfectivoDelWorkspace",
        "un workspace delegado conservaría el rol del tenant de origen: un Administrador en A delegado como " +
        "Consulta en B superaría las puertas de Administrador de B")]
    public void El_middleware_esta_registrado_entre_autenticacion_y_autorizacion(string middleware, string porque)
    {
        var lineas = LineasDeCodigo();

        var posicion = PosicionDe(lineas, middleware);
        var autenticacion = PosicionDe(lineas, "UseAuthentication");
        var autorizacion = PosicionDe(lineas, "UseAuthorization");

        posicion.Should().BeGreaterThan(0, $"sin esta registración, {porque}");

        posicion.Should().BeGreaterThan(autenticacion,
            "antes de UseAuthentication no hay principal autenticado que corregir");

        posicion.Should().BeLessThan(autorizacion,
            "después de UseAuthorization las puertas de rol ya han contestado, así que corregir el principal " +
            "llega tarde y no cambia ninguna decisión");
    }

    [Fact]
    public void El_plano_3_se_evalua_antes_que_el_plano_2()
    {
        // El middleware del plano 2 se abstiene cuando el token nombra una
        // sesión privilegiada, dando por hecho que el del plano 3 ya le quitó
        // el rol. Invertirlos dejaría esa suposición sin cumplir.
        var lineas = LineasDeCodigo();

        PosicionDe(lineas, "UseRolEfectivoDelWorkspace")
            .Should().BeGreaterThan(PosicionDe(lineas, "UseSesionPrivilegiadaSinRolDeNegocio"));
    }

    /// <summary>
    /// Guarda del propio ratchet: si el buscador dejara de encontrar los
    /// anclajes del pipeline, todas las comparaciones de orden se harían sobre
    /// ceros y el test pasaría por no mirar.
    /// </summary>
    [Fact]
    public void Los_anclajes_del_pipeline_siguen_existiendo()
    {
        var lineas = LineasDeCodigo();

        PosicionDe(lineas, "UseAuthentication").Should().BeGreaterThan(0);
        PosicionDe(lineas, "UseAuthorization").Should().BeGreaterThan(0);
    }

    /// <summary>
    /// Las líneas que son íntegramente comentario se descartan. No es
    /// cosmética: sin esto, <b>comentar</b> la registración dejaría el texto en
    /// su sitio y el ratchet seguiría en verde — el mismo fallo que
    /// <see cref="RegistroDelResolutorDeSesionPrivilegiadaTests"/> demostró por
    /// mutación, y que aquí importa el doble porque estos dos middlewares se
    /// explican largamente en comentarios que citan su propio nombre.
    /// </summary>
    private static List<string> LineasDeCodigo() =>
        [.. File.ReadAllLines(Path.Combine(
                RaizDelRepositorio(), ArchivoDelPipeline.Replace('/', Path.DirectorySeparatorChar)))
            .Select(l => l.TrimStart().StartsWith("//", StringComparison.Ordinal) ? string.Empty : l)];

    /// <summary>Número de línea (1-based) de la llamada, o 0 si no aparece.</summary>
    private static int PosicionDe(List<string> lineas, string nombre)
    {
        var indice = lineas.FindIndex(l => l.Contains($"{nombre}(", StringComparison.Ordinal));
        return indice + 1;
    }

    private static string RaizDelRepositorio()
    {
        var actual = new DirectoryInfo(AppContext.BaseDirectory);

        while (actual is not null && !File.Exists(Path.Combine(actual.FullName, "CaeManager.slnx")))
            actual = actual.Parent;

        if (actual is null)
            throw new InvalidOperationException(
                "No se encontró CaeManager.slnx subiendo desde " + AppContext.BaseDirectory +
                " — este test necesita el árbol fuente del repositorio, no solo los ensamblados compilados.");

        return actual.FullName;
    }
}
