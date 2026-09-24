using System.Text.RegularExpressions;
using FluentAssertions;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// <b>Un componente que pide el rol efectivo lo hace por <c>PuertaAccesoDatos</c>.</b>
///
/// <para>
/// <c>ICurrentUserService.ObtenerRolEfectivoAsync</c> (antes <c>ObtenerRolActualAsync</c>, alias obsoleto) parece una lectura de claims y no
/// lo es: dentro de un Workspace operativo derivado resuelve el rol contra la
/// cartera de la operación, con una consulta al <c>DbContext</c> del circuito (o de
/// la petición, en el prerender). Un componente que la llama en
/// <c>OnInitializedAsync</c> corre en paralelo con el layout, que comparte ese
/// <c>DbContext</c>: «A second operation was started on this context instance»,
/// HTTP 500. <c>SubidaMasiva</c> lo hacía y solo fallaba para Consulta delegada,
/// Gestor CAE delegado y Actor de Plataforma en ventana de Soporte; con el rol
/// propio no hay selección, el método devuelve el claim sin consultar y nada
/// fallaba. Hermano de <see cref="IdentityEnComponentesPorLaPuertaDeAccesoADatosTests"/>.
/// </para>
///
/// <para>
/// <b>Lo que SÍ observa:</b> los componentes (<c>.razor</c> más su <c>.razor.cs</c>)
/// que llaman a <c>ObtenerRolEfectivoAsync</c>, a <c>ObtenerRolOrigenAsync</c> (lee con <c>UserManager</c>), al alias <c>ObtenerRolActualAsync</c> o a <c>TieneDobleFactorActivoAsync</c>
/// (esta última lee con <c>UserManager</c>) y en los que no aparece ninguna llamada a
/// <c>PuertaAccesoDatos.EjecutarAsync</c>. La lista congelada es vacía.
/// </para>
///
/// <para>
/// <b>Lo que NO observa</b> (huecos declarados): la granularidad es el componente,
/// no la llamada (uno que use la puerta en un sitio y llame fuera de ella en otro
/// pasa en verde); solo esos miembros de <c>ICurrentUserService</c>; no mira
/// servicios que llamen a otros servicios; se descartan los comentarios, no los
/// literales de texto.
/// </para>
/// </summary>
public class RolActualEnComponentesPorLaPuertaDeAccesoADatosTests
{
    private static readonly Regex LlamaAlRolOalDobleFactor = new(
        @"\.\s*(?:ObtenerRolActualAsync|ObtenerRolEfectivoAsync|ObtenerRolOrigenAsync|TieneDobleFactorActivoAsync)\s*\(", RegexOptions.Compiled);

    private static readonly Regex PasaPorLaPuerta = new(
        @"\bPuertaAccesoDatos\s*\.\s*EjecutarAsync\s*[<(]", RegexOptions.Compiled);

    private static readonly Regex Comentarios = new(
        @"@\*.*?\*@|/\*.*?\*/|//[^\n]*", RegexOptions.Compiled | RegexOptions.Singleline);

    [Fact]
    public void Ningun_componente_pide_el_rol_efectivo_fuera_de_la_puerta()
    {
        var quienLlama = ComponentesQueLlaman();

        quienLlama.Should().Contain("src/CaeManager.Web/Features/Documentos/Pages/SubidaMasiva.razor",
            "SubidaMasiva es el componente que pide el rol efectivo en OnInitializedAsync: si el descubrimiento " +
            "no lo ve, ha dejado de observar el fenómeno y la comparación de abajo pasaría en vacío");

        var sinPuerta = quienLlama
            .Where(c => !FicherosDelComponente(c).Any(f => PasaPorLaPuerta.IsMatch(CodigoSinComentarios(f))))
            .ToList();

        sinPuerta.Should().BeEmpty(
            "ObtenerRolEfectivoAsync/ObtenerRolOrigenAsync consultan el DbContext dentro de un workspace delegado y el componente se " +
            "inicializa en paralelo con el layout: envuélvelo en PuertaAccesoDatos.EjecutarAsync");
    }

    [Fact]
    public void El_detector_distingue_la_llamada_envuelta_de_la_suelta()
    {
        const string suelta = "_x = await CurrentUserService.ObtenerRolEfectivoAsync();";
        const string envuelta = "_x = await PuertaAccesoDatos.EjecutarAsync(() => CurrentUserService.ObtenerRolEfectivoAsync());";
        const string soloComentario = "// PuertaAccesoDatos.EjecutarAsync(() => x)\n_x = await CurrentUserService.ObtenerRolEfectivoAsync();";

        LlamaAlRolOalDobleFactor.IsMatch(suelta).Should().BeTrue();
        PasaPorLaPuerta.IsMatch(Comentarios.Replace(suelta, " ")).Should().BeFalse();
        PasaPorLaPuerta.IsMatch(Comentarios.Replace(envuelta, " ")).Should().BeTrue();
        PasaPorLaPuerta.IsMatch(Comentarios.Replace(soloComentario, " ")).Should().BeFalse();
    }

    private static string CodigoSinComentarios(string relativo) =>
        Comentarios.Replace(File.ReadAllText(Absoluta(relativo)), " ");

    private static List<string> ComponentesQueLlaman()
    {
        var raiz = RaizDelRepositorio();
        var web = Path.Combine(raiz, "src", "CaeManager.Web");
        var separador = Path.DirectorySeparatorChar;

        return Directory
            .EnumerateFiles(web, "*.razor", SearchOption.AllDirectories)
            .Where(a => !a.Contains($"{separador}obj{separador}") && !a.Contains($"{separador}bin{separador}"))
            .Select(a => Path.GetRelativePath(raiz, a).Replace(separador, '/'))
            .Where(c => FicherosDelComponente(c).Any(f => LlamaAlRolOalDobleFactor.IsMatch(CodigoSinComentarios(f))))
            .OrderBy(c => c, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>El <c>.razor</c> y, si existe, su <c>.razor.cs</c>: son el mismo componente.</summary>
    private static IEnumerable<string> FicherosDelComponente(string razorRelativo)
    {
        yield return razorRelativo;

        var codeBehind = razorRelativo + ".cs";
        if (File.Exists(Absoluta(codeBehind))) yield return codeBehind;
    }

    private static string Absoluta(string relativa) =>
        Path.Combine(RaizDelRepositorio(), relativa.Replace('/', Path.DirectorySeparatorChar));

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
