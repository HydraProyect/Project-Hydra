using System.Text.RegularExpressions;
using FluentAssertions;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// <b>Todo servicio de fondo declara que quien actúa es la plataforma</b> (P41c).
///
/// <para>
/// Este trinquete existe porque la propiedad que la auditoría promete —"una
/// acción de máquina queda registrada como <c>Sistema</c>"— no la sostiene el
/// interceptor por sí solo: el interceptor sabe leer el ámbito ambiental, pero
/// alguien tiene que establecerlo, y ese alguien es el punto de entrada de cada
/// servicio de fondo. Un servicio nuevo que no lo declare no rompe nada visible:
/// sus filas caen en <c>Desconocido</c>, que es exactamente el hueco que P41c
/// cierra, y ningún test de integración de los existentes se pondría rojo por
/// ello. El fallo sería silencioso — la clase de fallo que un trinquete atrapa y
/// una suite de comportamiento no.
/// </para>
///
/// <para>
/// <b>Cómo se enumeran los servicios.</b> En el primer incremento se buscaban los
/// ficheros <c>*HostedService.cs</c> de Infrastructure: una convención de
/// <i>nombre</i>, que un servicio llamado de otra forma, o registrado desde Web,
/// se saltaba sin que nada avisara. Ahora hay dos fuentes independientes, que se
/// cruzan:
/// </para>
/// <list type="number">
/// <item>Los <b>registros</b> —<c>AddHostedService&lt;T&gt;</c> y
/// <c>AddSingleton&lt;IHostedService, T&gt;</c>— en cualquier proyecto de
/// <c>src</c>: lo que de verdad arranca el host.</item>
/// <item>Las <b>declaraciones</b>: toda clase concreta de <c>src</c> que hereda de
/// <c>BackgroundService</c> o implementa <c>IHostedService</c>/<c>IHostedLifecycleService</c>:
/// lo que <i>podría</i> arrancar.</item>
/// </list>
/// <para>
/// La unión es lo que se comprueba. Si las dos fuentes discrepan el test falla
/// aunque todos declaren el ámbito: o hay un servicio que nadie registra (código
/// muerto, o registrado por un mecanismo que no vemos) o hay un registro cuyo tipo
/// no hereda directamente de la base —y entonces su punto de entrada vive en otra
/// clase que este test no ha leído—. En ambos casos el resultado deja de ser
/// evidencia hasta que una persona lo mire.
/// </para>
///
/// <para>
/// <b>Lo que este test observa y lo que no.</b> Observa el TEXTO de cada servicio:
/// que dentro del cuerpo de su punto de entrada (<c>ExecuteAsync</c> o
/// <c>StartAsync</c>) haya un <c>using var</c> con
/// <c>AmbitoActorAuditoria.EstablecerSistema()</c>. No observa que el ámbito llegue
/// de verdad hasta el <c>SaveChanges</c> —eso lo demuestra
/// <c>TipoActorDeAuditoriaBajoRuntimeTests</c> contra PostgreSQL— ni observa los
/// seeders de arranque, que hoy siguen escribiendo con <c>Desconocido</c> (hueco
/// declarado, no olvido: la siembra se invoca desde siete llamadas sueltas de
/// Program.cs y darle su ámbito es un incremento propio).
/// </para>
///
/// <para>
/// <b>Sin lista blanca.</b> Los tres servicios de verificación de arranque no
/// escriben entidades auditadas hoy, y aun así lo declaran: una excepción "porque
/// este no escribe" caducaría el día que escribiera, sin que nada avisara.
/// </para>
/// </summary>
public class ServiciosDeFondoDeclaranActorDeSistemaTests
{
    private static readonly Regex Registro = new(
        @"AddHostedService\s*<\s*(?<tipo>[\w.]+)\s*>|AddSingleton\s*<\s*IHostedService\s*,\s*(?<tipo>[\w.]+)\s*>",
        RegexOptions.Compiled);

    /// <summary>
    /// Cabecera de una clase que entra en el host como servicio de fondo. Se ancla
    /// al inicio de línea para no casar la palabra "class" de un comentario, y
    /// admite constructor primario multilínea (<c>[^{;]*</c>).
    /// </summary>
    private static readonly Regex ClaseDeFondo = new(
        @"^[ \t]*(?:\[(?:[^\[\]\r\n]|\[[^\[\]\r\n]*\])*\][ \t]*)*(?:(?:public|internal|private|protected|sealed|static|partial|abstract|file|unsafe|new)[ \t]+)*class[ \t]+@?(?<nombre>\w+)(?<cabecera>[^{;]*)\{",
        RegexOptions.Compiled | RegexOptions.Multiline);

    private static readonly Regex BaseDeFondo = new(
        @"\b(BackgroundService|IHostedService|IHostedLifecycleService)\b",
        RegexOptions.Compiled);

    private static readonly Regex PuntoDeEntrada = new(
        @"\b(?:override\s+(?:async\s+)?Task\s+ExecuteAsync|(?:async\s+)?Task\s+StartAsync)\s*\(",
        RegexOptions.Compiled);

