using System.Text.RegularExpressions;
using FluentAssertions;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// <c>ContextoMarcoDelAmbito</c> (pieza 1.3 del plan de rendimiento) solo es seguro con dos condiciones de registro que
/// ningún test unitario ve, porque cada uno monta su propio contenedor:
/// <list type="bullet">
/// <item>Scoped: un singleton compartiría el contexto del marco entre usuarios, Tenants y circuitos.</item>
/// <item>La MISMA instancia registrada también como <c>IMemoPorContexto</c>: si el alias creara otra instancia,
/// <c>InvalidacionAlcanceBehavior</c> invalidaría una copia y los componentes seguirían leyendo el contexto anterior a
/// un Command.</item>
/// </list>
/// Ningún test monta el host real, así que se vigila el texto del registro, mismo mecanismo que
/// <see cref="RegistroDeLaLiberacionDeAccesoADatosAlCerrarCircuitoTests"/>.
/// </summary>
public class RegistroDelContextoMarcoEnProgramTests
{
    private const string Tipo = "CaeManager.Web.Components.Layout.ContextoMarcoDelAmbito";

    [Fact]
    public void Program_registra_el_contexto_del_marco_scoped_y_como_IMemoPorContexto_con_la_misma_instancia()
    {
        var texto = SinEspacios(SinComentarios(File.ReadAllText(Path.Combine(
            RaizDelRepositorio(), "src", "CaeManager.Web", "Program.cs"))));

        texto.Should().Contain($"AddScoped<{Tipo}>()", "Scoped: una instancia por petición o circuito, nunca compartida");
        texto.Should().Contain(
            $"AddScoped<CaeManager.Application.Common.IMemoPorContexto>(sp=>sp.GetRequiredService<{Tipo}>())",
            "el alias resuelve la misma instancia del ámbito, para que cada Command la descarte");
        Regex.Matches(texto, Regex.Escape(Tipo)).Count.Should().Be(2,
            "ningún otro registro (singleton, transient o una segunda instancia) del contexto del marco");
    }

    private static string SinEspacios(string texto) => Regex.Replace(texto, @"\s+", "");

    /// <summary>
    /// Sin comentarios: comentar el registro dejaba la cadena en su sitio y el
    /// ratchet en verde (mismo hueco demostrado en el ratchet de referencia).
    /// </summary>
    private static string SinComentarios(string texto) =>
        string.Join('\n', texto
            .Split('\n')
            .Where(linea => !linea.TrimStart().StartsWith("//", StringComparison.Ordinal)));

    private static string RaizDelRepositorio()
    {
        var actual = new DirectoryInfo(AppContext.BaseDirectory);

        while (actual is not null && !File.Exists(Path.Combine(actual.FullName, "CaeManager.slnx")))
            actual = actual.Parent;

        if (actual is null)
            throw new InvalidOperationException(
                "No se encontró CaeManager.slnx subiendo desde " + AppContext.BaseDirectory +
                " — este test necesita el árbol fuente del repositorio, no solo los ensamblados compilados.");

        return actual.FullName;
    }
}
