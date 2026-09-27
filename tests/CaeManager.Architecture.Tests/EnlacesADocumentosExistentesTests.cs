using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using FluentAssertions;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// Toda mención a un fichero con extensión <c>.md</c> apunta a un fichero versionado que existe.
///
/// <para>
/// <b>Por qué hace falta.</b> Desde 2026-08-13 la documentación vive en el repositorio
/// privado de Negocio y este solo conserva lo necesario para compilar, probar y desplegar.
/// Los comentarios siguieron citando <c>docs/MULTITENANCY</c>, <c>ROADMAP</c>,
/// <c>RUNBOOK-RLS</c>… (con su extensión) como si estuvieran al lado. Medido el 2026-09-26
/// sobre <c>origin/main</c>: 1.192 menciones rotas en 771 ficheros. Una referencia rota no
/// falla en ningún build: se lee como una pista y lleva a un sitio que no existe.
/// </para>
///
/// <para>
/// <b>Contrato.</b> Una mención vale si resuelve a un fichero versionado —relativa al fichero
/// que la contiene o a la raíz del repositorio— o si cita el repositorio de Negocio con el
/// prefijo <c>Project-Hydra-Negocio/</c>. La resolución es por igualdad exacta de ruta, nunca
/// por prefijo ni por sufijo: un nombre que solo coincide en parte con uno existente no vale.
/// </para>
///
/// <para>
/// <b>Contrato efectivo, más estrecho que el nombre.</b>
/// <list type="bullet">
/// <item>El universo es <c>git ls-files</c>, no el disco: un fichero ignorado o sin versionar
/// que exista en una copia local no hace viva una mención que en CI está rota, y lo que no
/// está versionado no se escanea.</item>
/// <item>Solo mira nombres con extensión <c>.md</c> (sin distinguir mayúsculas). Una carpeta
/// citada (<c>docs/blueprints</c>) o un documento sin extensión (<c>ADR-011 § 8</c>) no se
/// comprueba.</item>
/// <item>Un nombre pegado por delante a <c>:</c>, <c>.</c>, <c>-</c> o <c>/</c> no se ve: es el
/// precio de no confundir una URL con una ruta del repositorio.</item>
/// <item>No comprueba que la ruta con prefijo <c>Project-Hydra-Negocio/</c> exista en
/// Negocio: ese repositorio no está en CI. Se verificó a mano al introducirlas.</item>
/// <item>Un nombre partido entre dos líneas de comentario no se reconstruye: se ve el
/// trozo final y, si ese trozo no existe, da rojo.</item>
/// <item>Excluye los <c>.Designer.cs</c> y el <c>ModelSnapshot</c> de EF: son instantáneas
/// generadas del modelo y copian literalmente el texto de la semilla.</item>
/// <item>En este mismo fichero ignora solo las líneas que son claves de la deuda congelada;
/// el resto se escanea como cualquier otro.</item>
/// </list>
/// </para>
/// </summary>
public class EnlacesADocumentosExistentesTests
{
    private const string PrefijoNegocio = "Project-Hydra-Negocio/";

    private const string EsteFichero = "tests/CaeManager.Architecture.Tests/EnlacesADocumentosExistentesTests.cs";

    /// <summary>Solo para construir textos sin que el propio fuente contenga una mención.</summary>
    private const string Md = ".md";

