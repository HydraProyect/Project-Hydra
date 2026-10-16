using System.Text.RegularExpressions;
using CaeManager.Migrations.PostgreSQL.Migrations;
using FluentAssertions;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// Decisión de producto (2026-10-16): en Outbound un Centro nace vacío. Los campos de las cajas de
/// Acceso y Prevención de Centro 360 son opcionales, empiezan sin informar tanto en el alta como en la
/// migración de los Centros existentes, y ninguno genera requisitos documentales, bloquea accesos ni
/// entra en el cálculo de cumplimiento. La siembra tampoco los rellena.
/// Este test congela que solo los leen y escriben la entidad, su configuración EF, la auditoría, la
/// consulta y el comando de la ficha y la propia ficha de Centro 360. Si un motor de requisitos, de
/// cumplimiento, de acceso o un seeder empieza a usarlos, el cambio necesita una decisión de producto
/// nueva y este test debe actualizarse a la vez que el comentario que la describe.
/// </summary>
public class FichaCentroSinRequisitosDerivadosTests
{
    private static readonly string[] CamposDeLaFicha =
    [
        "HorarioAcceso", "ControlAccesosNombre", "ControlAccesosTelefono",
        "TecnicoPrevencionNombre", "TecnicoPrevencionTelefono", "TecnicoPrevencionCorreo",
        "TrabajosEnAltura", "EspaciosConfinados", "AtmosferasExplosivas",
    ];

    private static readonly Regex PatronCampo = new(@"\b(" + string.Join("|", CamposDeLaFicha) + @")\b", RegexOptions.Compiled);

    private static readonly HashSet<string> ArchivosAutorizados =
    [
        "src/CaeManager.Domain/Centros/Centro.cs",
        "src/CaeManager.Infrastructure/Persistence/Configurations/CentroConfiguration.cs",
        "src/CaeManager.Infrastructure/Auditing/AuditoriaInterceptor.cs",
        "src/CaeManager.Application/Centros/Queries/ObtenerCentroPorId/ObtenerCentroPorIdQuery.cs",
        "src/CaeManager.Application/Centros/Commands/EditarFichaCentro/EditarFichaCentroCommand.cs",
        "src/CaeManager.Web/Features/Centros/Pages/CentroDetalle.razor",
        "src/CaeManager.Web/Features/Centros/Pages/CentroDetalle.razor.cs",
        "src/CaeManager.Web/Features/Centros/Components/EditarFichaCentroDrawer.razor",
    ];

    [Fact]
    public void Ningun_campo_de_acceso_ni_de_prevencion_se_usa_fuera_de_la_ficha_de_Centro_360()
    {
        var raiz = RaizDelRepositorio();
        var src = Path.Combine(raiz, "src");
        var separador = Path.DirectorySeparatorChar;

        var usos = Directory.EnumerateFiles(src, "*.*", SearchOption.AllDirectories)
            .Where(a => a.EndsWith(".cs", StringComparison.Ordinal) || a.EndsWith(".razor", StringComparison.Ordinal))
            .Where(a => !a.Contains($"{separador}obj{separador}") && !a.Contains($"{separador}bin{separador}"))
            .Where(a => !a.Contains($"{separador}CaeManager.Migrations.PostgreSQL{separador}"))
            .Select(a => Path.GetRelativePath(raiz, a).Replace(separador, '/'))
            .Where(r => !ArchivosAutorizados.Contains(r))
            .Where(r => File.ReadLines(Path.Combine(raiz, r)).Any(l => PatronCampo.IsMatch(l)))
            .ToList();

        usos.Should().BeEmpty(
            "los campos de acceso y prevención del Centro no generan requisitos, no bloquean accesos, no entran " +
            "en el cumplimiento y la siembra no los rellena (decisión de producto 2026-10-16)");

        // Control positivo: el patrón sí encuentra los campos donde deben estar.
        File.ReadLines(Path.Combine(raiz, "src/CaeManager.Domain/Centros/Centro.cs"))
            .Count(l => PatronCampo.IsMatch(l)).Should().BeGreaterThan(0);
    }

    [Fact]
    public void La_migracion_deja_los_Centros_existentes_sin_informar()
    {
        var columnas = new AnadeFichaCentro().UpOperations.OfType<AddColumnOperation>().ToList();

        columnas.Select(c => c.Name).Should().BeEquivalentTo(CamposDeLaFicha);
        columnas.Should().AllSatisfy(c =>
        {
            c.Table.Should().Be("Centros");
            c.IsNullable.Should().BeTrue();
            c.DefaultValue.Should().BeNull("un Centro existente no puede amanecer con un «No» que nadie ha informado");
            c.DefaultValueSql.Should().BeNull();
            c.ComputedColumnSql.Should().BeNull();
        });
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
