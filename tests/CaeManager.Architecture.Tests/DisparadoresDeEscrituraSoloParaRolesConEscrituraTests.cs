using System.Text.RegularExpressions;
using FluentAssertions;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// Un botón de crear que se ve habilitado y falla al pulsarlo con «Tu rol no
/// permite crear, editar ni eliminar datos» es una pantalla que miente. Se midió
/// en la demo a dirección: una Dirección CAE operando un Tenant propietario como
/// Consulta delegada veía «+ Nueva empresa», «+ Nuevo cliente», «Redactar» y
/// «Resolver» activos. Los disparadores de escritura se envuelven en
/// <c>SoloConEscritura</c>, que los pinta solo para los roles de
/// <c>Roles.ConEscrituraCsv</c>.
///
/// <para>
/// <b>Contrato efectivo, más estrecho que el nombre.</b> Solo mira, sobre el
/// fuente Razor, los disparadores de <i>creación y respuesta principales</i>:
/// <c>&lt;Boton&gt;</c> con «+ Nuevo/Nueva …», «Redactar», «Resolver», «Reabrir»
/// y «Añadir contacto». NO cubre los ítems de menú de fila (Editar, Eliminar),
/// ni formularios ni checkboxes de selección múltiple, ni ningún botón con otro
/// texto: es una alarma sobre la familia que se midió, no una garantía sobre
/// toda escritura de la interfaz. La ausencia de aviso aquí no significa que no
/// quede otro botón de escritura visible para Consulta.
/// </para>
/// </summary>
public class DisparadoresDeEscrituraSoloParaRolesConEscrituraTests
{
    private static readonly Regex Disparador = new(
        @"<Boton\b[^\n]*(\+ Nuev[oa]\b|>\s*Redactar\s*<|>\s*Resolver\s*<|>\s*Reabrir\s*<|Añadir contacto</Boton>)",
        RegexOptions.Compiled);

    [Fact]
    public void Todo_disparador_de_creacion_o_respuesta_esta_dentro_de_SoloConEscritura()
    {
        var (sitios, sinEnvolver) = Medir(LeerRazor());

        // Control positivo: si el detector no viera nada, o casi nada, «no hay
        // ninguno sin envolver» se cumpliría por vacío.
        sitios.Should().BeGreaterThan(30,
            "el detector tiene que ver los disparadores conocidos (≈45 al escribirlo); si baja de golpe, dejó de mirar");

        sinEnvolver.Should().BeEmpty(
            "un botón que el rol de Consulta ve habilitado y no puede ejecutar es el defecto que este trinquete vigila; " +
            "envuélvelo en <SoloConEscritura>");
    }

    [Fact]
    public void El_detector_distingue_un_disparador_envuelto_de_uno_suelto()
    {
        // El instrumento probado contra sí mismo, con fuente inventada: uno suelto,
        // uno envuelto en la misma línea y uno envuelto en varias.
        const string fuente =
            "<Boton OnClick=\"A\">+ Nuevo cliente</Boton>\n" +
            "<SoloConEscritura><Boton OnClick=\"B\">+ Nueva empresa</Boton></SoloConEscritura>\n" +
            "<SoloConEscritura>\n" +
            "   @if (x)\n" +
            "   {\n" +
            "       <Boton OnClick=\"C\">Resolver</Boton>\n" +
            "   }\n" +
            "</SoloConEscritura>\n" +
            "<Boton OnClick=\"D\">Redactar</Boton>\n";

        var (sitios, sinEnvolver) = Medir([("fuente-falsa.razor", fuente)]);

        sitios.Should().Be(4);
        sinEnvolver.Should().BeEquivalentTo(
            "fuente-falsa.razor:1 <Boton OnClick=\"A\">+ Nuevo cliente</Boton>",
            "fuente-falsa.razor:9 <Boton OnClick=\"D\">Redactar</Boton>");
    }

    private static (int Sitios, List<string> SinEnvolver) Medir(IEnumerable<(string Ruta, string Contenido)> ficheros)
    {
        var sitios = 0;
        var sinEnvolver = new List<string>();

        foreach (var (ruta, contenido) in ficheros)
        {
            var lineas = contenido.Split('\n');
            var profundidad = 0;

            for (var i = 0; i < lineas.Length; i++)
            {
                var linea = lineas[i];
                var abiertasAntes = profundidad;
                profundidad += Regex.Matches(linea, "<SoloConEscritura>").Count
                               - Regex.Matches(linea, "</SoloConEscritura>").Count;

                if (!Disparador.IsMatch(linea)) continue;
                sitios++;

                // Envuelto si hay un <SoloConEscritura> abierto al empezar la línea, o
                // se abre en ella misma antes del botón.
                var posicionBoton = linea.IndexOf("<Boton", StringComparison.Ordinal);
                var envueltoEnLaLinea = linea[..posicionBoton].Contains("<SoloConEscritura>", StringComparison.Ordinal);
                if (abiertasAntes > 0 || envueltoEnLaLinea) continue;

                sinEnvolver.Add($"{Path.GetFileName(ruta)}:{i + 1} {linea.Trim().TrimEnd('\r')}");
            }
        }

        return (sitios, sinEnvolver);
    }

    private static IEnumerable<(string Ruta, string Contenido)> LeerRazor()
    {
        var web = Path.Combine(RaizDelRepositorio(), "src", "CaeManager.Web");
        var sep = Path.DirectorySeparatorChar;

        return Directory
            .EnumerateFiles(web, "*.razor", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{sep}obj{sep}", StringComparison.Ordinal))
            .Select(f => (Ruta: f, Contenido: File.ReadAllText(f)));
    }

    private static string RaizDelRepositorio()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "CaeManager.slnx")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? AppContext.BaseDirectory;
    }
}
