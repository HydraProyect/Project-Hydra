using System.Text.RegularExpressions;
using FluentAssertions;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// El puerto de decisiones cerradas del asistente
/// (<c>IDecisionesCerradasAsistenteService</c>) no nombra al proveedor que lo
/// implementa hoy. El nombre del proveedor, su modelo y su endpoint viven solo en
/// Infrastructure.
///
/// Es una propiedad que se pierde sin ruido: basta un comentario o una constante
/// «de conveniencia» en Application para que el siguiente cambio de proveedor
/// arrastre la capa que no debería enterarse. Mismo mecanismo de ratchet por
/// texto que <see cref="ClientesDeIaConResilienciaPropiaTests"/>.
/// </summary>
public class ProveedorDeDecisionesCerradasFueraDeApplicationTests
{
    private static readonly Regex NombresDelProveedor = new(
        @"(?i)\b(jev|typesafe|type\s*safe|system\s*one|systemone)\b|jev-\d|api\.typesafe",
        RegexOptions.Compiled);

    [Fact]
    public void Application_no_nombra_al_proveedor_de_decisiones_cerradas()
    {
        var infractores = Directory
            .EnumerateFiles(Ruta("src/CaeManager.Application"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !EsGenerado(f))
            .Where(f => NombresDelProveedor.IsMatch(File.ReadAllText(f)))
            .ToList();

        infractores.Should().BeEmpty(
            "el puerto expresa decisiones cerradas, no un proveedor: su nombre, su modelo y su endpoint van en Infrastructure");
    }

    /// <summary>
    /// Guarda del instrumento: si el patrón dejara de reconocer al proveedor, el
    /// test principal daría verde sin mirar nada. El adaptador lo nombra, así que
    /// ahí tiene que casar.
    /// </summary>
    [Fact]
    public void El_detector_reconoce_al_proveedor_donde_si_debe_estar()
    {
        var adaptador = Ruta("src/CaeManager.Infrastructure/AsistenteIa/TypeSafeDecisionesCerradasService.cs");

        File.Exists(adaptador).Should().BeTrue("si el adaptador cambia de sitio, hay que reapuntar esta guarda");
        NombresDelProveedor.IsMatch(File.ReadAllText(adaptador)).Should().BeTrue();

        Directory.EnumerateFiles(Ruta("src/CaeManager.Application/AsistenteIa/Decisiones"), "*.cs")
            .Should().NotBeEmpty("el puerto vigilado tiene que seguir existiendo");
    }

    private static bool EsGenerado(string archivo) =>
        archivo.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
        || archivo.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal);

    private static string Ruta(string relativa) =>
        Path.Combine(RaizDelRepositorio(), relativa.Replace('/', Path.DirectorySeparatorChar));

    private static string RaizDelRepositorio()
    {
        var actual = new DirectoryInfo(AppContext.BaseDirectory);

        while (actual is not null && !File.Exists(Path.Combine(actual.FullName, "CaeManager.slnx")))
            actual = actual.Parent;

        actual.Should().NotBeNull("los tests tienen que correr dentro del repositorio");
        return actual!.FullName;
    }
}
