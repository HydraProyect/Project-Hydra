using System.Text.RegularExpressions;
using FluentAssertions;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// Toda mención a un fichero <c>.md</c> del repositorio apunta a un fichero que existe.
///
/// <para>
/// <b>Por qué hace falta.</b> Desde 2026-08-13 la documentación vive en el repositorio
/// privado de Negocio y este solo conserva lo necesario para compilar, probar y desplegar.
/// Los comentarios siguieron citando <c>docs/MULTITENANCY.md</c>, <c>ROADMAP.md</c>,
/// <c>RUNBOOK-RLS.md</c>… como si estuvieran al lado. Medido el 2026-09-26 sobre
/// <c>origin/main</c>: 1.192 menciones rotas en 771 ficheros. Una referencia rota no
/// falla en ningún build: se lee como una pista y lleva a un sitio que no existe.
/// </para>
///
/// <para>
/// <b>Contrato.</b> Una mención <c>ruta/nombre.md</c> vale si resuelve a un fichero real
/// —relativa al fichero que la contiene o a la raíz del repositorio— o si cita el
/// repositorio de Negocio con el prefijo <c>Project-Hydra-Negocio/</c>. La resolución es
/// por igualdad exacta de ruta, nunca por prefijo ni por sufijo: <c>EADME.md</c> no vale
/// porque exista <c>README.md</c>.
/// </para>
///
/// <para>
/// <b>Contrato efectivo, más estrecho que el nombre.</b>
/// <list type="bullet">
/// <item>Solo mira nombres que terminan en <c>.md</c>. Una carpeta citada
/// (<c>docs/blueprints</c>) o un documento sin extensión (<c>ADR-011 § 8</c>) no se
/// comprueba.</item>
/// <item>No comprueba que la ruta con prefijo <c>Project-Hydra-Negocio/</c> exista en
/// Negocio: ese repositorio no está en CI. Se verificó a mano al introducirlas.</item>
/// <item>Un nombre partido entre dos líneas de comentario no se reconstruye: se ve el
/// trozo final y, si ese trozo no existe, da rojo.</item>
/// <item>Excluye los <c>.Designer.cs</c> y el <c>ModelSnapshot</c> de EF: son instantáneas
/// generadas del modelo y copian literalmente el texto de la semilla.</item>
/// </list>
/// </para>
/// </summary>
public class EnlacesADocumentosExistentesTests
{
    private const string PrefijoNegocio = "Project-Hydra-Negocio/";

