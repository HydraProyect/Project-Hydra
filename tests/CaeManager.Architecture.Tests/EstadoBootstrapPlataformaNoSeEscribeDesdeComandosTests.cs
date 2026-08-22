using System.Reflection;
using System.Text.RegularExpressions;
using CaeManager.Domain.Plataforma;
using FluentAssertions;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// <b>El estado de bootstrap no lo toca ningún comando.</b> Lo designa el
/// arranque de la aplicación y lo consume el acto fundacional; nada más.
///
/// <para>
/// Importa porque esa fila es la que decide quién puede acuñar la autoridad
/// fundacional de la plataforma. Un comando que pudiera reescribirla —o
/// borrarla y volver a insertarla— reabriría el bootstrap consumido, que es
/// justamente lo que A2 cierra: la recuperación de la autoridad perdida es un
/// procedimiento administrativo externo, no una propiedad del dominio.
/// </para>
/// </summary>
public class EstadoBootstrapPlataformaNoSeEscribeDesdeComandosTests
{
    /// <summary>
    /// Escrituras sobre el DbSet, por su forma y no por su nombre: se persigue
    /// cualquier <c>Add</c>/<c>Update</c>/<c>Remove</c> —y sus variantes de
    /// rango— sobre <c>EstadoBootstrapPlataforma</c>, esté escrito como esté.
    /// </summary>
    private static readonly Regex EscrituraSobreElEstado = new(
        @"EstadoBootstrapPlataforma\s*\.\s*(Add|AddRange|Update|UpdateRange|Remove|RemoveRange|ExecuteDelete|ExecuteUpdate)",
        RegexOptions.Compiled);

    [Fact]
    public void Ningun_codigo_de_Application_escribe_el_estado_de_bootstrap()
    {
        var raiz = RaizDelRepositorio();
        var application = Path.Combine(raiz, "src", "CaeManager.Application");

        var infractores = Directory
            .EnumerateFiles(application, "*.cs", SearchOption.AllDirectories)
            .Where(a => !a.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !a.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Where(a => EscrituraSobreElEstado.IsMatch(File.ReadAllText(a)))
            .Select(a => Path.GetRelativePath(raiz, a).Replace(Path.DirectorySeparatorChar, '/'))
            .OrderBy(r => r)
            .ToList();

        infractores.Should().BeEmpty(
            "la fila de bootstrap la designa el arranque y la consume el acto fundacional llamando a " +
            "Consumir() sobre el agregado; un comando que la escribiera directamente podría reabrir un " +
            "bootstrap ya consumido");
    }

    /// <summary>
    /// La otra mitad, y la que cubre a <b>cualquier</b> llamante y no solo a
    /// Application: el agregado no ofrece ninguna forma de cambiar la identidad
    /// raíz. No hay setter público, y el único método que muta algo es
    /// <c>Consumir</c>, que no la toca.
    ///
    /// <para>
    /// Esto es lo que hace que "la configuración designa pero no gobierna" sea
    /// una propiedad estructural y no una disciplina del seeder: aunque alguien
    /// cambiara <c>AdministradorInicial:Email</c>, no existe código capaz de
    /// reasignar la raíz.
    /// </para>
    /// </summary>
    [Fact]
    public void La_identidad_raiz_no_se_puede_reasignar_desde_ningun_sitio()
    {
        var tipo = typeof(EstadoBootstrapPlataforma);

        tipo.GetProperty(nameof(EstadoBootstrapPlataforma.UsuarioRaizId))!
            .SetMethod!.IsPublic.Should().BeFalse("la raíz se designa una vez y no se reasigna");

        var mutadores = tipo
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName)
            .Select(m => m.Name)
            .OrderBy(n => n)
            .ToList();

        mutadores.Should().BeEquivalentTo(["Consumir", "PuedeArrancar"],
            "cualquier método nuevo que mute el agregado tiene que pasar por aquí y por su revisión: " +
            "es el sitio donde se decidiría, sin querer, que la raíz puede cambiar de manos");
    }

    private static string RaizDelRepositorio()
    {
        var actual = new DirectoryInfo(AppContext.BaseDirectory);

        while (actual is not null && !File.Exists(Path.Combine(actual.FullName, "CaeManager.slnx")))
            actual = actual.Parent;

        if (actual is null)
            throw new InvalidOperationException(
                "No se encontró CaeManager.slnx subiendo desde " + AppContext.BaseDirectory);

        return actual.FullName;
    }
}
