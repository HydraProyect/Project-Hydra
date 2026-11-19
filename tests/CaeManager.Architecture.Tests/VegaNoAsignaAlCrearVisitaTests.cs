using FluentAssertions;
using Xunit;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// Crear una Visita que asigna al Centro y crea las Gestiones de lo que falta (<c>AsignarYCrearGestiones</c>) solo lo
/// dispara la persona que crea la Visita, tras ver la vista previa (decisión de Chris, 2026-11-19): Vega nunca. El asistente
/// crea Visitas por <c>EjecutarPlanTareaAsistenteCommand</c>; este trinquete vigila que nada bajo <c>AsistenteIa</c> pase el
/// flag, que por defecto es <c>false</c>.
/// </summary>
public class VegaNoAsignaAlCrearVisitaTests
{
    [Fact]
    public void Nada_del_asistente_pasa_AsignarYCrearGestiones_al_crear_una_Visita()
    {
        var carpeta = Path.Combine(RaizDelRepositorio(), "src", "CaeManager.Application", "AsistenteIa");
        var ficheros = Directory.GetFiles(carpeta, "*.cs", SearchOption.AllDirectories);

        var ejecutor = ficheros.Single(f => Path.GetFileName(f) == "EjecutarPlanTareaAsistenteCommand.cs");
        var fuente = File.ReadAllText(ejecutor);
        var inicio = fuente.IndexOf("new CrearVisitaCommand(", StringComparison.Ordinal);
        inicio.Should().BeGreaterThanOrEqualTo(0,
            "control: el asistente sigue creando Visitas por aquí; si cambia de sitio, este trinquete debe seguirlo");

        // Tampoco por posición: la llamada no lleva ningún literal true (el flag es el último parámetro del record).
        LlamadaCompleta(fuente, inicio).Should().NotContain("true", "el flag tampoco se pasa por posición");

        ficheros.Where(f => File.ReadAllText(f).Contains("AsignarYCrearGestiones", StringComparison.Ordinal))
            .Select(f => Path.GetRelativePath(carpeta, f))
            .Should().BeEmpty("Vega nunca asigna Trabajadores al Centro ni crea Gestiones al crear una Visita");
    }

    private static string LlamadaCompleta(string fuente, int inicio)
    {
        var abre = fuente.IndexOf('(', inicio);
        var profundidad = 0;
        for (var i = abre; i < fuente.Length; i++)
        {
            if (fuente[i] == '(')
                profundidad++;
            else if (fuente[i] == ')' && --profundidad == 0)
                return fuente[inicio..(i + 1)];
        }

        throw new InvalidOperationException("La llamada a CrearVisitaCommand no cierra el paréntesis.");
    }

    private static string RaizDelRepositorio()
    {
        var actual = new DirectoryInfo(AppContext.BaseDirectory);

        while (actual is not null && !File.Exists(Path.Combine(actual.FullName, "CaeManager.slnx")))
            actual = actual.Parent;

        return actual?.FullName ?? throw new InvalidOperationException(
            "No se encontró CaeManager.slnx subiendo desde " + AppContext.BaseDirectory +
            " — este test necesita el árbol fuente del repositorio.");
    }
}