    /// <summary>
    /// Deuda congelada: menciones que no resuelven y no se reescriben en este incremento.
    /// Clave exacta <c>ruta|token</c>; el valor fija cuántas veces aparece. Si aparece más,
    /// menos o ya no aparece, el test falla: la lista solo puede encoger a propósito.
    /// </summary>
    private static readonly Dictionary<string, (int Veces, string Motivo)> DeudaCongelada = new(StringComparer.Ordinal)
    {
        [".gitignore|.local.md"] = (1, "patrón o enumeración de .gitignore, no es un enlace"),
        [".github/workflows/ci.yml|coveragereport-web/SummaryGithub.md"] = (1, "fichero generado en CI (informe de cobertura), no es un enlace"),
        [".github/workflows/ci.yml|coveragereport/SummaryGithub.md"] = (2, "fichero generado en CI (informe de cobertura), no es un enlace"),
        [".github/workflows/ci.yml|informe-cobertura-por-capas.md"] = (3, "fichero generado en CI (informe de cobertura), no es un enlace"),
        [".gitignore|README.md/CLAUDE.md/AGENTS.md"] = (1, "patrón o enumeración de .gitignore, no es un enlace"),
        ["scripts/control-estado-ramas.sh|261o.md"] = (1, "fichero de prueba con acento que crea el propio guion, no es un enlace"),
        ["scripts/control-estado-ramas.sh|año.md"] = (6, "fichero de prueba con acento que crea el propio guion, no es un enlace"),
        ["scripts/detectar-referencias-docs.sh|doc1.md"] = (2, "marcador de uso del guion, no es un enlace"),
        ["scripts/detectar-referencias-docs.sh|doc2.md"] = (2, "marcador de uso del guion, no es un enlace"),
        ["scripts/estado-ramas.sh|261o.md"] = (1, "fichero de prueba con acento que crea el propio guion, no es un enlace"),
        ["scripts/estado-ramas.sh|año.md"] = (1, "fichero de prueba con acento que crea el propio guion, no es un enlace"),
        ["scripts/respaldo-local.ps1|.local.md"] = (2, "patrón de ficheros locales que el respaldo recoge, no es un enlace"),
        ["scripts/respaldo-local.ps1|RUNBOOK-GRAPH-M365.local.md"] = (1, "patrón de ficheros locales que el respaldo recoge, no es un enlace"),
        ["scripts/verificar-gobernanza-pr.tests.sh|RANDOM.md"] = (1, "fichero temporal del test del guion, no es un enlace"),
        ["scripts/verificar-gobernanza-pr.tests.sh|TMP_ROOT/cuerpo-tab.md"] = (1, "fichero temporal del test del guion, no es un enlace"),
        ["scripts/verificar-licencias-nuget.sh|LICENSE.md"] = (1, "nombre de fichero de licencia dentro de un .nuspec, no es un enlace"),
        ["scripts/verificar-licencias-nuget.tests.sh|LICENSE.md"] = (1, "nombre de fichero de licencia dentro de un .nuspec, no es un enlace"),
        ["src/CaeManager.Application/Centros/CalculoEstadoCentroService.cs|PLAN-EJECUCION-UX.md"] = (4, "diferida: fichero de una línea viva (P1-L1, 2026-09-26); reescribir al cerrarse"),
        ["src/CaeManager.Application/Centros/Commands/CrearCanalGestion/CrearCanalGestionCommand.cs|PLAN-EJECUCION-UX.md"] = (1, "diferida: fichero de una línea viva (P1-L1, 2026-09-26); reescribir al cerrarse"),
        ["src/CaeManager.Application/Centros/Commands/CrearCanalGestion/CrearCanalGestionCommand.cs|ROADMAP.md"] = (1, "diferida: fichero de una línea viva (P1-L1, 2026-09-26); reescribir al cerrarse"),
        ["src/CaeManager.Application/Centros/Commands/CrearCanalGestion/CrearCanalGestionCommand.cs|docs/business/MATURITY_REVIEW.md"] = (1, "diferida: fichero de una línea viva (P1-L1, 2026-09-26); reescribir al cerrarse"),
        ["src/CaeManager.Application/Centros/Commands/EditarCanalGestion/EditarCanalGestionCommand.cs|PLAN-EJECUCION-UX.md"] = (1, "diferida: fichero de una línea viva (P1-L1, 2026-09-26); reescribir al cerrarse"),
        ["src/CaeManager.Application/Centros/Commands/EliminarCanalGestion/EliminarCanalGestionCommand.cs|PLAN-EJECUCION-UX.md"] = (1, "diferida: fichero de una línea viva (P1-L1, 2026-09-26); reescribir al cerrarse"),
        ["src/CaeManager.Application/Centros/Commands/MarcarCanalGestionPrincipal/MarcarCanalGestionPrincipalCommand.cs|PLAN-EJECUCION-UX.md"] = (1, "diferida: fichero de una línea viva (P1-L1, 2026-09-26); reescribir al cerrarse"),
        ["src/CaeManager.Infrastructure/AsistenteIa/AnthropicAsistenteIaService.cs|ROADMAP.md"] = (1, "texto del prompt del asistente: cambiarlo es cambio de comportamiento"),
        ["src/CaeManager.Infrastructure/DependencyInjection/InfrastructureServiceCollectionExtensions.cs|RUNBOOK-CLAVES.md"] = (1, "mensaje de excepción o de registro en producción"),
        ["src/CaeManager.Infrastructure/Identity/IdentitySeeder.cs|DEPLOY.md"] = (1, "mensaje de excepción o de registro en producción"),
        ["src/CaeManager.Infrastructure/Persistence/Seed/TipoDocumentoSeedData.cs|ROADMAP.md"] = (1, "literal sembrado en base de datos: cambiarlo exige migración"),
        ["src/CaeManager.Migrations.PostgreSQL/Migrations/20260801120000_HabilitarRlsPostgres.cs|docs/MULTITENANCY.md"] = (1, "dentro del SQL de una migración aplicada"),
        ["src/CaeManager.Migrations.PostgreSQL/Migrations/20260809152455_ModalidadPreventivaObligatoriaCae.cs|ROADMAP.md"] = (1, "literal sembrado en base de datos: cambiarlo exige migración"),
        ["src/CaeManager.Web/Api/V1/ApiKeySecuritySchemeTransformer.cs|docs/business/MATURITY_REVIEW.md"] = (1, "descripción publicada en OpenAPI: cambio de comportamiento"),
        ["src/CaeManager.Web/Components/Layout/MainLayout.razor.cs|docs/business/MATURITY_REVIEW.md"] = (1, "diferida: fichero de una línea viva (P1-L1, 2026-09-26); reescribir al cerrarse"),
        ["src/CaeManager.Web/Features/Documentos/Pages/Documentos.razor.cs|PLAN-EJECUCION-UX.md"] = (1, "diferida: fichero de una línea viva (P1-L1, 2026-09-26); reescribir al cerrarse"),
        ["src/CaeManager.Web/Features/Documentos/Pages/Documentos.razor.cs|docs/business/MATURITY_REVIEW.md"] = (2, "diferida: fichero de una línea viva (P1-L1, 2026-09-26); reescribir al cerrarse"),
        ["src/CaeManager.Web/Features/Documentos/Pages/Documentos.razor.cs|docs/ux-audit/02-clientes.md"] = (1, "diferida: fichero de una línea viva (P1-L1, 2026-09-26); reescribir al cerrarse"),
        ["src/CaeManager.Web/Features/Documentos/Pages/Documentos.razor.cs|docs/ux-audit/05-trabajadores-vehiculos.md"] = (1, "diferida: fichero de una línea viva (P1-L1, 2026-09-26); reescribir al cerrarse"),
        ["src/CaeManager.Web/Features/Empresas/Components/EmpresaWorkspacePanel.razor|PLAN-EJECUCION-UX.md"] = (1, "diferida: fichero de una línea viva (P1-L1, 2026-09-26); reescribir al cerrarse"),
        ["src/CaeManager.Web/Features/GestionRoles/Pages/Roles.razor.cs|ROADMAP.md"] = (1, "diferida: fichero de una línea viva (P1-L1, 2026-09-26); reescribir al cerrarse"),
        ["src/CaeManager.Web/Features/Subcontratas/Components/SubcontrataWorkspacePanel.razor|04_UX_PATTERNS.md"] = (1, "diferida: fichero de una línea viva (P1-L1, 2026-09-26); reescribir al cerrarse"),
        ["src/CaeManager.Web/Features/Subcontratas/Components/SubcontrataWorkspacePanel.razor|PLAN-EJECUCION-UX.md"] = (1, "diferida: fichero de una línea viva (P1-L1, 2026-09-26); reescribir al cerrarse"),
        ["src/CaeManager.Web/Features/Usuarios/Pages/Usuarios.razor.cs|docs/ux-audit/05-trabajadores-vehiculos.md"] = (1, "diferida: fichero de una línea viva (P1-L1, 2026-09-26); reescribir al cerrarse"),
        ["tests/CaeManager.Web.Tests/DocumentosGen2Tests.cs|CAPA-USUARIO-AVANZADO-TALVEG.md"] = (1, "diferida: fichero de una línea viva (P1-L1, 2026-09-26); reescribir al cerrarse"),
    };

