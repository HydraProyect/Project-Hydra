using FluentAssertions;
using Xunit;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// Cierre de la transición P1-F3 (2026-10-21): los compose de staging y
/// producción ya no declaran <c>DataProtection__PermitirClavesSinCifrar</c>, de
/// modo que un despliegue sin certificado no arranca en vez de escribir las
/// claves de Data Protection en claro. La bandera sigue existiendo en el código
/// (pruebas y ensayo de restauración de un backup anterior al certificado); lo
/// que se vigila aquí es que no vuelva a los ficheros que llegan al servidor,
/// ni como variable del compose ni como línea activa de los <c>.env</c> de
/// ejemplo que se copian para crearlos.
/// </summary>
public class ComposeDesplegadosCifranElLlaveroTests
{
    [Theory]
    [InlineData("docker-compose.produccion.yml")]
    [InlineData("docker-compose.staging.yml")]
    [InlineData(".env.example")]
    [InlineData(".env.staging.example")]
    [InlineData("docker-compose.rec196-override.yml")]
    public void Ningun_fichero_desplegado_permite_claves_sin_cifrar(string fichero)
    {
        var lineasActivas = File.ReadAllLines(RutaDeDespliegue(fichero))
            .Where(l => !l.TrimStart().StartsWith('#'));

        lineasActivas.Should().NotContain(l => l.Contains("PermitirClavesSinCifrar", StringComparison.OrdinalIgnoreCase),
            "con la bandera, un .env al que le falte el certificado arranca escribiendo claves del llavero en claro " +
            "—sesiones, antiforgery y credenciales de portales legibles para quien tenga un backup— y solo lo " +
            "delata un aviso. Sin ella el arranque en Production se niega, que es lo que se quiere desde que el " +
            "certificado está activo en staging y producción (runbook P1-F3, § 2.6).");
    }

    private static string RutaDeDespliegue(string fichero) =>
        Path.Combine(RaizDelRepositorio(), "deploy", "local", fichero);

    private static string RaizDelRepositorio()
    {
        var actual = new DirectoryInfo(AppContext.BaseDirectory);

        while (actual is not null && !File.Exists(Path.Combine(actual.FullName, "CaeManager.slnx")))
            actual = actual.Parent;

        return actual?.FullName ?? throw new InvalidOperationException(
            "No se encontró CaeManager.slnx subiendo desde " + AppContext.BaseDirectory);
    }
}
