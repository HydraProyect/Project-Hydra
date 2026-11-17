using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// La siembra administrativa de la demo Inbound llama a dos núcleos SIN la guarda de Development de la siembra local:
/// <c>InboundDemoSeeder.SembrarLoteInboundAsync</c> (el lote, con el contexto de tráfico) y
/// <c>LineaInboundDemoSeeder.ContratarLineaAsync</c> (la línea Inbound del Tenant TCE, con la identidad de bootstrap). Es lo
/// que la hace posible en staging, que arranca como <c>Production</c>, y por eso sigue siendo correcta solo mientras se alcance
/// desde el modo de CLI <c>--sembrar-demo-inbound</c> de <c>Program.cs</c>, que termina el proceso antes del arranque normal.
/// Este trinquete vigila quién llama a cada núcleo, dónde está la llamada y en qué orden, y que ninguno de los ficheros del
/// camino nombre un servicio que envíe correo. Mismo instrumento que <see cref="PilotoOutboundSoloDesdeElModoCliTests"/>.
/// </summary>
public class InboundDemoSoloDesdeElModoCliTests
{
    private const string Siembra = "InboundDemoAdministrativa";
    private const string ModoSembrar = "if (args.Contains(InboundDemoAdministrativa.ArgumentoSembrar))";

    [Fact]
    public void Solo_Program_invoca_la_siembra_administrativa_de_la_demo_Inbound()
    {
        var invocadores = FicherosDeCodigo()
            .Where(f => Texto(f).Contains(Siembra + ".SembrarAsync(", StringComparison.Ordinal) ||
                        Texto(f).Contains(Siembra + ".EjecutarAsync(", StringComparison.Ordinal))
            .Select(Relativa)
            .ToList();

        invocadores.Should().BeEquivalentTo(["src/CaeManager.Web/Program.cs"],
            "un hosted service, un seeder o un endpoint que la llamara sembraría en un servidor real sin el modo de CLI que la protege");
    }

    [Theory]
    [InlineData("SembrarLoteInboundAsync(", "InboundDemoSeeder.cs")]
    [InlineData("ContratarLineaAsync(", "LineaInboundDemoSeeder.cs")]
    public void Los_nucleos_sin_guarda_de_Development_solo_los_llaman_la_siembra_local_y_la_administrativa(string metodo, string propietario)
    {
        var llamadores = FicherosDeCodigo()
            .Where(f => Texto(f).Contains(metodo, StringComparison.Ordinal))
            .Select(f => Path.GetFileName(f))
            .ToList();

        llamadores.Should().BeEquivalentTo([propietario, "InboundDemoAdministrativa.cs"],
            "es el camino sin la guarda de Development: un tercer llamador es un tercer camino a un servidor real sin revisar");

        var declaracion = Texto(Path.Combine(Seed(), propietario));
        declaracion.Should().MatchRegex(@"internal static async Task " + Regex.Escape(metodo),
            "si fuera público, otro ensamblado podría sembrar sin pasar por las guardas de la siembra local ni por el modo de CLI");
    }

    [Theory]
    [InlineData("InboundDemoSeeder.cs", "public static async Task SeedAsync(", "RechazarFueraDeDevelopment(configuration, entorno);", "SembrarLoteInboundAsync(")]
    [InlineData("LineaInboundDemoSeeder.cs", "public static async Task SeedAsync(", "if (!entorno.IsDevelopment())", "ContratarLineaAsync(")]
    public void La_siembra_local_sigue_llamando_al_nucleo_despues_de_su_guarda_de_Development(
        string fichero, string metodo, string guarda, string nucleo)
    {
        var (cuerpo, _) = BloqueDe(Texto(Path.Combine(Seed(), fichero)), metodo);

        var posicionGuarda = cuerpo.IndexOf(guarda, StringComparison.Ordinal);
        posicionGuarda.Should().BeGreaterThan(-1, "control positivo: la siembra local conserva su guarda de Development");
        cuerpo.IndexOf(nucleo, StringComparison.Ordinal).Should().BeGreaterThan(posicionGuarda,
            "la siembra local llega al núcleo solo después de negarse fuera de Development");
    }

