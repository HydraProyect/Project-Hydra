using System.Text.RegularExpressions;
using CaeManager.Architecture.Tests.Soporte;
using FluentAssertions;
using Xunit;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// Todo listado que monta <c>BarraFiltros</c> ofrece filtros guardados: le pasa la lista
/// (<c>FiltrosGuardados</c>), «Guardar filtro» (<c>OnGuardarFiltro</c>) y el gesto de aplicar uno
/// (<c>OnAplicarFiltroGuardado</c>). Hasta el cierre de listados N1 solo lo hacían Trabajadores,
/// Clientes empresariales y Documentos; ahora son nueve, y los seis nuevos lo hacen con la pieza
/// compartida <c>FiltrosGuardadosDeListado</c> y una lista blanca <c>ParametrosDeVista</c>.
///
/// <para>
/// Lo que esta prueba NO mira: que guardar y aplicar funcionen (eso es de bUnit,
/// <c>FiltrosGuardadosEnListadosTests</c>). Solo fija que ninguna barra de filtros nueva nazca sin ellos.
/// </para>
/// </summary>
public class ListadosConBarraFiltrosOfrecenFiltrosGuardadosTests
{
    private const string CarpetaDeFeatures = "src/CaeManager.Web/Features/";

    /// <summary>
    /// Listados que pueden montar <c>BarraFiltros</c> sin filtros guardados. Visitas entra después: su
    /// cabecera se está rehaciendo en otra línea de trabajo y hoy ni siquiera usa <c>BarraFiltros</c>.
    /// Cuando los tenga, se quita de aquí (la última prueba lo exige).
    /// </summary>
    private static readonly string[] AunSinFiltrosGuardados =
    [
        CarpetaDeFeatures + "Visitas/Pages/Visitas.razor",
    ];

    /// <summary>Los nueve que ya los tienen: control positivo de que el recorrido ve lo que dice ver.</summary>
    private static readonly string[] ListadosConocidos =
    [
        CarpetaDeFeatures + "Centros/Pages/Centros.razor",
        CarpetaDeFeatures + "Clientes/Pages/Clientes.razor",
        CarpetaDeFeatures + "Documentos/Pages/Documentos.razor",
        CarpetaDeFeatures + "Empresas/Pages/Empresas.razor",
        CarpetaDeFeatures + "Gestiones/Pages/Gestiones.razor",
        CarpetaDeFeatures + "Proyectos/Pages/Proyectos.razor",
        CarpetaDeFeatures + "Subcontratas/Pages/Subcontratas.razor",
        CarpetaDeFeatures + "Trabajadores/Pages/Trabajadores.razor",
        CarpetaDeFeatures + "Vehiculos/Pages/Vehiculos.razor",
    ];

    /// <summary>Los seis que usan la pieza compartida.</summary>
    private static readonly string[] ListadosConLaPiezaCompartida =
    [
        CarpetaDeFeatures + "Centros/Pages/Centros.razor",
        CarpetaDeFeatures + "Empresas/Pages/Empresas.razor",
        CarpetaDeFeatures + "Gestiones/Pages/Gestiones.razor",
        CarpetaDeFeatures + "Proyectos/Pages/Proyectos.razor",
        CarpetaDeFeatures + "Subcontratas/Pages/Subcontratas.razor",
        CarpetaDeFeatures + "Vehiculos/Pages/Vehiculos.razor",
    ];

    private static readonly string[] AtributosExigidos = ["FiltrosGuardados", "OnGuardarFiltro", "OnAplicarFiltroGuardado"];

    /// <summary>Ruta relativa → etiquetas de apertura de <c>BarraFiltros</c> (sin comentarios) de cada .razor de Features.</summary>
    private static Dictionary<string, List<string>> BarrasPorFichero() => Aperturas("BarraFiltros");

    private static Dictionary<string, List<string>> Aperturas(string componente) =>
        FuentesDeSrc.Archivos()
            .Where(a => a.EndsWith(".razor", StringComparison.OrdinalIgnoreCase))
            .Select(a => (Ruta: FuentesDeSrc.Relativa(a), Archivo: a))
            .Where(f => f.Ruta.StartsWith(CarpetaDeFeatures, StringComparison.Ordinal))
            .Select(f => (f.Ruta, Aperturas: MarcadoRazor
                .Aperturas(LimpiadorDeComentarios.Quitar(File.ReadAllText(f.Archivo), razor: true), componente)
                .Select(a => a.Texto).ToList()))
            .Where(f => f.Aperturas.Count > 0)
            .ToDictionary(f => f.Ruta, f => f.Aperturas, StringComparer.Ordinal);

    private static bool Pasa(string apertura, string atributo) =>
        Regex.IsMatch(apertura, $@"\s{Regex.Escape(atributo)}\s*=");

