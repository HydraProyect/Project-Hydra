using System.Text.RegularExpressions;
using CaeManager.Application.Configuracion;
using CaeManager.Architecture.Tests.Soporte;
using FluentAssertions;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// Todo listado con <c>&lt;BarraFiltros&gt;</c> recuerda su vista (decisión del 2026-10-08): monta
/// <c>&lt;VistaRecordadaDeListado&gt;</c> con su pantalla, su lista blanca, su parámetro de búsqueda y su
/// manera de aplicar una vista, y pasa la misma conexión a la barra, que es la que pinta «Restablecer
/// vista». Un listado nuevo con barra de filtros y sin la pieza se queda en rojo.
///
/// <para>
/// Qué mira: el marcado SIN comentarios (ni <c>@* *@</c> ni <c>&lt;!-- --&gt;</c>: una pieza comentada no
/// se monta) y los atributos de la etiqueta <c>&lt;VistaRecordadaDeListado&gt;</c> en concreto. El mismo
/// texto en otra etiqueta no cuenta: <c>ParametrosDeVista="ParametrosDeVista"</c> lo lleva también
/// <c>&lt;FiltrosGuardadosDeListado&gt;</c>, y buscarlo en el fichero entero daba verde con la pieza sin
/// lista blanca. Qué NO mira: que recordar y restaurar funcionen (eso es de bUnit,
/// <c>VistaRecordadaDeListadoTests</c> y <c>VistaRecordadaEnListadosTests</c>).
/// </para>
///
/// <para>
/// Visitas no tiene <c>&lt;BarraFiltros&gt;</c> todavía (su cabecera se está reescribiendo): cuando la
/// tenga entrará sola en la medida. Los nueve listados con barra montan ya la pieza: no queda
/// ninguno congelado.
/// </para>
/// </summary>
public class ListadosConBarraFiltrosRecuerdanLaVistaTests
{
    private const string Paginas = "src/CaeManager.Web/Features/";
    private const string Pieza = "VistaRecordadaDeListado";
    private const string Barra = "BarraFiltros";

    /// <summary>Lo que la pieza necesita de la página, además de la conexión: sin uno de ellos no recuerda o no restaura.</summary>
    private static readonly string[] AtributosExigidos =
        ["Pantalla", "ParametrosDeVista", "ParametroDeBusqueda", "OnAplicar", "AlCambiar"];

    /// <summary>El marcado que Razor monta de verdad: sin comentarios de Razor ni de HTML.</summary>
    private static string SinComentarios(string marcado) =>
        Regex.Replace(LimpiadorDeComentarios.Quitar(marcado, razor: true), "<!--.*?-->", string.Empty, RegexOptions.Singleline);

    /// <summary>Ruta → marcado sin comentarios de cada página con <c>&lt;BarraFiltros&gt;</c>.</summary>
    private static Dictionary<string, string> ListadosConBarra() =>
        MarcadoRazor.LeerRazorDeLaWeb()
            .Where(f => f.Ruta.StartsWith(Paginas, StringComparison.Ordinal))
            .Select(f => (f.Ruta, Marcado: SinComentarios(f.Contenido)))
            .Where(f => MarcadoRazor.Aperturas(f.Marcado, Barra).Count > 0)
            .ToDictionary(f => f.Ruta, f => f.Marcado, StringComparer.Ordinal);

    /// <summary>El valor de ese atributo en la etiqueta de apertura, o <c>null</c> si no lo lleva.</summary>
    private static string? Atributo(string apertura, string nombre)
    {
        var comienzo = Regex.Match(apertura, $@"\s{Regex.Escape(nombre)}\s*=\s*""");
        return comienzo.Success ? MarcadoRazor.ValorDeComillas(apertura, comienzo.Index + comienzo.Length) : null;
    }

    /// <summary>La constante de <see cref="PantallasConVistaRecordada"/> que nombra ese valor de <c>Pantalla</c>, resuelta a su texto.</summary>
    private static string? PantallaDeclarada(string? valor)
    {
        var constante = Regex.Match(valor ?? string.Empty, $@"^@(?:[\w.]+\.)?{nameof(PantallasConVistaRecordada)}\.(\w+)$");
        return constante.Success
            ? typeof(PantallasConVistaRecordada).GetField(constante.Groups[1].Value)?.GetRawConstantValue() as string
            : null;
    }

