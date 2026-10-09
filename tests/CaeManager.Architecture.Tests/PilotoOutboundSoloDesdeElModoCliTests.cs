using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// La siembra administrativa del piloto Outbound llama al núcleo
/// <c>PilotoOutboundSeeder.SembrarLoteAsync</c>, que NO lleva la guarda de Producción de
/// la siembra local: es lo que la hace posible en un servidor que arranca como
/// <c>Production</c> y, a la vez, la línea que más cuidado pide. Sigue siendo correcta
/// mientras solo se alcance desde el modo de CLI <c>--sembrar-piloto-outbound</c> de
/// <c>Program.cs</c>, que termina el proceso antes del arranque normal. Este trinquete
/// vigila eso —quién la llama, dónde está la llamada y en qué orden—, y que la retirada
/// entre por la puerta que exige la confirmación de entorno. Mismo instrumento que
/// <see cref="SiembraDemoDireccionSoloDesdeElModoCliTests"/>.
/// </summary>
public class PilotoOutboundSoloDesdeElModoCliTests
{
    private const string Siembra = "PilotoOutboundAdministrativa";
    private const string ModoSembrar = "if (args.Contains(PilotoOutboundAdministrativa.ArgumentoSembrar))";
    private const string ModoRetirar = "if (args.Contains(PilotoOutboundRetirada.Argumento))";

    [Fact]
    public void Solo_Program_invoca_la_siembra_administrativa_del_piloto()
    {
        var invocadores = FicherosDeCodigo()
            .Where(f => Texto(f).Contains(Siembra + ".SembrarAsync(", StringComparison.Ordinal) ||
                        Texto(f).Contains(Siembra + ".EjecutarAsync(", StringComparison.Ordinal))
            .Select(Relativa)
            .ToList();

        invocadores.Should().BeEquivalentTo(["src/CaeManager.Web/Program.cs"],
            "un hosted service, un seeder o un endpoint que la llamara sembraría en un servidor real sin el modo de CLI que la protege");
    }