    /// <summary>
    /// Deuda congelada: menciones que no resuelven y no se reescriben en este incremento.
    /// Clave exacta <c>ruta|token</c>; el valor fija cuántas veces aparece. Si aparece más,
    /// menos o ya no aparece, el test falla: la lista solo puede encoger a propósito.
    /// </summary>
    private static readonly Dictionary<string, (int Veces, string Motivo)> DeudaCongelada = new(StringComparer.Ordinal)
    {
        [".gitignore|.local.md"] = (1, "patrón o enumeración de .gitignore, no es un enlace"),
        [".gitignore|README.md/CLAUDE.md/AGENTS.md"] = (1, "patrón o enumeración de .gitignore, no es un enlace"),
        [".github/workflows/ci.yml|coveragereport-web/SummaryGithub.md"] = (1, "fichero generado en CI (informe de cobertura), no es un enlace"),
        [".github/workflows/ci.yml|coveragereport/SummaryGithub.md"] = (2, "fichero generado en CI (informe de cobertura), no es un enlace"),
        [".github/workflows/ci.yml|informe-cobertura-por-capas.md"] = (3, "fichero generado en CI (informe de cobertura), no es un enlace"),
        ["scripts/control-estado-ramas.sh|261o.md"] = (1, "fichero de prueba con acento que crea el propio guion, no es un enlace"),
        ["scripts/control-estado-ramas.sh|año.md"] = (6, "fichero de prueba con acento que crea el propio guion, no es un enlace"),
        ["scripts/detectar-referencias-docs.sh|doc1.md"] = (2, "marcador de uso del guion, no es un enlace"),
        ["scripts/detectar-referencias-docs.sh|doc2.md"] = (2, "marcador de uso del guion, no es un enlace"),
        ["scripts/detectar-referencias-docs.sh|docs/README.md"] = (1, "cita literal de lo que señaló una ejecución histórica del guion: esa ruta no existe a propósito"),
        ["scripts/estado-ramas.sh|261o.md"] = (1, "fichero de prueba con acento que crea el propio guion, no es un enlace"),
        ["scripts/estado-ramas.sh|año.md"] = (1, "fichero de prueba con acento que crea el propio guion, no es un enlace"),
        ["scripts/respaldo-local.ps1|.local.md"] = (2, "patrón de ficheros locales que el respaldo recoge, no es un enlace"),
        ["scripts/respaldo-local.ps1|RUNBOOK-GRAPH-M365.local.md"] = (1, "patrón de ficheros locales que el respaldo recoge, no es un enlace"),
        ["scripts/verificar-gobernanza-pr.tests.sh|RANDOM.md"] = (1, "fichero temporal del test del guion, no es un enlace"),
        ["scripts/verificar-gobernanza-pr.tests.sh|TMP_ROOT/cuerpo-tab.md"] = (1, "fichero temporal del test del guion, no es un enlace"),
        ["scripts/verificar-licencias-nuget.sh|LICENSE.md"] = (1, "nombre de fichero de licencia dentro de un .nuspec, no es un enlace"),
        ["scripts/verificar-licencias-nuget.tests.sh|LICENSE.md"] = (1, "nombre de fichero de licencia dentro de un .nuspec, no es un enlace"),
        ["src/CaeManager.Infrastructure/AsistenteIa/AnthropicAsistenteIaService.cs|ROADMAP.md"] = (1, "texto del prompt del asistente: cambiarlo es cambio de comportamiento"),
        ["src/CaeManager.Infrastructure/Identity/IdentitySeeder.cs|DEPLOY.md"] = (1, "mensaje de excepción o de registro en producción"),
        ["src/CaeManager.Infrastructure/Persistence/Seed/TipoDocumentoSeedData.cs|ROADMAP.md"] = (1, "literal sembrado en base de datos: cambiarlo exige migración"),
        ["src/CaeManager.Migrations.PostgreSQL/Migrations/20260801120000_HabilitarRlsPostgres.cs|docs/MULTITENANCY.md"] = (1, "dentro del SQL de una migración aplicada"),
        ["src/CaeManager.Migrations.PostgreSQL/Migrations/20260809152455_ModalidadPreventivaObligatoriaCae.cs|ROADMAP.md"] = (1, "literal sembrado en base de datos: cambiarlo exige migración"),
        ["src/CaeManager.Web/Api/V1/ApiKeySecuritySchemeTransformer.cs|docs/business/MATURITY_REVIEW.md"] = (1, "descripción publicada en OpenAPI: cambio de comportamiento"),
        ["src/CaeManager.Web/Features/Usuarios/Pages/Usuarios.razor.cs|docs/ux-audit/05-trabajadores-vehiculos.md"] = (1, "diferida: fichero de la PR abierta #931 (P1-L1, 2026-09-26); reescribir al fusionarse"),
    };

    private sealed record Mencion(string Fichero, int Linea, string Token);

    private static readonly Regex PatronMd = new(
        @"(?<![\w/.:\-])((?:\.{1,2}/)*(?:[\w.\-]+/)*[\w.\-]+\.(?i:md))\b",
        RegexOptions.Compiled);

    private static readonly Regex EntradaDeDeuda = new(@"^\s*\[""[^""]*""\] = \(\d+, """, RegexOptions.Compiled);

    private static readonly string[] Extensiones =
    [
        ".cs", ".razor", ".css", ".js", ".mjs", ".ts", ".sh", ".ps1", ".py", ".yml", ".yaml", ".json",
        ".toml", ".md", ".sql", ".example", ".runsettings", ".props", ".targets", ".csproj", ".slnx",
        ".xml", ".resx", ".txt", ".html", ".config",
    ];

    private static readonly string[] NombresSinExtension =
        ["Dockerfile", "Caddyfile", ".gitignore", ".dockerignore", ".gitattributes", ".editorconfig", ".claudeignore"];

