using System.Diagnostics;
using FluentAssertions;

namespace CaeManager.Web.Tests;

/// <summary>
/// Lanza las pruebas de Node de <c>wwwroot/js/zona-soltar-archivo.js</c> (Ctrl+V sin foco previo en
/// la zona, ignorar campos de texto, desenganchar al desmontar, rechazos con motivo). bUnit no
/// ejecuta JavaScript, así que esto es lo único que corre el oyente de verdad. Falla, no se
/// salta, si no hay Node: un instrumento que no puede observar no es un verde.
/// </summary>
public class ZonaSoltarArchivoJsTests
{
    private static string RaizDelRepositorio()
    {
        var directorio = new DirectoryInfo(AppContext.BaseDirectory);
        while (directorio is not null && !File.Exists(Path.Combine(directorio.FullName, "tests", "CaeManager.Web.Tests", "js", "zona-soltar-archivo.test.mjs")))
            directorio = directorio.Parent;
        directorio.Should().NotBeNull("las pruebas de Node viven en tests/CaeManager.Web.Tests/js");
        return directorio!.FullName;
    }

    [Fact]
    public async Task El_modulo_de_la_zona_cumple_sus_pruebas_de_node()
    {
        var raiz = RaizDelRepositorio();
        var prueba = Path.Combine(raiz, "tests", "CaeManager.Web.Tests", "js", "zona-soltar-archivo.test.mjs");
        var inicio = new ProcessStartInfo("node")
        {
            WorkingDirectory = raiz,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        inicio.ArgumentList.Add("--test");
        inicio.ArgumentList.Add(prueba);

        Process? proceso = null;
        try
        {
            proceso = Process.Start(inicio);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // handled below
        }

        proceso.Should().NotBeNull("hace falta Node en el PATH para ejecutar las pruebas del módulo JavaScript");
        using (proceso)
        {
            var salida = proceso!.StandardOutput.ReadToEndAsync();
            var errores = proceso.StandardError.ReadToEndAsync();
            using var limite = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            await proceso.WaitForExitAsync(limite.Token);

            proceso.ExitCode.Should().Be(0, $"node --test falló:{Environment.NewLine}{await salida}{await errores}");
            (await salida).Should().Contain("fail 0");
        }
    }
}