    private static readonly Regex Declaracion = new(
        @"\busing\s+var\s+\w+\s*=\s*AmbitoActorAuditoria\.EstablecerSistema\(\)",
        RegexOptions.Compiled);

    [Fact]
    public void Todo_servicio_de_fondo_declara_el_ambito_de_actor_de_sistema()
    {
        var fuentes = FuentesDeSrc();

        var registrados = fuentes.SelectMany(f => TiposRegistrados(f.Texto)).ToHashSet(StringComparer.Ordinal);
        var declarados = fuentes
            .SelectMany(f => ClasesDeFondo(f.Texto).Select(nombre => (Nombre: nombre, Fuente: f)))
            .ToList();
        var nombresDeclarados = declarados.Select(d => d.Nombre).ToHashSet(StringComparer.Ordinal);

        // Control positivo de las dos fuentes: un BackgroundService y un IHostedService
        // que existen hoy. Sin esto, una regex que dejara de casar devolvería listas
        // vacías y el resto del test pasaría sin observar nada.
        registrados.Should().Contain(["RetencionHostedService", "VerificacionKmsHostedService"],
            "la enumeración por registro tiene que ver un BackgroundService y un IHostedService reales");
        nombresDeclarados.Should().Contain(["RetencionHostedService", "VerificacionKmsHostedService"],
            "la enumeración por tipo base tiene que ver un BackgroundService y un IHostedService reales");

        registrados.Except(nombresDeclarados).Should().BeEmpty(
            "un tipo registrado cuya clase no hereda directamente de BackgroundService/IHostedService " +
            "tiene su punto de entrada en otra clase que este test no ha leído");
        nombresDeclarados.Except(registrados).Should().BeEmpty(
            "una clase de fondo que ningún AddHostedService registra es código muerto o se registra por " +
            "un mecanismo que este test no ve: en ambos casos su declaración de actor no está comprobada");

        var sinDeclarar = declarados
            .Where(d => !DeclaraEnSuPuntoDeEntrada(d.Fuente.Texto, d.Nombre))
            .Select(d => $"{d.Nombre} ({d.Fuente.Ruta})")
            .OrderBy(r => r, StringComparer.Ordinal)
            .ToList();

        sinDeclarar.Should().BeEmpty(
            "cada servicio de fondo tiene que abrir su punto de entrada con " +
            "`using var ambitoActor = AmbitoActorAuditoria.EstablecerSistema();`: sin esa línea sus " +
            "escrituras se auditan como Desconocido, indistinguibles de una persona cuya identidad no " +
            "se pudo resolver, que es justo la confusión que P41c deshace");
    }

    [Fact]
    public void El_detector_no_acepta_la_declaracion_fuera_del_punto_de_entrada()
    {
        const string enMetodoAuxiliarTrasLaEntrada = """
            public class Falso : BackgroundService
            {
                protected override async Task ExecuteAsync(CancellationToken ct)
                {
                    await TrabajarAsync(ct);
                }

                private async Task TrabajarAsync(CancellationToken ct)
                {
                    using var ambitoActor = AmbitoActorAuditoria.EstablecerSistema();
                    await Task.CompletedTask;
                }
            }
            """;
        DeclaraEnSuPuntoDeEntrada(enMetodoAuxiliarTrasLaEntrada, "Falso").Should().BeFalse(
            "declararlo en un método auxiliar deja fuera todos los demás caminos del servicio");

        // El caso que el detector anterior aceptaba mal: una declaración ANTERIOR al
        // punto de entrada, en un método auxiliar cualquiera.
        const string enMetodoAuxiliarAntesDeLaEntrada = """
            public class Falso : BackgroundService
            {
                private void Preparar()
                {
                    using var ambitoActor = AmbitoActorAuditoria.EstablecerSistema();
                }

                protected override async Task ExecuteAsync(CancellationToken ct)
                {
                    await Task.CompletedTask;
                }
            }
            """;
        DeclaraEnSuPuntoDeEntrada(enMetodoAuxiliarAntesDeLaEntrada, "Falso").Should().BeFalse();

        // Sin `using`, el ámbito nunca se restaura.
        const string sinUsing = """
            public class Falso : BackgroundService
            {
                protected override async Task ExecuteAsync(CancellationToken ct)
                {
                    var ambitoActor = AmbitoActorAuditoria.EstablecerSistema();
                    await Task.CompletedTask;
                }
            }
            """;
        DeclaraEnSuPuntoDeEntrada(sinUsing, "Falso").Should().BeFalse(
            "sin `using` el ámbito no se libera y se filtra al resto de la ejecución");

        const string correcto = """
            public class Bueno : BackgroundService
            {
                protected override async Task ExecuteAsync(CancellationToken ct)
                {
                    var texto = $"{{llave}} {ct}";
                    using var ambitoActor = AmbitoActorAuditoria.EstablecerSistema();
                    await TrabajarAsync(ct);
                }

                private void Otro() { }
            }
            """;
        DeclaraEnSuPuntoDeEntrada(correcto, "Bueno").Should().BeTrue(
            "y el detector tiene que aceptar la forma correcta, o sería rojo permanente en vez de trinquete");
    }