    [Fact]
    public void Toda_mencion_a_un_md_apunta_a_un_fichero_que_existe()
    {
        var raiz = RaizDelRepositorio();
        var versionados = FicherosVersionados(raiz);
        var existentes = new HashSet<string>(versionados, StringComparer.Ordinal);
        existentes.Should().Contain($"README{Md}",
            "sin la lista real de ficheros versionados este trinquete estaría en verde por no mirar nada");

        var rotas = FicherosDeTexto(raiz, versionados)
            .SelectMany(f => MencionesRotas(f, File.ReadAllText(Path.Combine(raiz, f)), existentes))
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
            $"el de Negocio con el prefijo {PrefijoNegocio} (p. ej. {PrefijoNegocio}tecnico/RUNBOOK-RLS{Md})");

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
            $"README{Md}", $"tools/PortalPruebaExtension/README{Md}", "src/Web/Programa.cs",
        };

        string[] Tokens(string texto) =>
            MencionesRotas("src/Web/Programa.cs", texto, existentes).Select(m => m.Token).ToArray();

        Tokens($"// ver docs/MULTITENANCY{Md} § 4").Should().Equal($"docs/MULTITENANCY{Md}");
        Tokens($"// ver ROADMAP{Md}").Should().Equal($"ROADMAP{Md}");
        Tokens($"// ver ROADMAP{Md.ToUpperInvariant()}").Should().Equal($"ROADMAP{Md.ToUpperInvariant()}");
        Tokens($"// ver ../../README{Md} y README{Md}").Should().BeEmpty();
        Tokens($"// tools/PortalPruebaExtension/README{Md}").Should().BeEmpty();
        Tokens($"// {PrefijoNegocio}tecnico/RUNBOOK-RLS{Md}").Should().BeEmpty();
        Tokens($"// ../{PrefijoNegocio}tecnico/X{Md}").Should().BeEmpty();

        Tokens($"// EADME{Md}").Should().Equal($"EADME{Md}");
        Tokens($"// README{Md}{Md}").Should().Equal($"README{Md}{Md}");
        Tokens($"// tools/PortalPruebaExtension/READMEs{Md}").Should().Equal($"tools/PortalPruebaExtension/READMEs{Md}");
        Tokens($"// PortalPruebaExtension/README{Md}").Should().Equal($"PortalPruebaExtension/README{Md}");
        Tokens($"// repo de Negocio: tecnico/X{Md}").Should().Equal($"tecnico/X{Md}");
        Tokens($"// Hydra-Negocio/tecnico/X{Md}").Should().Equal($"Hydra-Negocio/tecnico/X{Md}");
        Tokens($"// https://example.org/docs/X{Md}").Should().BeEmpty();
    }

    private static IEnumerable<Mencion> MencionesRotas(string fichero, string contenido, HashSet<string> existentes)
    {
        var directorio = fichero.Contains('/') ? fichero[..fichero.LastIndexOf('/')] : "";
        var esEsteFichero = fichero == EsteFichero;
        var lineas = contenido.Split('\n');
        for (var i = 0; i < lineas.Length; i++)
        {
            if (esEsteFichero && EntradaDeDeuda.IsMatch(lineas[i])) continue;
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

    private static IEnumerable<string> FicherosDeTexto(string raiz, IEnumerable<string> versionados) =>
        versionados
            .Where(f => Extensiones.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase)
                        || NombresSinExtension.Contains(Path.GetFileName(f), StringComparer.Ordinal))
            .Where(f => !f.EndsWith(".Designer.cs", StringComparison.Ordinal)
                        && !f.EndsWith("ModelSnapshot.cs", StringComparison.Ordinal)
                        && !f.EndsWith("packages.lock.json", StringComparison.Ordinal))
            // Un fichero borrado en la copia de trabajo sigue en el índice hasta el commit.
            .Where(f => File.Exists(Path.Combine(raiz, f)));

    /// <summary>
    /// Lista de <c>git ls-files</c>: el mismo universo en local que en CI. Si git no
    /// responde, el test falla en vez de quedarse sin ficheros que mirar.
    /// </summary>
    private static List<string> FicherosVersionados(string raiz)
    {
        var inicio = new ProcessStartInfo("git", "-c core.quotepath=off ls-files -z")
        {
            WorkingDirectory = raiz,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
        };
        using var proceso = Process.Start(inicio)
            ?? throw new InvalidOperationException("no se pudo arrancar git");
        var salida = proceso.StandardOutput.ReadToEnd();
        var error = proceso.StandardError.ReadToEnd();
        proceso.WaitForExit();
        if (proceso.ExitCode != 0)
            throw new InvalidOperationException($"git ls-files salió con {proceso.ExitCode}: {error}");
        return salida.Split('\0', StringSplitOptions.RemoveEmptyEntries).ToList();
    }

    private static string RaizDelRepositorio()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "CaeManager.slnx")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? AppContext.BaseDirectory;
    }
}
