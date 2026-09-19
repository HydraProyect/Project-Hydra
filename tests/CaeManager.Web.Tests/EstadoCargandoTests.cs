using Bunit;
using CaeManager.Web.Components.DesignSystem;
using FluentAssertions;

namespace CaeManager.Web.Tests;

/// <summary>
/// El esqueleto de carga no debe verse en cargas rápidas: una carga que termina
/// en unos milisegundos, con un esqueleto que aparece y desaparece en un
/// fotograma, se lee como un fallo. El retardo es CSS puro (una animación de
/// opacidad con <c>animation-delay</c> sobre <c>.esqueleto-lista</c>), sin
/// temporizadores en C#.
/// <para>
/// Lo que estos tests observan: el contrato del marcado (la clase que lleva el
/// retardo, <c>aria-busy</c>, las filas) y que la hoja de estilos del componente
/// declara el retardo y apaga el brillo con <c>prefers-reduced-motion</c>.
/// Lo que NO observan: que el destello desaparezca. bUnit no aplica CSS; eso
/// se mide en el navegador (tiempo visible del esqueleto antes y después).
/// </para>
/// </summary>
public class EstadoCargandoTests : BunitContext
{
    [Fact]
    public void El_esqueleto_sale_como_region_ocupada_con_su_clase_de_retardo()
    {
        var cut = Render<EstadoCargando>();

        var lista = cut.Find(".esqueleto-lista");
        lista.GetAttribute("aria-busy").Should().Be("true");
        lista.GetAttribute("aria-label").Should().Be("Cargando…");
    }

    [Theory]
    [InlineData(5)]
    [InlineData(3)]
    public void Pinta_tantas_filas_como_se_piden(int filas)
    {
        var cut = Render<EstadoCargando>(p => p.Add(c => c.Filas, filas));

        cut.FindAll(".esqueleto-fila").Should().HaveCount(filas);
    }

    [Fact]
    public void La_hoja_del_componente_retrasa_la_aparicion_y_apaga_el_brillo_sin_movimiento()
    {
        var css = File.ReadAllText(RutaDelComponente("EstadoCargando.razor.css"));

        // El retardo vive en la propia lista, con relleno "both": transparente
        // antes de que venza, opaca después.
        css.Should().MatchRegex(@"\.esqueleto-lista\s*\{[^}]*animation:\s*esqueleto-aparece\s+0s\s+linear\s+200ms\s+both");
        css.Should().MatchRegex(@"@keyframes\s+esqueleto-aparece\s*\{\s*from\s*\{\s*opacity:\s*0");
        // Con movimiento reducido se apaga el brillo (no el retardo).
        css.Should().MatchRegex(@"@media\s*\(prefers-reduced-motion:\s*reduce\)\s*\{\s*\.esqueleto-fila\s*\{\s*animation:\s*none");
    }

    private static string RutaDelComponente(string fichero)
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "CaeManager.slnx")))
            dir = Path.GetDirectoryName(dir);
        dir.Should().NotBeNull("se necesita la raíz del repositorio para leer la hoja de estilos del componente");
        return Path.Combine(dir!, "src", "CaeManager.Web", "Components", "DesignSystem", fichero);
    }
}
