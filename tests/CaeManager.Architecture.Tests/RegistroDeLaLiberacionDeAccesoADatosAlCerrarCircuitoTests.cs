using FluentAssertions;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// <c>LiberacionDeAccesoADatosAlCerrarCircuito</c> es lo único que cierra la
/// <c>PuertaAccesoDatos</c> del circuito y espera a la consulta en vuelo antes de
/// que el framework disponga el scope (y con él el <c>DbContext</c> y su
/// conexión). Sin ella el sistema sigue compilando, arrancando y con todos los
/// tests en verde, y la carrera vuelve: consultas que mueren con
/// <c>ArgumentOutOfRangeException</c> en <c>NpgsqlDataReader.ProcessMessage</c> y
/// conexiones desincronizadas devueltas al pool (ver
/// <c>CierreDeCircuitoConConsultaEnVueloTests</c>, que fija el comportamiento
/// con base de datos real pero recibe el handler ya registrado en su propio
/// contenedor: no puede saber si <c>Program.cs</c> lo registra).
///
/// Ningún test monta el host real, así que se vigila el texto del registro, mismo
/// mecanismo que <see cref="RegistroDelResolutorDeSesionPrivilegiadaTests"/>.
/// </summary>
public class RegistroDeLaLiberacionDeAccesoADatosAlCerrarCircuitoTests
{
    [Fact]
    public void Program_registra_la_liberacion_de_acceso_a_datos_como_CircuitHandler_scoped()
    {
        var texto = SinComentarios(File.ReadAllText(Path.Combine(
            RaizDelRepositorio(), "src", "CaeManager.Web", "Program.cs")));

        texto.Should().Contain(
            "AddScoped<CircuitHandler, CaeManager.Web.Services.LiberacionDeAccesoADatosAlCerrarCircuito>",
            "scoped: tiene que recibir la PuertaAccesoDatos del propio circuito, y como CircuitHandler para que el " +
            "framework llame a OnCircuitClosedAsync antes de disponer el scope");
    }

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
