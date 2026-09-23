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
/// <c>&lt;Boton&gt;</c> (aunque su etiqueta caiga en otra línea) con «+ Nuevo/Nueva …», «Redactar», «Resolver», «Reabrir», «Enviar reclamación»
/// y «Añadir contacto», también cuando la etiqueta sale de un <c>IStringLocalizer</c>
/// (<c>@Textos["Clave"]</c> se resuelve contra el <c>.resx</c> neutral de su Feature y los
/// comunes de <c>Web/Recursos</c>). NO cubre los ítems de menú de fila (Editar, Eliminar),
/// ni formularios ni checkboxes de selección múltiple, ni ningún botón con otro
/// texto: es una alarma sobre la familia que se midió, no una garantía sobre
/// toda escritura de la interfaz. La ausencia de aviso aquí no significa que no
/// quede otro botón de escritura visible para Consulta.
/// </para>
/// </summary>
public class DisparadoresDeEscrituraSoloParaRolesConEscrituraTests
{
    // Un <Boton> entero, aunque su etiqueta caiga en otra línea que el atributo OnClick; el
    // aserto negativo impide que un <Boton .../> autocerrado se trague al siguiente.
    private static readonly Regex ElementoBoton = new(
        @"<Boton\b(?:(?!<Boton\b)[\s\S])*?</Boton>",
        RegexOptions.Compiled);

    private static readonly Regex Disparador = new(
        @"\+ Nuev[oa]\b|>\s*(Redactar|Resolver|Reabrir|Añadir contacto|Enviar reclamación[^<]*)\s*</Boton>$",
        RegexOptions.Compiled);

    [Fact]
    public void Todo_disparador_de_creacion_o_respuesta_esta_dentro_de_SoloConEscritura()
    {
        var (sitios, sinEnvolver, sinResolver) = Medir(LeerRazor(), TextosNeutralesPorRazor());

        // Una clave sin valor en el .resx dejaría su botón fuera de la vista del detector
        // sin avisar: el recurso ausente también es un defecto visible en pantalla.
        sinResolver.Should().BeEmpty(
            "cada @Textos[\"Clave\"] dentro de un <Boton> debe resolverse en el .resx neutral de su Feature o en TextosComunes");

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
            "<Boton OnClick=\"D\">Redactar</Boton>\n" +
            "<Boton OnClick=\"E\"\n" +
            "       Tamano=\"P\">\n" +
            "    Añadir contacto\n" +
            "</Boton>\n" +
            "<SoloConEscritura><Boton OnClick=\"F\"\n" +
            "    Tamano=\"P\">\n" +
            "    Añadir contacto\n" +
            "</Boton></SoloConEscritura>\n";

        var (sitios, sinEnvolver, _) = Medir([("fuente-falsa.razor", fuente)]);

        sitios.Should().Be(6);
        sinEnvolver.Should().BeEquivalentTo(
            "fuente-falsa.razor:1 <Boton OnClick=\"A\">+ Nuevo cliente</Boton>",
            "fuente-falsa.razor:9 <Boton OnClick=\"D\">Redactar</Boton>",
            "fuente-falsa.razor:10 <Boton OnClick=\"E\" Tamano=\"P\"> Añadir contacto </Boton>");
    }

    [Fact]
    public void El_detector_ve_la_etiqueta_localizada_por_su_valor_en_el_resx()
    {
        // Tras migrar a recursos el fuente ya no dice «+ Nuevo …»: lo dice el .resx.
        const string fuente =
            "<Boton OnClick=\"A\">@Textos[\"BotonCrear\"]</Boton>\n" +
            "<SoloConEscritura><Boton OnClick=\"B\">@Textos[\"BotonCrear\"]</Boton></SoloConEscritura>\n" +
            "<Boton OnClick=\"C\">\n    @Textos[\"BotonContacto\"]\n</Boton>\n" +
            "<Boton OnClick=\"D\">@Textos[\"BotonCancelar\"]</Boton>\n" +
            "<Boton OnClick=\"E\">@Textos[\"ClaveSinValor\"]</Boton>\n";
        var textos = new Dictionary<string, string>
        {
            ["BotonCrear"] = "+ Nuevo tipo",
            ["BotonContacto"] = "Añadir contacto",
            ["BotonCancelar"] = "Cancelar",
        };

        var (sitios, sinEnvolver, sinResolver) = Medir([("fuente-falsa.razor", fuente)], _ => textos);

        sitios.Should().Be(3);
        sinEnvolver.Should().BeEquivalentTo(
            "fuente-falsa.razor:1 <Boton OnClick=\"A\">@Textos[\"BotonCrear\"]</Boton>",
            "fuente-falsa.razor:3 <Boton OnClick=\"C\"> @Textos[\"BotonContacto\"] </Boton>");

        // La clave sin valor no se ignora en silencio: se informa.
        sinResolver.Should().BeEquivalentTo("fuente-falsa.razor:7 ClaveSinValor");

        // Sin resolver, el mismo fuente no enseña ningún disparador: la ceguera que se corrige.
        Medir([("fuente-falsa.razor", fuente)]).Sitios.Should().Be(0);
    }

