namespace CaeManager.E2ETests.Rendimiento;

/// <summary>
/// Fixture con el log de comandos de EF Core encendido. El servidor de los E2E
/// es un proceso aparte (ver <see cref="WebAppFixture"/>): un interceptor o un
/// contador en memoria del test no lo ve. Lo único que cruza esa frontera es
/// lo que el propio servidor escribe, y con
/// <c>Microsoft.EntityFrameworkCore.Database.Command</c> en Information
/// escribe una entrada «Executed DbCommand» por cada comando SQL que ejecuta
/// (en producción el override está en Warning, ver appsettings.json: solo
/// esta fixture lo sube). Colección propia: subir el nivel llena el log y no
/// tiene por qué pagarlo el resto de la suite.
/// </summary>
public sealed class WebAppFixtureConConsultasSql : WebAppFixture
{
    protected override IReadOnlyDictionary<string, string> VariablesDeEntornoAdicionales() =>
        new Dictionary<string, string>
        {
            ["Serilog__MinimumLevel__Override__Microsoft.EntityFrameworkCore.Database.Command"] = "Information",
        };
}

[CollectionDefinition("AppCollectionConsultasSql")]
public class AppCollectionConsultasSql : ICollectionFixture<WebAppFixtureConConsultasSql>;