    private sealed record Mencion(string Fichero, int Linea, string Token);

    private static readonly Regex PatronMd = new(
        @"(?<![\w/.:\-])((?:\.{1,2}/)*(?:[\w.\-]+/)*[\w.\-]+\.md)\b",
        RegexOptions.Compiled);

    private static readonly string[] Extensiones =
    [
        ".cs", ".razor", ".css", ".js", ".ts", ".sh", ".ps1", ".py", ".yml", ".yaml", ".json",
        ".md", ".sql", ".example", ".runsettings", ".props", ".targets", ".csproj", ".slnx",
        ".xml", ".txt", ".html", ".config",
    ];

    private static readonly string[] NombresSinExtension =
        ["Dockerfile", ".gitignore", ".dockerignore", ".gitattributes", ".editorconfig"];

    private static readonly string[] DirectoriosExcluidos =
        [".git", "bin", "obj", "node_modules", "TestResults", ".vs", ".claude", "coveragereport"];

    [Fact]
    public void Toda_mencion_a_un_md_apunta_a_un_fichero_que_existe()
    {
        var raiz = RaizDelRepositorio();
        var existentes = FicherosExistentes(raiz);
        existentes.Should().Contain("README.md",
            "sin la raíz real del repositorio este trinquete estaría en verde por no mirar nada");

        var rotas = FicherosDeTexto(raiz)
            .SelectMany(f => MencionesRotas(f.Relativa, File.ReadAllText(f.Absoluta), existentes))
            .ToList();
        rotas.Should().NotBeEmpty("la deuda congelada no está vacía: si no ve ninguna, el escáner no está leyendo");

        var porClave = rotas.GroupBy(m => $"{m.Fichero}|{m.Token}", StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        var nuevas = porClave
            .Where(kv => !DeudaCongelada.TryGetValue(kv.Key, out var d) || d.Veces != kv.Value.Count)
            .SelectMany(kv => kv.Value.Select(m =>
                $"{m.Fichero}:{m.Linea}  {m.Token}" +
                (DeudaCongelada.TryGetValue(kv.Key, out var d) ? $"  (congeladas {d.Veces}, vistas {kv.Value.Count})" : "")))
            .ToList();

        string.Join(Environment.NewLine, nuevas).Should().BeEmpty(
            "una mención a un .md tiene que resolver a un fichero de este repositorio o citar " +
            $"el de Negocio con el prefijo {PrefijoNegocio} (p. ej. {PrefijoNegocio}tecnico/RUNBOOK-RLS.md)");

        var retiradas = DeudaCongelada.Keys.Where(k => !porClave.ContainsKey(k)).ToList();
        retiradas.Should().BeEmpty("esas entradas de deuda ya no aparecen: bórralas de DeudaCongelada");
    }

    /// <summary>
    /// Prueba de sensibilidad permanente sobre el detector, independiente del árbol: si
    /// alguien cambia la resolución exacta por <c>Contains</c>, <c>StartsWith</c> o
    /// <c>EndsWith</c>, o deja pasar cualquier cosa que lleve la palabra Negocio, esto se
    /// pone rojo aunque el repositorio esté limpio.
    /// </summary>
    [Fact]
    public void El_detector_distingue_una_mencion_rota_de_una_viva()
    {
        var existentes = new HashSet<string>(StringComparer.Ordinal)
        {
            "README.md", "tools/PortalPruebaExtension/README.md", "src/Web/Programa.cs",
        };

        string[] Tokens(string fichero, string texto) =>
            MencionesRotas(fichero, texto, existentes).Select(m => m.Token).ToArray();

        Tokens("src/Web/Programa.cs", "// ver docs/MULTITENANCY.md § 4").Should().Equal("docs/MULTITENANCY.md");
        Tokens("src/Web/Programa.cs", "// ver ROADMAP.md").Should().Equal("ROADMAP.md");
        Tokens("src/Web/Programa.cs", "// ver ../../README.md y README.md").Should().BeEmpty();
        Tokens("src/Web/Programa.cs", "// tools/PortalPruebaExtension/README.md").Should().BeEmpty();
        Tokens("src/Web/Programa.cs", "// Project-Hydra-Negocio/tecnico/RUNBOOK-RLS.md").Should().BeEmpty();
        Tokens("src/Web/Programa.cs", "// ../Project-Hydra-Negocio/tecnico/X.md").Should().BeEmpty();

        Tokens("src/Web/Programa.cs", "// EADME.md").Should().Equal("EADME.md");
        Tokens("src/Web/Programa.cs", "// README.md.md").Should().Equal("README.md.md");
        Tokens("src/Web/Programa.cs", "// tools/PortalPruebaExtension/READMEs.md").Should().Equal("tools/PortalPruebaExtension/READMEs.md");
        Tokens("src/Web/Programa.cs", "// PortalPruebaExtension/README.md").Should().Equal("PortalPruebaExtension/README.md");
        Tokens("src/Web/Programa.cs", "// repo de Negocio: tecnico/X.md").Should().Equal("tecnico/X.md");
        Tokens("src/Web/Programa.cs", "// Hydra-Negocio/tecnico/X.md").Should().Equal("Hydra-Negocio/tecnico/X.md");
        Tokens("src/Web/Programa.cs", "// https://example.org/docs/X.md").Should().BeEmpty();
    }

    private static IEnumerable<Mencion> MencionesRotas(string fichero, string contenido, HashSet<string> existentes)
    {
        var directorio = fichero.Contains('/') ? fichero[..fichero.LastIndexOf('/')] : "";
        var lineas = contenido.Split('\n');
        for (var i = 0; i < lineas.Length; i++)
        {
            foreach (Match m in PatronMd.Matches(lineas[i]))
            {
                var token = m.Groups[1].Value;
                if (CitaNegocio(token)) continue;
                if (Resuelve(directorio, token, existentes) || Resuelve("", token, existentes)) continue;
                yield return new Mencion(fichero, i + 1, token);
            }
        }
    }

    private static bool CitaNegocio(string token)
    {
        var sinSubidas = token;
        while (sinSubidas.StartsWith("../", StringComparison.Ordinal)) sinSubidas = sinSubidas[3..];
        return sinSubidas.StartsWith(PrefijoNegocio, StringComparison.Ordinal);
    }

    private static bool Resuelve(string directorio, string token, HashSet<string> existentes)
    {
        var partes = new List<string>();
        foreach (var segmento in (directorio.Length == 0 ? token : $"{directorio}/{token}").Split('/'))
        {
            if (segmento is "" or ".") continue;
            if (segmento == "..")
            {
                if (partes.Count == 0) return false;
                partes.RemoveAt(partes.Count - 1);
                continue;
            }
            partes.Add(segmento);
        }
        return existentes.Contains(string.Join('/', partes));
    }

    private static HashSet<string> FicherosExistentes(string raiz) =>
        new(Recorrer(raiz).Select(f => Relativa(raiz, f)), StringComparer.Ordinal);

    private static IEnumerable<(string Absoluta, string Relativa)> FicherosDeTexto(string raiz) =>
        Recorrer(raiz)
            .Where(f => Extensiones.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase)
                        || NombresSinExtension.Contains(Path.GetFileName(f), StringComparer.Ordinal))
            .Where(f => !f.EndsWith(".Designer.cs", StringComparison.Ordinal)
                        && !f.EndsWith("ModelSnapshot.cs", StringComparison.Ordinal))
            .Where(f => !f.EndsWith("packages.lock.json", StringComparison.Ordinal))
            .Select(f => (Absoluta: f, Relativa: Relativa(raiz, f)))
            // Sus propios ejemplos del detector son menciones rotas a propósito.
            .Where(f => f.Relativa != "tests/CaeManager.Architecture.Tests/EnlacesADocumentosExistentesTests.cs");

    private static IEnumerable<string> Recorrer(string directorio)
    {
        foreach (var f in Directory.EnumerateFiles(directorio)) yield return f;
        foreach (var d in Directory.EnumerateDirectories(directorio))
        {
            var nombre = Path.GetFileName(d);
            if (DirectoriosExcluidos.Contains(nombre, StringComparer.Ordinal)) continue;
            if (nombre == "lib" && Path.GetFileName(Path.GetDirectoryName(d)) == "wwwroot") continue;
            foreach (var f in Recorrer(d)) yield return f;
        }
    }

    private static string Relativa(string raiz, string ruta) =>
        Path.GetRelativePath(raiz, ruta).Replace('\\', '/');

    private static string RaizDelRepositorio()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "CaeManager.slnx")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? AppContext.BaseDirectory;
    }
}
