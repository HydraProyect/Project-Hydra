using System.Text.RegularExpressions;
using FluentAssertions;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// PD-A3 (commit 4): <c>IComandoDeAprovisionamiento</c> es una lista
/// explícita, no una heurística de carpeta o de nombre —
/// <c>AutorizacionEscrituraBehavior</c> solo deja pasar, bajo una sesión de
/// Aprovisionamiento, los comandos que la implementan. Este ratchet congela
/// el inventario exacto: un comando nuevo que la implemente, o uno de los
/// seis que deje de hacerlo, tiene que verse en la revisión de ESTE test, no
/// perderse entre cientos de archivos.
///
/// <b>Por qué seis y no menos.</b> Los tres del alta de contenido
/// (Empresa/Centro/Trabajador) más los tres de importación —incluido
/// <c>RegistrarHistorialImportacionCommand</c>, que no es opcional: sin él el
/// alta quedaría hecha con su historial sin registrar, ver el propio comando.
/// </summary>
public class ComandosDeAprovisionamientoInventariadosTests
{
    private static readonly HashSet<string> ComandosEsperados =
    [
        "CrearEmpresaCommand",
        "CrearCentroCommand",
        "CrearTrabajadorCommand",
        "EjecutarImportacionCommand",
        "EjecutarImportacionCombinadaCommand",
        "RegistrarHistorialImportacionCommand",
    ];

    [Fact]
    public void Exactamente_los_seis_comandos_declarados_implementan_IComandoDeAprovisionamiento()
    {
        var raiz = RaizDelRepositorio();
        var directorio = Path.Combine(raiz, "src", "CaeManager.Application");

        var patron = new Regex(
            @"public\s+record\s+(\w+)\s*\([^)]*\)[^;{]*:\s*[^;{]*\bIComandoDeAprovisionamiento\b",
            RegexOptions.Compiled | RegexOptions.Singleline);

        var encontrados = new HashSet<string>();

        foreach (var archivo in Directory.EnumerateFiles(directorio, "*.cs", SearchOption.AllDirectories))
        {
            var contenido = File.ReadAllText(archivo);
            foreach (Match m in patron.Matches(contenido))
                encontrados.Add(m.Groups[1].Value);
        }

        encontrados.Should().BeEquivalentTo(ComandosEsperados,
            "IComandoDeAprovisionamiento es una lista explícita: un comando nuevo que la implemente, o uno " +
            "de los seis que deje de hacerlo, tiene que verse aquí y no perderse en el resto del repositorio");
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