    private static IEnumerable<string> Faltas(Dictionary<string, List<string>> barras) =>
        from fichero in barras
        where !AunSinFiltrosGuardados.Contains(fichero.Key)
        from apertura in fichero.Value
        from atributo in AtributosExigidos
        where !Pasa(apertura, atributo)
        select $"{fichero.Key}: <BarraFiltros> sin {atributo}=";

    [Fact]
    public void El_recorrido_ve_los_nueve_listados_con_barra_de_filtros()
    {
        var barras = BarrasPorFichero();

        barras.Keys.Should().Contain(ListadosConocidos,
            "si el recorrido deja de ver un listado que monta <BarraFiltros>, la prueba siguiente da verde por vacío");
        barras.Count.Should().BeGreaterThan(3, "antes del cierre N1 solo tres listados tenían filtros guardados; la lista tiene que haber crecido");
        barras.Values.Should().OnlyContain(aperturas => aperturas.All(a => a.StartsWith("<BarraFiltros", StringComparison.Ordinal) && a.EndsWith('>')),
            "cada apertura es la etiqueta entera, con sus atributos: sobre ella se buscan los tres exigidos");
    }

    [Fact]
    public void Todo_listado_con_barra_de_filtros_le_pasa_los_filtros_guardados_guardar_y_aplicar()
    {
        Faltas(BarrasPorFichero()).Should().BeEmpty(
            "un listado con <BarraFiltros> ofrece filtros guardados: con la pieza FiltrosGuardadosDeListado bastan una conexión, " +
            "una lista blanca ParametrosDeVista y un método que aplique la vista (ver Empresas.razor y Empresas.razor.cs)");
    }

    /// <summary>La prueba anterior es sensible: sin uno de los atributos, la barra aparece entre las faltas.</summary>
    [Theory]
    [InlineData("FiltrosGuardados")]
    [InlineData("OnGuardarFiltro")]
    [InlineData("OnAplicarFiltroGuardado")]
    public void Una_barra_sin_uno_de_los_tres_atributos_se_detecta(string atributo)
    {
        const string fichero = CarpetaDeFeatures + "Empresas/Pages/Empresas.razor";
        var apertura = BarrasPorFichero()[fichero].Single();
        Pasa(apertura, atributo).Should().BeTrue("control: la barra real lo pasa");
        var sinAtributo = Regex.Replace(apertura, $@"\s{Regex.Escape(atributo)}\s*=\s*""[^""]*""", string.Empty);
        sinAtributo.Should().NotBe(apertura, "la mutación tiene que haber quitado algo");

        var faltas = Faltas(new Dictionary<string, List<string>> { [fichero] = [sinAtributo] });

        faltas.Should().Equal($"{fichero}: <BarraFiltros> sin {atributo}=");
    }

    [Fact]
    public void Los_seis_listados_de_la_pieza_compartida_declaran_su_lista_blanca_y_se_la_pasan()
    {
        var piezas = Aperturas("FiltrosGuardadosDeListado");

        piezas.Keys.Should().BeEquivalentTo(ListadosConLaPiezaCompartida);
        foreach (var (ruta, aperturas) in piezas)
        {
            aperturas.Should().ContainSingle($"{ruta} monta la pieza una vez")
                .Which.Should().Contain("ParametrosDeVista=\"ParametrosDeVista\"", $"{ruta} le pasa su lista blanca, no una lista escrita en el marcado");

            var codigo = File.ReadAllText(Path.Combine(FuentesDeSrc.RaizDelRepositorio(), ruta + ".cs"));
            codigo.Should().MatchRegex(@"public static readonly IReadOnlyList<string> ParametrosDeVista\s*=\s*\[",
                $"{ruta}.cs declara la lista blanca con ese nombre: otras líneas de trabajo le añaden entradas");
            Regex.Match(codigo, @"ParametrosDeVista\s*=\s*\[([^\]]*)\]").Groups[1].Value
                .Should().NotContainAny(["\"accion\"", "\"Accion\"", "\"Nombre\"", "\"ClienteId\"", "\"EmpresaId\"", "\"CentroId\""],
                    "ni la acción pedida por URL ni las precargas de un alta son la vista");
        }
    }

    [Fact]
    public void Un_listado_de_la_lista_de_espera_que_ya_ofrece_filtros_guardados_sale_de_ella()
    {
        var barras = BarrasPorFichero();

        var yaCumplen = AunSinFiltrosGuardados
            .Where(barras.ContainsKey)
            .Where(ruta => barras[ruta].All(a => AtributosExigidos.All(atributo => Pasa(a, atributo))))
            .ToList();

        yaCumplen.Should().BeEmpty("quien ya pasa los filtros guardados a su barra no necesita la excepción: quítalo de AunSinFiltrosGuardados");
    }
}
