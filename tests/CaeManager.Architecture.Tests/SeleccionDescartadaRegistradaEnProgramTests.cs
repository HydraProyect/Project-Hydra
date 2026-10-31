using FluentAssertions;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// <c>SeleccionDescartadaEnIdentificacionMiddleware</c> solo protege si está en la tubería, y detrás de la revalidación de
/// la selección: delante, esa revalidación vería una selección nula y borraría la cookie de una Sesión Privilegiada que
/// sigue abierta en las demás pestañas. Sus pruebas invocan el middleware directamente, así que ninguna fallaría sin esto.
/// </summary>
public class SeleccionDescartadaRegistradaEnProgramTests
{
    [Fact]
    public void El_descarte_de_la_seleccion_corre_detras_de_su_revalidacion()
    {
        var programa = File.ReadAllText(Path.Combine(RaizDelRepositorio(), "src", "CaeManager.Web", "Program.cs"));

        var revalidacion = programa.IndexOf("app.UseRevalidacionClienteActivo();", StringComparison.Ordinal);
        var descarte = programa.IndexOf("app.UseSeleccionDescartadaEnIdentificacion();", StringComparison.Ordinal);

        revalidacion.Should().BeGreaterThan(-1, "control del instrumento: la revalidación sigue registrada");
        descarte.Should().BeGreaterThan(revalidacion);
    }

    private static string RaizDelRepositorio()
    {
        var actual = new DirectoryInfo(AppContext.BaseDirectory);

        while (actual is not null && !File.Exists(Path.Combine(actual.FullName, "CaeManager.slnx")))
            actual = actual.Parent;

        return actual?.FullName
            ?? throw new InvalidOperationException("No se encontró CaeManager.slnx subiendo desde " + AppContext.BaseDirectory);
    }
}