    /// <summary>
    /// Control positivo de la enumeración: lo que el trinquete anterior no veía. Un
    /// servicio con un nombre que no acaba en <c>HostedService</c>, definido y
    /// registrado en cualquier proyecto, tiene que aparecer en las dos fuentes.
    /// </summary>
    [Fact]
    public void La_enumeracion_ve_servicios_sin_el_sufijo_de_nombre_y_registros_fuera_de_Infrastructure()
    {
        const string registro = """
            services.AddHostedService<Trabajos.PurgadorNocturno>();
            services.AddSingleton<IHostedService, OtroProceso>();
            """;
        TiposRegistrados(registro).Should().BeEquivalentTo(["PurgadorNocturno", "OtroProceso"]);

        const string clase = """
            /// Una clase de la que se habla: class Comentario
            public sealed class PurgadorNocturno(ILogger<PurgadorNocturno> logger)
                : BackgroundService
            {
            }

            public class NoEsDeFondo(ILogger<NoEsDeFondo> logger) : IDisposable
            {
            }

            public abstract class BaseAbstracta : BackgroundService
            {
            }
            """;
        ClasesDeFondo(clase).Should().BeEquivalentTo(["PurgadorNocturno"],
            "el nombre no acaba en HostedService, el constructor primario ocupa varias líneas y hay una " +
            "clase sin base de fondo, una abstracta y un comentario con la palabra class, que no deben contar");

        // Formas de cabecera que una lista corta de modificadores dejaba invisibles: clase
        // anidada privada, `file`, atributo en la misma línea (con corchetes anidados) e
        // identificador verbatim.
        const string formas = """
            public class Contenedor
            {
                private sealed class Anidado : BackgroundService
                {
                }
            }

            file class SoloDelFichero : IHostedService
            {
            }

            [Tipo(typeof(int[]))] public sealed class ConAtributo : BackgroundService
            {
            }

            public sealed class @Verbatim : BackgroundService
            {
            }
            """;
        ClasesDeFondo(formas).Should().BeEquivalentTo(
            ["Anidado", "SoloDelFichero", "ConAtributo", "Verbatim"],
            "una clase de fondo no deja de serlo por ser anidada, `file`, llevar un atributo delante " +
            "en la misma línea o usar un identificador verbatim");
    }

    private static IEnumerable<string> TiposRegistrados(string texto)
        => Registro.Matches(texto).Select(m => m.Groups["tipo"].Value.Split('.').Last());

    private static IEnumerable<string> ClasesDeFondo(string texto)
        => ClaseDeFondo.Matches(texto)
            .Where(m => BaseDeFondo.IsMatch(m.Groups["cabecera"].Value)
                        && !Regex.IsMatch(m.Value, @"\babstract\b"))
            .Select(m => m.Groups["nombre"].Value);

    /// <summary>
    /// Busca la declaración DENTRO del cuerpo del punto de entrada, delimitado por
    /// emparejamiento de llaves (no por "hasta el siguiente método", que dejaba
    /// pasar una declaración en un auxiliar). Ignora comentarios de línea y
    /// literales de cadena para que una llave dentro de un texto no descuadre el
    /// recuento. Si las llaves no cuadran devuelve <c>false</c>: falla en voz alta.
    /// </summary>
    private static bool DeclaraEnSuPuntoDeEntrada(string texto, string clase)
    {
        var inicioClase = Regex.Match(texto, $@"\bclass\s+{Regex.Escape(clase)}\b");
        if (!inicioClase.Success) return false;

        var entrada = PuntoDeEntrada.Match(texto, inicioClase.Index);
        if (!entrada.Success) return false;

        var apertura = texto.IndexOf('{', entrada.Index + entrada.Length);
        if (apertura < 0) return false;

        var cuerpo = CuerpoHastaElCierre(texto, apertura);
        return cuerpo is not null && Declaracion.IsMatch(cuerpo);
    }

    private static string? CuerpoHastaElCierre(string texto, int apertura)
    {
        var profundidad = 0;

        for (var i = apertura; i < texto.Length; i++)
        {
            var c = texto[i];

            if (c == '/' && i + 1 < texto.Length && texto[i + 1] == '/')
            {
                while (i < texto.Length && texto[i] != '\n') i++;
                continue;
            }

            if (c == '"')
            {
                for (i++; i < texto.Length && texto[i] != '"'; i++)
                    if (texto[i] == '\\') i++;
                continue;
            }

            if (c == '{') profundidad++;
            else if (c == '}' && --profundidad == 0)
                return texto[apertura..(i + 1)];
        }

        return null;
    }

    private static List<(string Ruta, string Texto)> FuentesDeSrc()
    {
        var raiz = RaizDelRepositorio();
        var src = Path.Combine(raiz, "src");
        var sep = Path.DirectorySeparatorChar;

        return Directory
            .EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(ruta => !ruta.Contains($"{sep}obj{sep}", StringComparison.Ordinal)
                        && !ruta.Contains($"{sep}bin{sep}", StringComparison.Ordinal))
            .Select(ruta => (Path.GetRelativePath(raiz, ruta), File.ReadAllText(ruta)))
            .ToList();
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