    [Fact]
    public void El_nucleo_sin_guarda_de_entorno_solo_lo_llaman_la_siembra_local_y_la_administrativa()
    {
        var llamadores = FicherosDeCodigo()
            .Where(f => Texto(f).Contains("SembrarLoteAsync(", StringComparison.Ordinal))
            .Select(f => Path.GetFileName(f))
            .ToList();

        llamadores.Should().BeEquivalentTo(
            ["PilotoOutboundSeeder.cs", "PilotoOutboundAdministrativa.cs"],
            "es el camino sin la guarda de Producción: un tercer llamador es un tercer camino a un servidor real sin revisar");
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
            "el código de salida del modo sale de la autoverificación: si su resultado no llegara a ExitCode, una matriz que no cuadra terminaría con 0");
        bloque.TrimEnd().Should().EndWith("return;",
            "el modo de CLI termina el proceso con return; sin levantar la aplicación: si faltara, la siembra seguiría al arranque normal");
        Regex.Count(program, Regex.Escape(Siembra + ".SembrarAsync(")).Should().Be(1,
            "una sola llamada en Program.cs, y es la del bloque: una segunda fuera del modo de CLI sembraría en el arranque normal");
        cierre.Should().BeLessThan(primeraSiembraDeArranque, "el modo de CLI cierra antes de cualquier siembra del arranque normal");
    }

    [Fact]
    public void La_retirada_del_piloto_solo_entra_por_la_puerta_que_exige_el_entorno_confirmado()
    {
        var program = Program();
        var (bloque, cierre) = BloqueDe(program, ModoRetirar);

        bloque.Should().Contain("PilotoOutboundRetirada.RetirarLoteConfirmadoAsync(", "la retirada del modo de CLI pasa por la confirmación de entorno");
        bloque.TrimEnd().Should().EndWith("return;", "el modo de CLI termina el proceso sin levantar la aplicación");
        cierre.Should().BeLessThan(program.IndexOf("IdentitySeeder.SeedAsync(", StringComparison.Ordinal));

        var sinConfirmacion = FicherosDeCodigo()
            .Where(f => Regex.IsMatch(Texto(f), @"PilotoOutboundRetirada\.RetirarLoteAsync\(|(?<![\w.])RetirarLoteAsync\("))
            .Select(Relativa)
            .ToList();

        sinConfirmacion.Should().BeEquivalentTo(
            [
                "src/CaeManager.Infrastructure/Persistence/Seed/PilotoOutboundRetirada.cs",
                // La retirada de la demo a dirección declara su propio método con ese nombre: no es la del piloto.
                "src/CaeManager.Infrastructure/Persistence/Seed/SiembraDemoDireccionAdministrativa.cs",
            ],
            "la retirada sin confirmación de entorno es interna: solo la llama RetirarLoteConfirmadoAsync, en su propio fichero");

        var retirada = Texto(Path.Combine(RaizDelRepositorio(), "src", "CaeManager.Infrastructure", "Persistence", "Seed", "PilotoOutboundRetirada.cs"));
        retirada.Should().Contain("internal static async Task<IReadOnlyList<RetiradaTenantDemoService.ResultadoRetirada>> RetirarLoteAsync(",
            "si volviera a ser pública, Program.cs u otro ensamblado podrían retirar sin la confirmación de entorno");
    }

    [Theory]
    [InlineData(ModoSembrar, "PilotoOutboundAdministrativa.MensajeDeInterrupcion(ex)")]
    [InlineData(ModoRetirar, "PilotoOutboundRetirada.MensajeDeInterrupcion(ex)")]
    public void Cualquier_excepcion_de_los_modos_del_piloto_termina_con_codigo_distinto_de_cero_y_diciendo_como_reanudar(
        string modo, string mensaje)
    {
        var (bloque, _) = BloqueDe(Program(), modo);

        Regex.IsMatch(
                bloque,
                @"catch \(Exception ex\)\s*\{\s*Console\.Error\.WriteLine\(" + Regex.Escape(mensaje) + @"\);\s*Environment\.ExitCode = 1;\s*\}")
            .Should().BeTrue(
                "un fallo de disco o de base de datos a mitad también tiene que salir con 1 y con el mensaje de cómo reanudar, no como excepción sin tratar");
        Regex.Matches(bloque, @"\bcatch\b").Count.Should().Be(1,
            "un catch más estrecho delante (el de InvalidOperationException de antes) dejaría fuera a las demás o les cambiaría el mensaje");
        bloque.Should().NotContain("ex.ToString()").And.NotContain("ex.StackTrace").And.NotContain("ex.Data",
            "del fallo solo se imprime el tipo y el mensaje, que es lo que compone MensajeDeInterrupcion");
    }

    [Fact]
    public void El_arranque_local_no_decide_en_Program_si_la_autoverificacion_exige_o_avisa()
    {
        var program = Program();

        Regex.Count(program, Regex.Escape("PilotoOutboundAutoverificacion.MedirYExigirOAvisarAsync(")).Should().Be(1,
            "control positivo: el arranque invoca el método que mide y decide");
        program.Should().NotContain("PilotoOutboundAutoverificacion.MedirAsync(").And.NotContain("PilotoOutboundAutoverificacion.Exigir(",
            "si Program.cs vuelve a medir por su cuenta, un fallo de la medición en un re-arranque vuelve a tumbar el arranque sin test que lo vea");
    }

    /// <summary>
    /// El resultado de la siembra lleva dos listas de Tenants propietarios del mismo tipo —los recién escritos y los
    /// que tienen datos de otra versión—, así que el compilador no distingue una de otra. Con la de los recién
    /// escritos, lo que la ejecución acaba de sembrar dejaría de medirse y de exigirse; con una lista vacía, un
    /// Tenant con datos de otra versión volvería a tumbar el arranque.
    /// </summary>
    [Fact]
    public void El_arranque_local_pasa_a_la_autoverificacion_la_lista_de_Tenants_con_datos_de_otra_version_que_calculo_la_siembra()
    {
        var llamada = LlamadaA(Program(), "PilotoOutboundAutoverificacion.MedirYExigirOAvisarAsync(");

        llamada.Should().Contain("siembraPilotoOutbound.Escribio",
            "control positivo: el instrumento ve los argumentos de la llamada, no solo su nombre");
        llamada.Should().NotContain("TenantsConDatosNuevos",
            "los Tenants que esta ejecución acaba de escribir son justo los que hay que medir y exigir: pasarlos como «de otra versión» los eximiría");
        // La coma de delante ancla también «escribió»: sin ella, «!siembraPilotoOutbound.Escribio» casaría igual
        // y la ejecución que acaba de escribir pasaría por un re-arranque, del que todo solo se avisa.
        llamada.Should().MatchRegex(@",\s*siembraPilotoOutbound\.Escribio,\s*siembraPilotoOutbound\.TenantsConDatosDeOtraVersion,\s*logger\)",
            "«escribió» va tal cual lo entrega la siembra, y el argumento que le sigue es, entero, la lista que calculó antes de escribir: " +
            "con «escribió» negado, con otra lista o con una vacía, la autoverificación exime a quien no debe o vuelve a medir un Tenant " +
            "en el que esta ejecución no ha escrito");
    }

    [Fact]
    public void Los_argumentos_y_las_claves_de_los_modos_de_CLI_son_los_que_documenta_el_runbook()
    {
        var siembra = Texto(Path.Combine(RaizDelRepositorio(), "src", "CaeManager.Infrastructure", "Persistence", "Seed", "PilotoOutboundAdministrativa.cs"));
        var retirada = Texto(Path.Combine(RaizDelRepositorio(), "src", "CaeManager.Infrastructure", "Persistence", "Seed", "PilotoOutboundRetirada.cs"));

        siembra.Should().Contain("ArgumentoSembrar = \"--sembrar-piloto-outbound\"");
        retirada.Should().Contain("Argumento = \"--retirar-piloto-outbound\"");
        siembra.Should().Contain("Seccion = \"PilotoOutbound\"");
        foreach (var clave in new[] { "ConfirmarEntorno", "DominioCorreo", "DirectorioCredenciales", "FechaDemostracion", "CorreoContactos" })
            siembra.Should().Contain($"Seccion + \":{clave}\"");
    }

    /// <summary>El cuerpo del bloque <c>if</c> que empieza en <paramref name="modo"/> y la posición de su llave de cierre.</summary>
    private static (string Bloque, int Cierre) BloqueDe(string program, string modo)
    {
        var inicio = program.IndexOf(modo, StringComparison.Ordinal);
        inicio.Should().BeGreaterThan(-1, $"control positivo: el modo de CLI existe con esa forma ({modo})");

        var apertura = program.IndexOf('{', inicio);
        var profundidad = 0;
        var cierre = -1;
        for (var i = apertura; i < program.Length; i++)
        {
            if (program[i] == '{') profundidad++;
            else if (program[i] == '}' && --profundidad == 0) { cierre = i; break; }
        }

        cierre.Should().BeGreaterThan(apertura, "el bloque del modo de CLI está delimitado por llaves balanceadas");
        return (program[(apertura + 1)..cierre], cierre);
    }

    /// <summary>El texto de la llamada de <c>Program.cs</c> a <paramref name="metodo"/>: desde su nombre hasta su punto y coma.</summary>
    private static string LlamadaA(string program, string metodo)
    {
        var inicio = program.IndexOf(metodo, StringComparison.Ordinal);
        inicio.Should().BeGreaterThan(-1, $"control positivo: Program.cs llama a {metodo}");

        var fin = program.IndexOf(';', inicio);
        fin.Should().BeGreaterThan(inicio, "control positivo: la llamada termina en punto y coma");

        return program[inicio..(fin + 1)];
    }

    private static string Program() => Texto(Path.Combine(RaizDelRepositorio(), "src", "CaeManager.Web", "Program.cs"));

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
