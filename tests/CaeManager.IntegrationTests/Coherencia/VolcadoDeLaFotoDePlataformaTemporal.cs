using Xunit;

namespace CaeManager.IntegrationTests.Coherencia;

// TEMPORAL (no se comitea): vuelca la foto de la plataforma a un fichero para compararla entre dos arboles.
public class VolcadoDeLaFotoDePlataformaTemporal
{
    [Fact]
    public async Task Vuelca_la_foto()
    {
        var salida = Environment.GetEnvironmentVariable("FOTO_PLATAFORMA_SALIDA");
        if (string.IsNullOrEmpty(salida)) return;
        var cadena = BaseDatosPostgresDePruebas.CadenaConexionUnica();
        await using var escenario = new EscenarioDeFotoDePlataforma(cadena);
        await escenario.SembrarAsync();
        var foto = await escenario.TomarFotoAsync();
        var lineas = new List<string>();
        foreach (var (seccion, filas) in foto)
        {
            lineas.Add("## " + seccion);
            lineas.AddRange(filas);
        }
        await File.WriteAllLinesAsync(salida, lineas);
        await BaseDatosPostgresDePruebas.EliminarAsync(cadena);
    }
}