    /// <summary>Por qué ese marcado (ya sin comentarios) no recuerda la vista; vacío si cumple.</summary>
    private static List<string> Defectos(string marcado)
    {
        var piezas = MarcadoRazor.Aperturas(marcado, Pieza);
        if (piezas.Count != 1)
            return [$"monta <{Pieza}> {piezas.Count} veces, y debe montarla una"];

        var pieza = piezas[0].Texto;
        var defectos = AtributosExigidos
            .Where(a => string.IsNullOrWhiteSpace(Atributo(pieza, a)))
            .Select(a => $"<{Pieza}> sin {a}=")
            .ToList();

        var pantalla = Atributo(pieza, "Pantalla");
        if (!string.IsNullOrWhiteSpace(pantalla)
            && (PantallaDeclarada(pantalla) is not { } declarada || !PantallasConVistaRecordada.Admitidas.Contains(declarada)))
            defectos.Add($"<{Pieza}> con Pantalla=\"{pantalla}\", que no es una constante admitida de {nameof(PantallasConVistaRecordada)}");

        var conexion = Atributo(pieza, "Conexion");
        if (string.IsNullOrWhiteSpace(conexion))
            defectos.Add($"<{Pieza}> sin Conexion=");
        else if (!MarcadoRazor.Aperturas(marcado, Barra).Any(b => Atributo(b.Texto, "VistaRecordada") == conexion))
            defectos.Add($"ninguna <{Barra}> recibe VistaRecordada=\"{conexion}\", la conexión de la pieza: nadie pinta «Restablecer vista»");

        return defectos;
    }

    private static IEnumerable<string> Faltas(Dictionary<string, string> listados) =>
        from listado in listados
        from defecto in Defectos(listado.Value)
        select $"{listado.Key}: {defecto}";

    [Fact]
    public void El_recorrido_ve_los_nueve_listados_con_barra_de_filtros()
    {
        // Control positivo: si el recorrido no viera las páginas, «ninguno sin la pieza» valdría por vacío.
        ListadosConBarra().Keys.Select(Path.GetFileNameWithoutExtension).Should().BeEquivalentTo(
            ["Trabajadores", "Empresas", "Clientes", "Documentos", "Centros", "Subcontratas", "Vehiculos", "Proyectos", "Gestiones"]);
    }

    [Fact]
    public void Todo_listado_con_barra_de_filtros_monta_la_vista_recordada_con_sus_atributos()
    {
        // No vale por vacío: el control positivo de arriba fija que el recorrido ve los nueve.
        Faltas(ListadosConBarra()).Should().BeEmpty(
            "un listado con <BarraFiltros> monta una vez <VistaRecordadaDeListado Conexion=\"_vistaRecordada\" Pantalla=\"@PantallasConVistaRecordada.…\" "
            + "ParametrosDeVista=\"ParametrosDeVista\" ParametroDeBusqueda=\"q\" OnAplicar=\"…\" AlCambiar=\"StateHasChanged\" /> "
            + "y pasa VistaRecordada=\"_vistaRecordada\" a la barra (ver Empresas.razor)");
    }

    /// <summary>
    /// Dos listados con la misma pantalla se pisarían la vista: lo recordado en uno se restauraría en el otro,
    /// con parámetros que no son suyos.
    /// </summary>
    [Fact]
    public void Cada_listado_recuerda_bajo_una_pantalla_admitida_y_distinta()
    {
        var pantallas = ListadosConBarra().ToDictionary(
            l => l.Key,
            l => PantallaDeclarada(Atributo(MarcadoRazor.Aperturas(l.Value, Pieza).Single().Texto, "Pantalla")));

        pantallas.Should().HaveCount(9, "control: una pantalla por cada uno de los nueve listados");
        pantallas.Values.Should().OnlyContain(p => p != null && PantallasConVistaRecordada.Admitidas.Contains(p));
        pantallas.Values.Should().OnlyHaveUniqueItems();
        pantallas.Should().OnlyContain(p => Path.GetFileNameWithoutExtension(p.Key) == p.Value,
            "hoy cada pantalla se llama como su página: si un listado deja de cumplirlo a propósito, se cambia esta línea");
    }

    // ------------------------------------------------------------ sensibilidad

    private static string Pagina(string nombre)
    {
        var marcado = ListadosConBarra()[$"{Paginas}{nombre}/Pages/{nombre}.razor"];
        Defectos(marcado).Should().BeEmpty("control: la página real cumple");
        return marcado;
    }

    private static string AperturaDeLaPieza(string marcado) => MarcadoRazor.Aperturas(marcado, Pieza).Single().Texto;

