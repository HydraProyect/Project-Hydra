using System.Text.RegularExpressions;
using CaeManager.Architecture.Tests.Soporte;
using FluentAssertions;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// PR #2185 (CampoFecha con máscara y calendario). Mientras el texto de un <c>CampoFecha</c> no es una fecha entera, el
/// campo emite <c>ValorChanged(null)</c> y <c>NoValidoChanged(true)</c>: el padre que guarda tiene null, y si no lee la
/// marca no distingue «sin fecha» (voluntario) de «fecha a medias». Tres rondas de revisión encontraron escritores que no la
/// leían (emisión, caducidad, verificaciones, inicio y cierre de Proyectos): la marca era opcional y cada consumidor tenía
/// que acordarse. Este trinquete la hace obligatoria.
///
/// <para>
/// <b>Qué mide.</b> Cada <c>&lt;CampoFecha&gt;</c> sin <c>NoValidoChanged</c>, y cada <c>&lt;CampoTexto Type="date"&gt;</c>
/// sin <c>FechaNoValidaChanged</c>, en los <c>.razor</c> de la Web, cuenta como <c>ruta :: CampoFecha sin marca = n</c>.
/// Un valor inerte (<c>_ =&gt; { }</c>, <c>default</c>) no cuenta como enlace. Lo que queda fuera es la lista congelada
/// <c>Congelados/CampoFecha-sin-marca-de-no-valido.txt</c>, y cada línea de esa lista lleva encima su motivo
/// (<c># motivo: …</c>): filtro de solo lectura, solo navega, o pendiente de migrar.
/// </para>
///
/// <para>
/// <b>Qué no ve.</b> Que el formulario <i>bloquee</i> el guardado con la marca, ni que el enlace llegue a un Command: lo
/// prueba el bUnit de cada pantalla (guardar sin salir del campo, recrear el campo). Tampoco ve un campo de fecha montado
/// por un componente hijo con otro nombre; <see cref="Todo_campo_de_fecha_del_sistema_de_diseno_esta_vigilado"/> obliga a
/// añadirlo aquí si aparece uno.
/// </para>
/// </summary>
public class CamposFechaEnlazanLaMarcaDeNoValidoTests
{
    private const string Lista = "CampoFecha-sin-marca-de-no-valido";
    private const string Simbolo = "CampoFecha sin marca";

    private static readonly string[] Propios =
    [
        "src/CaeManager.Web/Components/DesignSystem/CampoFecha.razor",
        "src/CaeManager.Web/Components/DesignSystem/CampoTexto.razor",
    ];

