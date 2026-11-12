using System.Text.RegularExpressions;
using FluentAssertions;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// Decisión de producto D-P11 (2026-10-30, ajustada el 2026-11-12): el constructor público de <c>Empresa</c> deja
/// <c>EsPropia = true</c>, y todo Tenant propietario solo puede tener una Empresa propia. Toda construcción fuera de Domain
/// (las factorías de contraparte) y de los seeders de demostración tiene que estar en un handler que pase por
/// <c>ILimiteEmpresaPropia.ComprobarAltaAsync</c>; un escritor nuevo que construya una Empresa sin esa comprobación
/// hace fallar esta prueba.
/// </summary>
public class AltaDeEmpresaPropiaSoloTrasElLimiteTests
{
    // También con el nombre calificado (new CaeManager.Domain.Empresas.Empresa(...), new global::...). No ve un
    // `new(...)` con tipo destino ni un alias de using: hueco conocido de un instrumento de texto.
    private static readonly Regex ConstruccionDeEmpresa = new(@"\bnew\s+(?:global::)?(?:\w+\.)*Empresa\s*\(", RegexOptions.Compiled);

    // Restaurar una Empresa desde la Papelera también puede dejar una Empresa propia activa más.
    private static readonly string[] RestauracionesConLimite =
    [
        "src/CaeManager.Application/Empresas/Commands/RestaurarEmpresa/RestaurarEmpresaCommand.cs",
        "src/CaeManager.Application/Clientes/Commands/RestaurarCliente/RestaurarClienteCommand.cs",
        "src/CaeManager.Application/Subcontratas/Commands/RestaurarSubcontrata/RestaurarSubcontrataCommand.cs",
    ];

    private static readonly string[] EscritoresConLimite =
    [
        "src/CaeManager.Application/Empresas/Commands/CrearEmpresa/CrearEmpresaCommand.cs",
        "src/CaeManager.Application/Importacion/Commands/EjecutarImportacion/EjecutarImportacionCommand.cs",
        "src/CaeManager.Application/Importacion/Commands/EjecutarImportacionCombinada/EjecutarImportacionCombinadaCommand.cs",
        "src/CaeManager.Application/Importacion/Commands/EjecutarImportacionMaestra/EjecutarImportacionMaestraCommand.cs",
    ];

    // Las factorías de Cliente empresarial y Subcontrata ponen EsPropia = false; los seeders siembran Tenants de
    // demostración sin Producto Contratado.
    private static readonly string[] ExentosDelLimite =
    [
        "src/CaeManager.Domain/Empresas/Empresa.cs",
        "src/CaeManager.Infrastructure/Persistence/Seed/CoberturaPantallasSeeder.cs",
        "src/CaeManager.Infrastructure/Persistence/Seed/DatosPruebaSeeder.cs",
        "src/CaeManager.Infrastructure/Persistence/Seed/EscenariosDireccionDemoSeeder.cs",
        "src/CaeManager.Infrastructure/Persistence/Seed/GestorCaeCarteraMultiTenantSeeder.cs",
        "src/CaeManager.Infrastructure/Persistence/Seed/PilotoOutboundSeeder.cs",
    ];

    [Fact]
    public void Solo_los_escritores_con_el_limite_y_los_exentos_construyen_una_Empresa()
    {
        var raiz = RaizDelRepositorio();

        var quienesLaConstruyen = FicherosDeSrc(raiz)
            .Where(f => ConstruccionDeEmpresa.IsMatch(File.ReadAllText(f)))
            .Select(f => Path.GetRelativePath(raiz, f).Replace('\\', '/'))
            .ToList();

        // Igualdad, no subconjunto: si uno deja de construirla, la lista se queda vieja y se pierde el control
        // positivo del instrumento.
        quienesLaConstruyen.Should().BeEquivalentTo(EscritoresConLimite.Concat(ExentosDelLimite));
    }

    [Fact]
    public void Cada_escritor_con_el_limite_lo_comprueba()
    {
        var raiz = RaizDelRepositorio();

        foreach (var escritor in EscritoresConLimite.Concat(RestauracionesConLimite))
        {
            var codigo = File.ReadAllText(Path.Combine(raiz, escritor));
            // El límite llega por el constructor del handler, no lo resuelve un helper aparte.
            codigo.Should().Contain("ILimiteEmpresaPropia", $"{escritor} recibe el límite de Empresa propia");
            codigo.Should().Contain("ComprobarAltaAsync(", $"{escritor} puede dejar una Empresa propia activa más");
        }
    }

    private static IEnumerable<string> FicherosDeSrc(string raiz) => Directory
        .EnumerateFiles(Path.Combine(raiz, "src"), "*.*", SearchOption.AllDirectories)
        .Where(f => f.EndsWith(".cs", StringComparison.Ordinal) || f.EndsWith(".razor", StringComparison.Ordinal))
        .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                    && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    private static string RaizDelRepositorio()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !dir.GetFiles("*.slnx").Any() && !dir.GetFiles("*.sln").Any())
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("No se encontró la raíz del repositorio.");
    }
}
