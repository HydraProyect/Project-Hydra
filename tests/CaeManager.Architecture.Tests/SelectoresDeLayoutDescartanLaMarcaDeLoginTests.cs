using System.Text.RegularExpressions;
using FluentAssertions;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// <b>Los endpoints a los que postean los selectores de MainLayout (idioma, vista de demo, vista de
/// vocabulario) redirigen con <c>RedireccionLocal.SanearParaVolver</c>, no con <c>Sanear</c>.</b>
///
/// <para>
/// Esos selectores toman como <c>returnUrl</c> la URL con la que se creó el circuito; tras iniciar
/// sesión es <c>/?desde=login</c>, la marca de un solo uso del aterrizaje D-2. Con <c>Sanear</c> (que
/// conserva la consulta) cambiar de idioma o de vista devolvería al usuario a esa URL y reactivaría el
/// aterrizaje. El comportamiento HTTP lo cubre el test de cada endpoint; esto solo evita que alguien
/// vuelva a <c>Sanear</c> sin que el test del endpoint exista todavía.
/// </para>
///
/// <para>
/// <b>Lo que NO observa</b> (huecos declarados): la lista de endpoints es explícita, así que un
/// selector nuevo en MainLayout no entra hasta añadir su endpoint a <see cref="EndpointsDeSelectores"/>;
/// no comprueba de dónde viene el <c>returnUrl</c> de otros endpoints (p. ej. el de empresa activa usa
/// la ruta sin consulta y conserva <c>Sanear</c>).
/// </para>
/// </summary>
public class SelectoresDeLayoutDescartanLaMarcaDeLoginTests
{
    private static readonly string[] EndpointsDeSelectores =
    [
        "src/CaeManager.Web/Components/Account/IdiomaEndpoints.cs",
        "src/CaeManager.Web/Features/Tenants/VistaDemoEndpoints.cs",
        "src/CaeManager.Web/Features/Tenants/VistaVocabularioPreviewEndpoints.cs",
    ];

    private static readonly Regex LlamaASanearParaVolver = new(
        @"RedireccionLocal\s*\.\s*SanearParaVolver\s*\(", RegexOptions.Compiled);

    private static readonly Regex LlamaASanear = new(
        @"RedireccionLocal\s*\.\s*Sanear\s*\(", RegexOptions.Compiled);

    [Fact]
    public void Los_endpoints_de_los_selectores_de_layout_usan_SanearParaVolver()
    {
        foreach (var fichero in EndpointsDeSelectores)
        {
            var texto = SinComentarios(fichero);

            LlamaASanearParaVolver.Matches(texto).Count.Should().Be(1,
                $"{fichero} recibe el returnUrl de un selector de MainLayout y debe volver sin la marca ?desde=login");
            LlamaASanear.IsMatch(texto).Should().BeFalse(
                $"{fichero} no puede volver a RedireccionLocal.Sanear: conserva ?desde=login y reactiva el aterrizaje D-2");
        }
    }

    private static string SinComentarios(string relativo)
    {
        var ruta = Path.Combine(RaizDelRepositorio(), relativo.Replace('/', Path.DirectorySeparatorChar));
        File.Exists(ruta).Should().BeTrue($"control: {relativo} sigue donde este test lo busca");
        return LimpiadorDeComentarios.Quitar(File.ReadAllText(ruta), razor: false);
    }

    private static string RaizDelRepositorio()
    {
        var actual = new DirectoryInfo(AppContext.BaseDirectory);
        while (actual is not null && !File.Exists(Path.Combine(actual.FullName, "CaeManager.slnx")))
            actual = actual.Parent;

        return actual?.FullName ?? throw new InvalidOperationException(
            "No se encontró CaeManager.slnx subiendo desde " + AppContext.BaseDirectory);
    }
}