    private static readonly Regex TipoFecha = new(@"(?:^|\s)Type\s*=\s*""date""", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly string[] Inertes = ["", "_=>{}", "(_)=>{}", "default", "null", "EventCallback.Empty", "default(EventCallback<bool>)"];

    private const string Guia =
        "Un CampoFecha (o CampoTexto Type=\"date\") de un formulario que guarda enlaza NoValidoChanged (FechaNoValidaChanged) y " +
        "bloquea el guardado mientras sea true. Si migras un uso, baja o borra su línea en tests/CaeManager.Architecture.Tests/" +
        "Congelados/" + Lista + ".txt. Solo se añade una línea para un filtro de solo lectura o un campo que solo navega, con su " +
        "«# motivo: …» encima.";

    [Fact]
    public void Todo_campo_de_fecha_enlaza_la_marca_de_no_valido_salvo_los_congelados()
    {
        var razor = MarcadoRazor.LeerRazorDeLaWeb().ToList();

        // Control positivo: si el recorrido no viera campos de fecha, «ninguno sin marca» valdría por vacío.
        Enlazados(razor).Should().BeGreaterThan(20, "había más de 20 campos de fecha con la marca enlazada al escribirlo; si baja de golpe, dejó de mirar");

        ListaCongelada.Verificar(Lista, MedirSinMarca(razor), Guia).Should().BeNull();
    }

    [Fact]
    public void Cada_linea_de_la_lista_congelada_lleva_su_motivo()
    {
        var lineas = File.ReadAllLines(ListaCongelada.RutaDeLista(Lista));
        var sinMotivo = new List<string>();
        for (var i = 0; i < lineas.Length; i++)
        {
            var linea = lineas[i].Trim();
            if (linea.Length == 0 || linea.StartsWith('#')) continue;
            if (i == 0 || !lineas[i - 1].TrimStart().StartsWith("# motivo:", StringComparison.Ordinal))
                sinMotivo.Add(linea);
        }

        sinMotivo.Should().BeEmpty("un uso fuera de la regla sin motivo escrito es deuda que nadie sabe por qué existe");
    }

    /// <summary>
    /// La medida reconoce los campos de fecha por nombre: un componente de fecha nuevo en el sistema de diseño (un rango, un
    /// selector de mes) la dejaría ciega para las pantallas que lo usen.
    /// </summary>
    [Fact]
    public void Todo_campo_de_fecha_del_sistema_de_diseno_esta_vigilado()
    {
        var diseno = Path.Combine(MarcadoRazor.RaizDelRepositorio(), "src", "CaeManager.Web", "Components", "DesignSystem");
        var deFecha = Directory.EnumerateFiles(diseno, "*.razor")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(n => n!.StartsWith("Campo", StringComparison.Ordinal) && n.Contains("Fecha", StringComparison.Ordinal))
            .ToList();

        deFecha.Should().Contain("CampoFecha");
        deFecha.Should().BeEquivalentTo(["CampoFecha"], "un campo de fecha nuevo del kit se vigila en este trinquete");
    }

    /// <summary>Control positivo de la medida, con fuentes sintéticas y nunca con una entrada de la lista.</summary>
    [Fact]
    public void La_medida_ve_lo_que_vigila_y_no_lo_parecido()
    {
        static Dictionary<Ubicacion, int> Medir(string fuente) => MedirSinMarca([("a.razor", fuente)]);
        var sinMarca = new Ubicacion("a.razor", Simbolo);

        Medir("<CampoFecha Valor=\"f\" ValorChanged=\"v => f = v\" />").Should().ContainSingle().Which.Key.Should().Be(sinMarca);
        Medir("<CampoFecha Valor=\"f\" />\n<CampoFecha Valor=\"g\" />").Should().ContainSingle().Which.Value.Should().Be(2);
        Medir("<CampoTexto Type=\"date\" Valor=\"f\" />").Should().ContainSingle("CampoTexto Type=\"date\" delega en CampoFecha");
        Medir("<CampoTexto Type=\"DATE\" Valor=\"f\" />").Should().ContainSingle("el tipo no distingue mayúsculas");
        Medir("<CampoFecha Valor=\"f\" NoValidoChanged=\"_ => { }\" />").Should().ContainSingle("un manejador vacío no es un enlace");
        Medir("<CampoTexto Type=\"date\" NoValidoChanged=\"v => x = v\" />").Should().ContainSingle(
            "CampoTexto no tiene NoValidoChanged: su marca es FechaNoValidaChanged");

        Medir("<CampoFecha Valor=\"f\" NoValidoChanged=\"v => _noValido = v\" />").Should().BeEmpty();
        Medir("<CampoFecha Valor=\"f\"\n            NoValidoChanged=\"AlCambiarNoValido\" />").Should().BeEmpty("el atributo en otra línea cuenta");
        Medir("<CampoTexto Type=\"date\" FechaNoValidaChanged=\"v => _noValido = v\" />").Should().BeEmpty();
        Medir("<CampoTexto Type=\"text\" Valor=\"x\" />").Should().BeEmpty("un CampoTexto de texto no es de fecha");
        Medir("<CampoFechaRango Valor=\"x\" />").Should().BeEmpty("otro nombre no es CampoFecha");
        Medir("@* <CampoFecha Valor=\"f\" /> *@").Should().BeEmpty("un comentario de Razor no cuenta");
        MedirSinMarca([(Propios[1], "<CampoFecha Valor=\"f\" />")]).Should().BeEmpty("CampoTexto reenvía la marca de su CampoFecha");

        Enlazados([("a.razor", "<CampoFecha NoValidoChanged=\"v => n = v\" /><CampoTexto Type=\"date\" FechaNoValidaChanged=\"v => n = v\" />")])
            .Should().Be(2);
    }

    private static IEnumerable<MarcadoRazor.Apertura> CamposDeFecha(string contenido) =>
        MarcadoRazor.Aperturas(LimpiadorDeComentarios.Quitar(contenido, razor: true), "CampoFecha|CampoTexto")
            .Where(a => a.Nombre == "CampoFecha" || TipoFecha.IsMatch(a.Texto));

    private static bool EnlazaLaMarca(MarcadoRazor.Apertura campo)
    {
        var atributo = campo.Nombre == "CampoFecha" ? "NoValidoChanged" : "FechaNoValidaChanged";
        var m = Regex.Match(campo.Texto, $@"(?:^|\s){atributo}\s*=\s*""");
        if (!m.Success) return false;
        var valor = Regex.Replace(MarcadoRazor.ValorDeComillas(campo.Texto, m.Index + m.Length), @"\s+", string.Empty).TrimStart('@');
        return !Inertes.Contains(valor);
    }

    private static Dictionary<Ubicacion, int> MedirSinMarca(IEnumerable<(string Ruta, string Contenido)> ficheros)
    {
        var medido = new Dictionary<Ubicacion, int>();
        foreach (var (ruta, contenido) in ficheros.Where(f => !Propios.Contains(f.Ruta)))
        {
            var n = CamposDeFecha(contenido).Count(c => !EnlazaLaMarca(c));
            if (n > 0) medido[new Ubicacion(ruta, Simbolo)] = n;
        }

        return medido;
    }

    private static int Enlazados(IEnumerable<(string Ruta, string Contenido)> ficheros) =>
        ficheros.Where(f => !Propios.Contains(f.Ruta)).Sum(f => CamposDeFecha(f.Contenido).Count(EnlazaLaMarca));
}