    /// <summary>El marcado con su etiqueta de la pieza sustituida. La mutación tiene que cambiar algo, o no prueba nada.</summary>
    private static string ConLaPieza(string marcado, Func<string, string> cambio)
    {
        var apertura = AperturaDeLaPieza(marcado);
        var mutada = cambio(apertura);
        mutada.Should().NotBe(apertura, "la mutación tiene que haber cambiado la etiqueta");
        return marcado.Replace(apertura, mutada, StringComparison.Ordinal);
    }

    private static string SinAtributo(string apertura, string atributo) =>
        Regex.Replace(apertura, $@"\s{Regex.Escape(atributo)}\s*=\s*""[^""]*""", string.Empty);

    [Theory]
    [InlineData("@* ", " *@")]
    [InlineData("<!-- ", " -->")]
    public void Una_pieza_dentro_de_un_comentario_no_cuenta(string abre, string cierra)
    {
        var crudo = MarcadoRazor.LeerRazorDeLaWeb().Single(f => f.Ruta == $"{Paginas}Vehiculos/Pages/Vehiculos.razor").Contenido;
        var inicio = crudo.IndexOf($"<{Pieza} ", StringComparison.Ordinal);
        var fin = crudo.IndexOf("/>", inicio, StringComparison.Ordinal) + 2;
        inicio.Should().BeGreaterThan(0, "control: la página real lleva la pieza");
        var comentada = crudo[..inicio] + abre + crudo[inicio..fin] + cierra + crudo[fin..];

        Defectos(SinComentarios(crudo)).Should().BeEmpty("control: sin comentar, cumple");
        Defectos(SinComentarios(comentada)).Should().Equal($"monta <{Pieza}> 0 veces, y debe montarla una");
    }

    /// <summary>
    /// Proyectos pasa el mismo <c>ParametrosDeVista="ParametrosDeVista"</c> a sus filtros guardados: que esté
    /// en otra etiqueta no le vale a la pieza.
    /// </summary>
    [Fact]
    public void La_lista_blanca_de_otra_etiqueta_no_vale_por_la_de_la_pieza()
    {
        var proyectos = Pagina("Proyectos");
        var sinLista = ConLaPieza(proyectos, a => SinAtributo(a, "ParametrosDeVista"));

        sinLista.Should().Contain("ParametrosDeVista=\"ParametrosDeVista\"", "control: los filtros guardados siguen llevando la suya");
        Defectos(sinLista).Should().Equal($"<{Pieza}> sin ParametrosDeVista=");
    }

    [Theory]
    [InlineData("Pantalla")]
    [InlineData("ParametrosDeVista")]
    [InlineData("ParametroDeBusqueda")]
    [InlineData("OnAplicar")]
    [InlineData("AlCambiar")]
    [InlineData("Conexion")]
    public void Una_pieza_sin_uno_de_sus_atributos_se_detecta(string atributo)
    {
        var sinAtributo = ConLaPieza(Pagina("Vehiculos"), a => SinAtributo(a, atributo));

        Defectos(sinAtributo).Should().Equal($"<{Pieza}> sin {atributo}=");
    }

    [Fact]
    public void Una_pantalla_que_no_es_una_constante_admitida_se_detecta()
    {
        var vehiculos = Pagina("Vehiculos");

        Defectos(ConLaPieza(vehiculos, a => a.Replace($"{nameof(PantallasConVistaRecordada)}.Vehiculos", $"{nameof(PantallasConVistaRecordada)}.Inventada")))
            .Should().ContainSingle().Which.Should().Contain("no es una constante admitida");
        Defectos(ConLaPieza(vehiculos, a => Regex.Replace(a, @"Pantalla=""[^""]*""", "Pantalla=\"Vehiculos\"")))
            .Should().ContainSingle("un literal no es la constante: un cambio de nombre no lo arrastraría").Which.Should().Contain("no es una constante admitida");
    }

    [Fact]
    public void Una_barra_que_no_recibe_la_conexion_de_la_pieza_o_una_pieza_repetida_se_detectan()
    {
        var vehiculos = Pagina("Vehiculos");
        var sinConexionEnLaBarra = vehiculos.Replace("VistaRecordada=\"_vistaRecordada\"", string.Empty);
        sinConexionEnLaBarra.Should().NotBe(vehiculos, "la mutación tiene que haber quitado algo");
        var apertura = AperturaDeLaPieza(vehiculos);

        Defectos(sinConexionEnLaBarra).Should().ContainSingle().Which.Should().Contain("nadie pinta «Restablecer vista»");
        Defectos(vehiculos.Replace(apertura, apertura + apertura, StringComparison.Ordinal))
            .Should().Equal($"monta <{Pieza}> 2 veces, y debe montarla una");
    }
}
