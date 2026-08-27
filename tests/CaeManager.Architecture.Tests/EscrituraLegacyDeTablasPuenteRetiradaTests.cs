using System.Text.RegularExpressions;
using FluentAssertions;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// F4.2c (R6 aceptada 2026-08-27): <c>RelacionEmpresarial</c> es la única
/// fuente de escritura de los vínculos empresariales — las tres tablas
/// puente legacy (<c>EmpresasClientes</c>, <c>SubcontratasClientes</c>,
/// <c>SubcontratasEmpresas</c>) quedan congeladas hasta su <c>DROP</c> en el
/// cierre de F4. <c>IEmpresaClienteRepository</c>/<c>ISubcontrataClienteRepository</c>/
/// <c>ISubcontrataEmpresaRepository</c> y sus implementaciones se retiraron
/// en el mismo commit que este ratchet.
///
/// <para>
/// <b>Por qué la regex tiene DOS ramas y no una</b> — la lección que evita
/// copiar a ciegas el molde de <see cref="EscrituraLegacyDeSubcontrataRetiradaTests"/>:
/// aquel funciona porque su repositorio se retiró a la vez, pero los tres
/// repositorios puente escribían vía <c>dbContext.Set&lt;EmpresaCliente&gt;().Add(…)</c>,
/// no vía la propiedad DbSet. Un ratchet de una sola rama daría verde con
/// los tres repositorios intactos y escribiendo — el falso negativo perfecto,
/// del tipo que la auditoría de ratchets encontró en 13 de 14. Cada rama se
/// falsó por su propia mutación, por separado.
/// </para>
///
/// <para>
/// Fuera del alcance declarado: SQL crudo (cubierto en parte por
/// <c>ProhibicionSqlCrudoYFiltrosIgnoradosTests</c>), escritura por
/// navegación EF (hoy no existe: las configuraciones usan
/// <c>HasOne&lt;T&gt;().WithMany()</c> sin propiedad de navegación) y los
/// tests — que siguen pudiendo sembrar legacy a propósito, como necesita
/// <c>AgregarRelacionEmpresarialMigrationTests</c> para probar el backfill.
/// </para>
/// </summary>
public class EscrituraLegacyDeTablasPuenteRetiradaTests
{
    /// <summary>
    /// Rama 1: escritura vía la propiedad DbSet (<c>EmpresasClientes.Add(…)</c>).
    /// Rama 2: escritura vía <c>Set&lt;Entidad&gt;()</c> — la forma que usaban
    /// los tres repositorios retirados y que una regex de propiedad no ve.
    /// El prefijo de namespace es opcional a propósito: la primera versión de
    /// esta regex solo reconocía el nombre pelado, y la propia mutación de
    /// falsación (que usaba <c>Set&lt;CaeManager.Domain.…&gt;()</c> calificado)
    /// pasó en verde con el infractor dentro — el defecto exacto que la
    /// prueba de sensibilidad existe para cazar.
    /// </summary>
    private static readonly Regex EscrituraSobreTablasPuente = new(
        @"\b(EmpresasClientes|SubcontratasClientes|SubcontratasEmpresas)\s*\.\s*(Add|AddRange|Update|UpdateRange|Remove|RemoveRange|ExecuteDelete|ExecuteUpdate)\s*\(" +
        @"|\bSet\s*<\s*(?:[A-Za-z_][\w]*\s*\.\s*)*(EmpresaCliente|SubcontrataCliente|SubcontrataEmpresa)\s*>\s*\(\s*\)\s*\.\s*(Add|AddRange|Update|UpdateRange|Remove|RemoveRange|ExecuteDelete|ExecuteUpdate)\s*\(",
        RegexOptions.Compiled);

    [Theory]
    [InlineData("CaeManager.Application")]
    [InlineData("CaeManager.Infrastructure")]
    [InlineData("CaeManager.Web")]
    public void Ningun_codigo_de_produccion_escribe_en_las_tablas_puente_legacy(string proyecto)
    {
        var raiz = RaizDelRepositorio();
        var directorio = Path.Combine(raiz, "src", proyecto);

        var infractores = Directory
            .EnumerateFiles(directorio, "*.cs", SearchOption.AllDirectories)
            .Where(a => !a.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !a.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Where(a => EscrituraSobreTablasPuente.IsMatch(File.ReadAllText(a)))
            .Select(a => Path.GetRelativePath(raiz, a).Replace(Path.DirectorySeparatorChar, '/'))
            .OrderBy(r => r)
            .ToList();

        infractores.Should().BeEmpty(
            "desde F4.2c la arista RelacionEmpresarial es la única fuente de escritura de los vínculos " +
            "empresariales — un escritor nuevo sobre una tabla puente divergiría en silencio de todos los " +
            "lectores, que ya solo leen la arista, y su dato jamás llegaría a ninguna pantalla");
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
