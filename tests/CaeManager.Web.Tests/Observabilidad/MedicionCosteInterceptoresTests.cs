using CaeManager.Application.Common;
using CaeManager.Infrastructure.Persistence.Interceptors;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Web.Tests.Observabilidad;

/// <summary>
/// Los interceptores de medición cuentan en el acumulador cada comando que EF Core
/// ejecuta (consulta, escritura y escalar) y cada apertura de conexión, sin alterar el
/// resultado. Sobre SQLite en memoria: es la mecánica de interceptación de EF, no la
/// de Npgsql, la que se prueba (la integración con PostgreSQL corre en CI).
/// </summary>
public class MedicionCosteInterceptoresTests
{
    [Fact]
    public async Task Cuenta_comandos_y_aperturas_de_conexion_sin_alterar_el_resultado()
    {
        var coste = new CosteCarga();
        await using var contexto = Crear(coste, out var conexion);
        await using var _ = conexion;

        await contexto.Database.ExecuteSqlRawAsync("CREATE TABLE Filas (Id INTEGER PRIMARY KEY, Valor TEXT)");
        await contexto.Database.ExecuteSqlRawAsync("INSERT INTO Filas (Valor) VALUES ('a'), ('b')");
        var filas = await contexto.Database.SqlQueryRaw<string>("SELECT Valor AS Value FROM Filas ORDER BY Id").ToListAsync();

        filas.Should().Equal("a", "b");
        var lectura = coste.Leer();
        lectura.Comandos.Should().Be(3);
        lectura.Conexiones.Should().BeGreaterThanOrEqualTo(1);
        lectura.BaseDatos.Should().BeGreaterThanOrEqualTo(TimeSpan.Zero);
    }

    [Fact]
    public async Task Un_comando_que_falla_tambien_cuenta()
    {
        var coste = new CosteCarga();
        await using var contexto = Crear(coste, out var conexion);
        await using var _ = conexion;

        var fallo = () => contexto.Database.ExecuteSqlRawAsync("SELECT * FROM TablaQueNoExiste");

        await fallo.Should().ThrowAsync<SqliteException>();
        coste.Leer().Comandos.Should().Be(1);
    }

    [Fact]
    public void Cuenta_tambien_por_el_camino_sincrono()
    {
        var coste = new CosteCarga();
        using var contexto = Crear(coste, out var conexion);
        using var _ = conexion;

        contexto.Database.ExecuteSqlRaw("CREATE TABLE T (Id INTEGER)");
        contexto.Database.SqlQueryRaw<int>("SELECT 1 AS Value").ToList();

        coste.Leer().Comandos.Should().Be(2);
        coste.Leer().Conexiones.Should().BeGreaterThanOrEqualTo(1);
    }

    private static ContextoDePrueba Crear(CosteCarga coste, out SqliteConnection conexion)
    {
        // La conexión la abre EF (no se abre a mano) para que pase por el interceptor de conexión;
        // una base en memoria compartida sobrevive a los cierres entre comandos.
        var cadena = $"Data Source=coste-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        conexion = new SqliteConnection(cadena);
        conexion.Open();
        var opciones = new DbContextOptionsBuilder<ContextoDePrueba>()
            .UseSqlite(cadena)
            .AddInterceptors(new MedicionCosteComandosInterceptor(coste), new MedicionCosteConexionesInterceptor(coste))
            .Options;
        return new ContextoDePrueba(opciones);
    }

    private sealed class ContextoDePrueba(DbContextOptions<ContextoDePrueba> opciones) : DbContext(opciones);
}