    [Fact]
    public void En_Program_la_siembra_esta_dentro_del_modo_de_CLI_que_termina_el_proceso_y_antes_de_toda_siembra_de_arranque()
    {
        var program = Program();
        var (bloque, cierre) = BloqueDe(program, ModoSembrar);
        var primeraSiembraDeArranque = program.IndexOf("IdentitySeeder.SeedAsync(", StringComparison.Ordinal);

        primeraSiembraDeArranque.Should().BeGreaterThan(-1, "control positivo: el instrumento ve la siembra de arranque");

        bloque.Should().Contain(Siembra + ".SembrarAsync(", "la llamada va DENTRO del modo de CLI");
        bloque.Should().Contain("Environment.ExitCode = " + Siembra + ".Informar(",
            "el código de salida del modo sale de la autoverificación: si no llegara a ExitCode, un lote que no cuadra terminaría con 0");
        bloque.TrimEnd().Should().EndWith("return;",
            "el modo de CLI termina el proceso con return; sin levantar la aplicación: si faltara, la siembra seguiría al arranque normal");
        Regex.Count(program, Regex.Escape(Siembra + ".SembrarAsync(")).Should().Be(1,
            "una sola llamada en Program.cs, y es la del bloque");
        cierre.Should().BeLessThan(primeraSiembraDeArranque, "el modo de CLI cierra antes de cualquier siembra del arranque normal");
    }

    [Fact]
    public void Cualquier_excepcion_del_modo_termina_con_codigo_distinto_de_cero_y_diciendo_como_reanudar()
    {
        var (bloque, _) = BloqueDe(Program(), ModoSembrar);

        Regex.IsMatch(
                bloque,
                @"catch \(Exception ex\)\s*\{\s*Console\.Error\.WriteLine\(" + Regex.Escape(Siembra + ".MensajeDeInterrupcion(ex)") + @"\);\s*Environment\.ExitCode = 1;\s*\}")
            .Should().BeTrue("un fallo a mitad también tiene que salir con 1 y con el mensaje de cómo reanudar");
        Regex.Matches(bloque, @"\bcatch\b").Count.Should().Be(1, "un catch más estrecho delante dejaría fuera a las demás excepciones");
        bloque.Should().NotContain("ex.ToString()").And.NotContain("ex.StackTrace").And.NotContain("ex.Data",
            "del fallo solo se imprime el tipo y el mensaje");
    }

    /// <summary>
    /// En staging el SMTP es real y no tiene guarda: la siembra no puede pasar por nada que envíe correo. Ningún fichero del
    /// camino nombra el servicio de correo, un Command (<c>ISender</c>/<c>IMediator</c>) ni los flujos que escriben a una
    /// persona (alta de Tenant, invitación, recuperación o confirmación de cuenta). La prueba de comportamiento —un espía de
    /// <c>IEmailService</c> sin intentos tras sembrar en Production— está en la suite de integración.
    /// </summary>
    [Theory]
    [InlineData("InboundDemoAdministrativa.cs")]
    [InlineData("InboundDemoSeeder.cs")]
    [InlineData("LineaInboundDemoSeeder.cs")]
    [InlineData("InboundDemoPdf.cs")]
    public void Ningun_fichero_de_la_siembra_nombra_un_servicio_que_envie_correo(string fichero)
    {
        var codigo = SinComentarios(Texto(Path.Combine(Seed(), fichero)));
        codigo.Should().NotBeNullOrWhiteSpace("control positivo: el instrumento lee el fichero");

        foreach (var prohibido in new[]
                 {
                     "IEmailService", "IEmailSender", "EnviarAsync(", "ISender", "IMediator", ".Send(", "Command(",
                     "GeneratePasswordResetTokenAsync", "GenerateEmailConfirmationTokenAsync", "Invitacion", "AprovisionarTenantCommand",
                 })
            codigo.Should().NotContain(prohibido, $"{fichero} está en el camino de la siembra de staging, donde el SMTP es real");
    }

