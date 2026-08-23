using FluentAssertions;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// <c>ISesionPrivilegiadaActual</c> tiene dos implementaciones y la diferencia
/// entre ellas es la seguridad entera del plano 3:
/// <list type="bullet">
/// <item><c>SesionPrivilegiadaAusente</c> (Application) siempre devuelve
/// <c>null</c>. Existe solo para que los fixtures mínimos de MediatR puedan
/// construir el behavior de escritura, y se registra con <c>TryAdd</c>.</item>
/// <item><c>SesionPrivilegiadaActual</c> (Infrastructure) es la que revalida
/// contra la base — la única que puede decir que <b>sí</b> hay sesión.</item>
/// </list>
///
/// Si alguien borrara la registración de Infrastructure, la aplicación seguiría
/// arrancando y todos los tests seguirían en verde: el <c>TryAdd</c> taparía el
/// hueco con el valor inerte. No se concederían privilegios de más —el inerte
/// dice que no a todo— pero la comprobación quedaría desactivada en silencio, y
/// una comprobación de seguridad desactivada en silencio es exactamente lo que
/// no puede pasar. En cuanto exista el camino que abre sesiones, ese silencio
/// pasaría de inofensivo a peligroso.
///
/// No hay forma barata de comprobarlo resolviendo el contenedor real —
/// <c>AddInfrastructure</c> exige configuración y entorno completos, y ningún
/// test monta hoy el host de verdad— así que se vigila el texto de la
/// registración, mismo mecanismo de ratchet que
/// <see cref="AccesoRestringidoACatalogosDeAsignacionTests"/>.
/// </summary>
public class RegistroDelResolutorDeSesionPrivilegiadaTests
{
    private const string ArchivoDeRegistro =
        "src/CaeManager.Infrastructure/DependencyInjection/InfrastructureServiceCollectionExtensions.cs";

    [Fact]
    public void Infrastructure_registra_el_resolutor_que_revalida_contra_la_base()
    {
        var texto = SinComentarios(File.ReadAllText(Path.Combine(
            RaizDelRepositorio(), ArchivoDeRegistro.Replace('/', Path.DirectorySeparatorChar))));

        texto.Should().Contain("ISesionPrivilegiadaActual",
            "sin esta registración el TryAdd de AddApplication deja en pie el valor inerte, que dice que no " +
            "hay sesión privilegiada nunca — la comprobación quedaría desactivada sin que nada fallara");

        texto.Should().Contain("Plataforma.SesionPrivilegiadaActual",
            "la implementación registrada tiene que ser la de Infrastructure, la que consulta la sesión, su " +
            "concesión y el alcance; cualquier otra convierte el plano 3 en decoración");
    }

    /// <summary>
    /// <b>Se descartan las líneas que son íntegramente comentario</b>, y no es
    /// cosmética: sin esto, <b>comentar</b> la registración dejaba las dos cadenas
    /// en su sitio y el ratchet seguía en verde. Demostrado por mutación el
    /// 2026-08-23 — compilaba, arrancaba, y desactivaba en silencio exactamente la
    /// comprobación que este test existe para vigilar.
    ///
    /// <para>
    /// Un comentario al final de una línea con código sí cuenta: para una
    /// comprobación de presencia, equivocarse hacia detectar de más obliga a
    /// mirar, que es el lado seguro.
    /// </para>
    /// </summary>
    private static string SinComentarios(string texto) =>
        string.Join('\n', texto
            .Split('\n')
            .Where(linea => !linea.TrimStart().StartsWith("//", StringComparison.Ordinal)));

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
