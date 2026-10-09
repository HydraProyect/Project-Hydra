using System.Text.RegularExpressions;
using FluentAssertions;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// PD-A3 (commit 4): <c>IComandoDeAprovisionamiento</c> es una lista
/// explícita, no una heurística de carpeta o de nombre —
/// <c>AutorizacionEscrituraBehavior</c> solo deja pasar, bajo una sesión de
/// Aprovisionamiento, los comandos que la implementan. Este ratchet congela
/// el inventario exacto: un comando nuevo que la implemente, o uno de los
/// declarados que deje de hacerlo, tiene que verse en la revisión de ESTE test, no
/// perderse entre cientos de archivos.
///
/// <b>Por qué seis y no menos.</b> Los tres del alta de contenido
/// (Empresa/Centro/Trabajador) más los tres de importación —incluido
/// <c>RegistrarHistorialImportacionCommand</c>, que no es opcional: sin él el
/// alta quedaría hecha con su historial sin registrar, ver el propio comando.
/// Más los dos del logo del Tenant (selector de Tenant, lote 1), que no son alta
/// de contenido: su única escritura son las tres columnas del logo de
/// <c>Tenants</c>, lo único de esa tabla que el rol elevado puede actualizar.
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
        // Logo del Tenant (contrato del selector de Tenant, decisión 4): Soporte TALVEG lo escribe con
        // Aprovisionamiento sobre ese Tenant; el rol elevado solo actualiza las columnas del logo.
        "GuardarLogoTenantCommand",
        "RetirarLogoTenantCommand",
        // Encargo de administración (decisión D-8, 2026-10-08): Soporte TALVEG lo registra y lo retira con
        // Aprovisionamiento sobre ese Tenant; el rol elevado solo inserta el encargo y actualiza las columnas
        // de la retirada. Quién puede lo decide AutoridadSobreElEncargo, nunca el rol efectivo.
        "RegistrarEncargoAdministracionCommand",
        "RetirarEncargoAdministracionCommand",
    ];

    [Fact]
    public void Exactamente_los_comandos_declarados_implementan_IComandoDeAprovisionamiento()
    {
        var raiz = RaizDelRepositorio();
        var directorio = Path.Combine(raiz, "src", "CaeManager.Application");

        var patron = new Regex(
            // Paréntesis opcionales: un record sin parámetros (RetirarLogoTenantCommand) también
            // cuenta; sin esto el detector no lo veía y el inventario habría quedado incompleto en verde.
            @"public\s+record\s+(\w+)\s*(?:\([^)]*\))?[^;{(]*:\s*[^;{]*\bIComandoDeAprovisionamiento\b",
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
            "de los declarados que deje de hacerlo, tiene que verse aquí y no perderse en el resto del repositorio");
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