    [Fact]
    public void El_alta_de_cuentas_y_Tenants_que_reutiliza_la_siembra_no_envia_correo()
    {
        var delegacion = SinComentarios(Texto(Path.Combine(Seed(), "DelegacionDemoSeeder.cs")));

        foreach (var metodo in new[] { "CrearUsuarioConsultoraAsync(", "AprovisionarTenantAsync(" })
        {
            var (cuerpo, _) = BloqueDe(delegacion, "static async Task", metodo);
            cuerpo.Should().Contain(metodo == "CrearUsuarioConsultoraAsync(" ? "CreateAsync(" : "Tenants.Add(",
                "control positivo: el instrumento ve el cuerpo del método");
            foreach (var prohibido in new[] { "IEmailService", "EnviarAsync(", "ISender", "IMediator", ".Send(", "TokenAsync" })
                cuerpo.Should().NotContain(prohibido, $"{metodo} lo reutiliza la siembra de staging, donde el SMTP es real");
        }
    }

    [Fact]
    public void Los_argumentos_y_las_claves_del_modo_son_los_que_documenta_la_PR()
    {
        var siembra = Texto(Path.Combine(Seed(), "InboundDemoAdministrativa.cs"));

        siembra.Should().Contain("ArgumentoSembrar = \"--sembrar-demo-inbound\"");
        siembra.Should().Contain("Seccion = \"InboundDemo\"");
        foreach (var clave in new[] { "ConfirmarEntorno", "DominioCorreo", "DirectorioCredenciales", "FechaDemostracion" })
            siembra.Should().Contain($"Seccion + \":{clave}\"");
        siembra.Should().Contain("SinTiposDeSalud: true", "D7: en staging el catálogo de cada Tenant se copia sin los tipos de salud");
    }

    private static string SinComentarios(string codigo) =>
        Regex.Replace(Regex.Replace(codigo, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline), @"//.*$", string.Empty, RegexOptions.Multiline);

    /// <summary>El cuerpo del método declarado como <c>static async Task… metodo</c> y la posición de su llave de cierre.</summary>
    private static (string Bloque, int Cierre) BloqueDe(string texto, string prefijoDeDeclaracion, string metodo)
    {
        var declaracion = Regex.Match(texto, Regex.Escape(prefijoDeDeclaracion) + @"[^\n;]*?\s" + Regex.Escape(metodo));
        declaracion.Success.Should().BeTrue($"control positivo: el método existe con esa forma ({metodo})");
        return BloqueDe(texto, declaracion.Value);
    }

    /// <summary>El cuerpo del bloque que empieza en la primera aparición de <paramref name="marca"/> y la posición de su llave de cierre.</summary>
    private static (string Bloque, int Cierre) BloqueDe(string texto, string marca)
    {
        var inicio = texto.IndexOf(marca, StringComparison.Ordinal);
        inicio.Should().BeGreaterThan(-1, $"control positivo: el bloque existe con esa forma ({marca})");

        var apertura = texto.IndexOf('{', inicio);
        var profundidad = 0;
        var cierre = -1;
        for (var i = apertura; i < texto.Length; i++)
        {
            if (texto[i] == '{') profundidad++;
            else if (texto[i] == '}' && --profundidad == 0) { cierre = i; break; }
        }

        cierre.Should().BeGreaterThan(apertura, "el bloque está delimitado por llaves balanceadas");
        return (texto[(apertura + 1)..cierre], cierre);
    }

    private static string Program() => Texto(Path.Combine(RaizDelRepositorio(), "src", "CaeManager.Web", "Program.cs"));

    private static string Seed() => Path.Combine(RaizDelRepositorio(), "src", "CaeManager.Infrastructure", "Persistence", "Seed");

    private static string Texto(string fichero) => File.ReadAllText(fichero);

    private static string Relativa(string fichero) => Path.GetRelativePath(RaizDelRepositorio(), fichero).Replace('\\', '/');

    private static IEnumerable<string> FicherosDeCodigo() =>
        Directory.EnumerateFiles(Path.Combine(RaizDelRepositorio(), "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") &&
                        !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));

    private static string RaizDelRepositorio()
    {
        var actual = new DirectoryInfo(AppContext.BaseDirectory);

        while (actual is not null && !File.Exists(Path.Combine(actual.FullName, "CaeManager.slnx")))
            actual = actual.Parent;

        return actual?.FullName ?? throw new InvalidOperationException(
            "No se encontró CaeManager.slnx subiendo desde " + AppContext.BaseDirectory +
            " — este test necesita el árbol fuente del repositorio.");
    }
}
