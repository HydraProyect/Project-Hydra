using System.Text;
using CaeManager.E2ETests.Rendimiento;

namespace CaeManager.Web.Tests.Rendimiento;

public sealed class MedidorConsultasSqlTests : IDisposable
{
    private readonly string _directorio = Path.Combine(Path.GetTempPath(), $"medidor-sql-{Guid.NewGuid():N}");

    public MedidorConsultasSqlTests() => Directory.CreateDirectory(_directorio);

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Lee_desde_la_marca_y_todas_las_rotaciones_sin_contar_el_historial(int rotaciones)
    {
        var medidor = new MedidorConsultasSql(_directorio, "log-*.txt");
        var anterior = Path.Combine(_directorio, "log-20261024.txt");
        File.WriteAllText(anterior, Entrada("SELECT historial"), new UTF8Encoding(false));
        var marca = medidor.MarcarAhora();
        File.AppendAllText(anterior, Entrada("SELECT anterior"));
        for (var i = 0; i < rotaciones; i++)
            File.WriteAllText(Path.Combine(_directorio, $"log-202610{25 + i}.txt"), Entrada($"SELECT nuevo{i}"));

        // El orden debe depender del nombre diario, incluso si se toca después el anterior.
        File.SetLastWriteTimeUtc(anterior, DateTime.UtcNow.AddMinutes(1));
        var comandos = medidor.ComandosDesde(marca);

        Assert.Equal(1 + rotaciones, comandos.Count);
        Assert.Equal("SELECT anterior", comandos[0].Sql);
        for (var i = 0; i < rotaciones; i++)
            Assert.Equal($"SELECT nuevo{i}", comandos[i + 1].Sql);
    }

    [Fact]
    public void Rechaza_la_perdida_del_fichero_marcado_aunque_exista_el_nuevo()
    {
        var medidor = new MedidorConsultasSql(_directorio, "log-*.txt");
        var anterior = Path.Combine(_directorio, "log-20261024.txt");
        File.WriteAllText(anterior, Entrada("SELECT anterior"));
        var marca = medidor.MarcarAhora();
        File.Delete(anterior);
        File.WriteAllText(Path.Combine(_directorio, "log-20261025.txt"), Entrada("SELECT nuevo"));

        Assert.Throws<InvalidOperationException>(() => medidor.ComandosDesde(marca));
    }

    [Fact]
    public void Rechaza_el_truncado_del_fichero_marcado()
    {
        var medidor = new MedidorConsultasSql(_directorio, "log-*.txt");
        var fichero = Path.Combine(_directorio, "log-20261024.txt");
        File.WriteAllText(fichero, Entrada("SELECT anterior"));
        var marca = medidor.MarcarAhora();
        File.WriteAllText(fichero, string.Empty);

        Assert.Throws<InvalidOperationException>(() => medidor.ComandosDesde(marca));
    }

    [Fact]
    public void Lee_el_primer_fichero_si_no_existia_log_al_marcar()
    {
        var medidor = new MedidorConsultasSql(_directorio, "log-*.txt");
        var marca = medidor.MarcarAhora();
        File.WriteAllText(Path.Combine(_directorio, "log-20261025.txt"), Entrada("SELECT nuevo"));

        Assert.Equal("SELECT nuevo", Assert.Single(medidor.ComandosDesde(marca)).Sql);
    }

    private static string Entrada(string sql) => $"2026-10-25 00:00:00.000 +02:00 [INF] Executed DbCommand (1ms)\n{sql}\n";

    public void Dispose() => Directory.Delete(_directorio, recursive: true);
}
