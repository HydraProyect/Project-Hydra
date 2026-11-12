using FluentAssertions;

namespace CaeManager.Web.Tests;

/// <summary>
/// La trampa de foco de Modal, Drawer, Context Workspace, buscador global y diálogo de restablecer el menú
/// (wwwroot/js/dialogo-foco.js) no corre en bUnit. js/dialogoFoco.test.mjs la evalúa con un document falso: al
/// abrirse, el foco entra en el diálogo si estaba fuera y se queda donde está si ya estaba dentro (#2194).
/// </summary>
public class DialogoFocoJsTests
{
    [Fact]
    public async Task La_trampa_de_foco_no_roba_el_foco_si_ya_esta_dentro_del_dialogo()
    {
        // Mismo patrón que CampoFechaTests: necesita Node; sin él esta prueba falla en vez de saltarse.
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "CaeManager.slnx"))) dir = Path.GetDirectoryName(dir);
        dir.Should().NotBeNull("se necesita la raíz del repositorio");
        var prueba = Path.Combine(dir!, "tests", "CaeManager.Web.Tests", "js", "dialogoFoco.test.mjs");
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
            throw new InvalidOperationException("Hace falta Node en el PATH para ejecutar js/dialogoFoco.test.mjs: " + ex.Message, ex);
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