    // Una etiqueta localizada, «@Textos["BotonNuevoTipo"]»: se sustituye por su valor del
    // .resx neutral antes de buscar el disparador. Sin esto, cada Feature migrada a recursos
    // dejaba sus «+ Nuevo …» fuera de la vista del detector (29 sitios al migrar TiposDocumento).
    private static readonly Regex LlamadaLocalizador = new(
        @"@[A-Za-z_]\w*\[""(?<clave>[A-Za-z_]\w*)""[^\]]*\]",
        RegexOptions.Compiled);

    private static (int Sitios, List<string> SinEnvolver, List<string> SinResolver) Medir(
        IEnumerable<(string Ruta, string Contenido)> ficheros,
        Func<string, IReadOnlyDictionary<string, string>>? textosDe = null)
    {
        var sitios = 0;
        var sinEnvolver = new List<string>();
        var sinResolver = new List<string>();

        foreach (var (ruta, contenido) in ficheros)
        {
            var textos = textosDe?.Invoke(ruta);
            foreach (Match boton in ElementoBoton.Matches(contenido))
            {
                var antes = contenido[..boton.Index];
                var etiqueta = textos is null
                    ? boton.Value
                    : LlamadaLocalizador.Replace(boton.Value, m =>
                    {
                        var clave = m.Groups["clave"].Value;
                        if (textos.TryGetValue(clave, out var valor)) return valor;
                        var lineaClave = contenido[..(boton.Index + m.Index)].Count(c => c == '\n') + 1;
                        sinResolver.Add($"{Path.GetFileName(ruta)}:{lineaClave} {clave}");
                        return m.Value;
                    });
                if (!Disparador.IsMatch(etiqueta)) continue;
                sitios++;

                // Envuelto si al empezar el botón hay más <SoloConEscritura> abiertos que cerrados.
                var abiertos = Regex.Matches(antes, "<SoloConEscritura>").Count
                               - Regex.Matches(antes, "</SoloConEscritura>").Count;
                if (abiertos > 0) continue;

                var linea = antes.Count(c => c == '\n') + 1;
                var resumen = Regex.Replace(boton.Value, @"\s+", " ").Trim();
                sinEnvolver.Add($"{Path.GetFileName(ruta)}:{linea} {resumen}");
            }
        }

        return (sitios, sinEnvolver, sinResolver);
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

    /// <summary>
    /// Textos de los <c>.resx</c> neutrales que puede usar un <c>.razor</c>: los de su
    /// Feature (o carpeta de Components) y los comunes de <c>Web/Recursos</c>. Solo los
    /// neutrales (un único punto en el nombre): el catalán no decide qué es un disparador.
    /// </summary>
    private static Func<string, IReadOnlyDictionary<string, string>> TextosNeutralesPorRazor()
    {
        var web = Path.Combine(RaizDelRepositorio(), "src", "CaeManager.Web");
        var comunes = LeerResx(Path.Combine(web, "Recursos"), SearchOption.TopDirectoryOnly);
        var cache = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

        return ruta =>
        {
            var partes = Path.GetRelativePath(web, ruta).Split(Path.DirectorySeparatorChar);
            var raiz = partes.Length > 2 && partes[0] is "Features" or "Components"
                ? Path.Combine(web, partes[0], partes[1])
                : Path.GetDirectoryName(ruta)!;
            if (cache.TryGetValue(raiz, out var textos)) return textos;

            var propios = new Dictionary<string, string>(comunes, StringComparer.Ordinal);
            foreach (var (clave, valor) in LeerResx(raiz, SearchOption.AllDirectories))
                propios[clave] = valor;
            return cache[raiz] = propios;
        };
    }

    private static Dictionary<string, string> LeerResx(string carpeta, SearchOption busqueda)
    {
        var textos = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!Directory.Exists(carpeta)) return textos;

        foreach (var resx in Directory.EnumerateFiles(carpeta, "*.resx", busqueda)
                     .Where(f => Path.GetFileName(f).Count(c => c == '.') == 1))
        {
            foreach (var data in System.Xml.Linq.XDocument.Load(resx).Root!.Elements("data"))
                textos[(string)data.Attribute("name")!] = (string?)data.Element("value") ?? string.Empty;
        }

        return textos;
    }

    private static string RaizDelRepositorio()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "CaeManager.slnx")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? AppContext.BaseDirectory;
    }
}
