using FluentAssertions;

namespace CaeManager.Web.Tests;

/// <summary>
/// El foco devuelto al disparador de CampoSelectAvanzado al cerrar la lista (wwwroot/js/campo-select-avanzado.js) no
/// corre en bUnit. js/campoSelectAvanzado.test.mjs lo evalúa con un document falso: vuelve al disparador si el foco sigue
/// dentro del componente o cayó al body, y no se le quita a otro campo al que ya se fue mientras llegaba la orden del
/// servidor (#2194).
/// </summary>
public class CampoSelectAvanzadoJsTests
{
    [Fact]
    public async Task Devolver_el_foco_al_cerrar_no_se_lo_quita_a_otro_campo()
    {
        // Mismo patrón que DialogoFocoJsTests: necesita Node; sin él esta prueba falla en vez de saltarse.
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "CaeManager.slnx"))) dir = Path.GetDirectoryName(dir);
        dir.Should().NotBeNull("se necesita la raíz del repositorio");
        var prueba = Path.Combine(dir!, "tests", "CaeManager.Web.Tests", "js", "campoSelectAvanzado.test.mjs");
        File.Exists(prueba).Should().BeTrue();
        var info = new System.Diagnostics.ProcessStartInfo("node", $"--test \"{prueba}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        System.Diagnostics.Process proceso;
        try { proceso = System.Diagnostics.Process.Start(info)!; }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new InvalidOperationException("Hace falta Node en el PATH para ejecutar js/campoSelectAvanzado.test.mjs: " + ex.Message, ex);
        }

        using (proceso)
        {
            var salida = proceso.StandardOutput.ReadToEndAsync();
            var error = proceso.StandardError.ReadToEndAsync();
            var termino = proceso.WaitForExit(TimeSpan.FromSeconds(60));
            if (!termino) proceso.Kill(entireProcessTree: true);
            termino.Should().BeTrue("node --test no debe colgarse");
            var textoSalida = await salida;
            var textoError = await error;
            proceso.ExitCode.Should().Be(0, "las pruebas de Node fallaron:\n" + textoSalida + textoError);
            textoSalida.Should().MatchRegex(@"(?m)\bpass [1-9]\d*\s*$", "node --test debe haber ejecutado casos");
        }
    }
}
