using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Migraciones;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Npgsql;
using Xunit;

namespace CaeManager.IntegrationTests.Migraciones;

/// <summary>
/// P1-M3: el migrador pasa una base con las 185 migraciones previas a la línea
/// base compactada reescribiendo solo <c>__EFMigrationsHistory</c>, y en
/// cualquier otro estado no toca nada. Cada caso ejecuta lo mismo que
/// <c>MigrarBaseDeDatosAsync</c> (Program.cs): la transición y después
/// <c>MigrateAsync</c>, contra PostgreSQL real.
///
/// <para>
/// La base «previa» se fabrica migrando con la línea base y sustituyendo su
/// historial por las 185 filas antiguas: las migraciones viejas ya no existen,
/// y que el esquema que dejaban es idéntico al de la línea base se demostró con
/// el diff de <c>pg_dump --schema-only</c> de la PR, no aquí.
/// </para>
/// </summary>
public class TransicionLineaBaseCompactadaTests : IAsyncLifetime
{
    private const string VersionPrevia = "10.0.0-historial-previo";

    private readonly string _cadena = BaseDatosPostgresDePruebas.CadenaConexionUnica();

    public async Task InitializeAsync() => await BaseDatosPostgresDePruebas.MigrarAsync(_cadena);

    public async Task DisposeAsync() => await BaseDatosPostgresDePruebas.EliminarAsync(_cadena);

    [Fact]
    public void La_linea_base_es_la_unica_migracion_y_ninguna_previa_sigue_en_el_ensamblado()
    {
        using var contexto = NuevoContexto();
        var migraciones = contexto.Database.GetMigrations().ToList();

        migraciones.Should().Equal([TransicionLineaBaseCompactada.IdLineaBase],
            "la compactación deja una sola migración, y la transición anota exactamente ese id");
        TransicionLineaBaseCompactada.IdsHistoriaPrevia.Should().HaveCount(185)
            .And.OnlyHaveUniqueItems()
            .And.BeInAscendingOrder(StringComparer.Ordinal)
            .And.NotContain(TransicionLineaBaseCompactada.IdLineaBase);
        TransicionLineaBaseCompactada.IdsHistoriaPrevia[0].Should().Be("20260731235023_LineaBase");
        TransicionLineaBaseCompactada.IdsHistoriaPrevia[^1].Should()
            .Be("20260926133410_CorregirBackfillReclamacionBuzonIntegracionBajoRls");
    }

    [Fact]
    public void La_lista_de_ids_previos_es_byte_a_byte_la_de_los_atributos_Migration_anteriores()
    {
        // Huella de los 185 [Migration("…")] de origin/main 0fcb8b87 (b5d8665b
        // en Migrations), ordenados por bytes y unidos con \n, en UTF-8:
        //   grep -rhoE '\[Migration\("[^"]*"\)\]' … | LC_ALL=C sort | sha256sum
        // La lista es lo único que queda de aquellas migraciones: los demás
        // tests construyen el historial a partir de ella, así que sin esta
        // huella un id mal copiado pasaría en todos.
        var bytes = System.Text.Encoding.UTF8.GetBytes(string.Join("\n", TransicionLineaBaseCompactada.IdsHistoriaPrevia));

        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)).Should()
            .Be("d2383fbbee1cd2d634a893be56cf7027ddcfb1c1391675758a784937213164c8");
    }

    [Fact]
    public async Task El_id_con_enie_se_reconoce_byte_a_byte_y_su_variante_sin_tilde_no()
    {
        // 20260810122003_RediseñarSugerenciaGestionCorreoMultiItem es el único
        // id no ASCII. Se escribe aquí con el punto de código explícito (ñ,
        // U+00F1, NFC, C3 B1 en UTF-8), no copiado de la lista.
        var conEnie = "20260810122003_RediseñarSugerenciaGestionCorreoMultiItem";
        var sinTilde = "20260810122003_RedisenarSugerenciaGestionCorreoMultiItem";
        TransicionLineaBaseCompactada.IdsHistoriaPrevia.Should().Contain(conEnie).And.NotContain(sinTilde);

        var resto = TransicionLineaBaseCompactada.IdsHistoriaPrevia.Where(id => id != conEnie).ToList();
        await ReemplazarHistorialAsync([.. resto, sinTilde]);

        var migrar = () => MigrarComoElMigradorAsync();
        var error = (await migrar.Should().ThrowAsync<TransicionLineaBaseAbortadaException>()).Which;
        error.Faltan.Should().Equal(conEnie);
        error.Sobran.Should().Equal(sinTilde);

        await ReemplazarHistorialAsync([.. resto, conEnie]);
        (await MigrarComoElMigradorAsync()).Should().Be(ResultadoTransicionLineaBase.Aplicada);
    }

    [Fact]
    public async Task Historial_previo_completo_se_sustituye_por_la_linea_base_y_migrar_no_hace_nada_mas()
    {
        await ReemplazarHistorialAsync(TransicionLineaBaseCompactada.IdsHistoriaPrevia);

        var resultado = await MigrarComoElMigradorAsync();

        resultado.Should().Be(ResultadoTransicionLineaBase.Aplicada);
        (await LeerHistorialAsync()).Should().Equal(
            [(TransicionLineaBaseCompactada.IdLineaBase, ProductInfo.GetVersion())]);
    }

    [Fact]
    public async Task El_orden_de_aplicacion_del_historial_previo_no_importa()
    {
        // En un servidor que desplegó #877 antes que #947, 20260926121120 se
        // aplicó después de 20260926133410: se compara el conjunto, no el orden.
        await ReemplazarHistorialAsync(TransicionLineaBaseCompactada.IdsHistoriaPrevia.Reverse().ToList());

        (await MigrarComoElMigradorAsync()).Should().Be(ResultadoTransicionLineaBase.Aplicada);
        (await LeerHistorialAsync()).Select(f => f.Id).Should().Equal(TransicionLineaBaseCompactada.IdLineaBase);
    }

    [Fact]
    public async Task Historial_previo_parcial_aborta_nombrando_lo_que_falta_y_no_toca_nada()
    {
        var falta = "20260926121120_VersionEncoladaYDescarteEnTrabajosAnalisis";
        var parcial = TransicionLineaBaseCompactada.IdsHistoriaPrevia.Where(id => id != falta).ToList();
        await ReemplazarHistorialAsync(parcial);
        var antes = await LeerHistorialAsync();

        var migrar = () => MigrarComoElMigradorAsync();

        var error = (await migrar.Should().ThrowAsync<TransicionLineaBaseAbortadaException>()).Which;
        error.Faltan.Should().Equal(falta);
        error.Sobran.Should().BeEmpty();
        error.Message.Should().Contain(falta);
        (await LeerHistorialAsync()).Should().Equal(antes, "abortar no puede dejar el historial a medias");
    }

    [Fact]
    public async Task Un_id_ajeno_ademas_del_historial_previo_aborta_nombrandolo_y_no_toca_nada()
    {
        var ajena = "20260927090000_MigracionDeUnaRamaLocal";
        await ReemplazarHistorialAsync([.. TransicionLineaBaseCompactada.IdsHistoriaPrevia, ajena]);
        var antes = await LeerHistorialAsync();

        var migrar = () => MigrarComoElMigradorAsync();

        var error = (await migrar.Should().ThrowAsync<TransicionLineaBaseAbortadaException>()).Which;
        error.Faltan.Should().BeEmpty();
        error.Sobran.Should().Equal(ajena);
        (await LeerHistorialAsync()).Should().Equal(antes);
    }

    [Fact]
    public async Task Linea_base_mezclada_con_historial_previo_aborta_y_no_toca_nada()
    {
        var previa = TransicionLineaBaseCompactada.IdsHistoriaPrevia[0];
        await ReemplazarHistorialAsync([TransicionLineaBaseCompactada.IdLineaBase, previa]);
        var antes = await LeerHistorialAsync();

        var migrar = () => MigrarComoElMigradorAsync();

        var error = (await migrar.Should().ThrowAsync<TransicionLineaBaseAbortadaException>()).Which;
        error.Sobran.Should().Equal(previa);
        (await LeerHistorialAsync()).Should().Equal(antes);
    }

    [Fact]
    public async Task Base_ya_transicionada_no_se_toca_y_relanzar_el_migrador_es_inocuo()
    {
        await ReemplazarHistorialAsync(TransicionLineaBaseCompactada.IdsHistoriaPrevia);
        (await MigrarComoElMigradorAsync()).Should().Be(ResultadoTransicionLineaBase.Aplicada);
        var tras = await LeerHistorialAsync();

        (await MigrarComoElMigradorAsync()).Should().Be(ResultadoTransicionLineaBase.YaTransicionada);
        (await MigrarComoElMigradorAsync()).Should().Be(ResultadoTransicionLineaBase.YaTransicionada);

        (await LeerHistorialAsync()).Should().Equal(tras);
    }

    [Fact]
    public async Task Base_nueva_la_migra_ef_con_la_linea_base_sin_que_la_transicion_intervenga()
    {
        var nueva = BaseDatosPostgresDePruebas.CadenaConexionUnica();
        await CrearBaseVaciaAsync(nueva);
        try
        {
            (await TransicionLineaBaseCompactada.AplicarAsync(nueva, ProductInfo.GetVersion(), CancellationToken.None))
                .Should().Be(ResultadoTransicionLineaBase.BaseSinHistoria, "sin tabla de historial no hay nada que transicionar");

            await using (var contexto = NuevoContexto(nueva))
                await contexto.Database.MigrateAsync();

            (await LeerHistorialAsync(nueva)).Select(f => f.Id).Should().Equal(TransicionLineaBaseCompactada.IdLineaBase);
            (await TransicionLineaBaseCompactada.AplicarAsync(nueva, ProductInfo.GetVersion(), CancellationToken.None))
                .Should().Be(ResultadoTransicionLineaBase.YaTransicionada);
        }
        finally
        {
            await BaseDatosPostgresDePruebas.EliminarAsync(nueva);
        }
    }

    [Fact]
    public async Task Base_que_aun_no_existe_se_deja_a_ef_que_la_crea()
    {
        // Medido al construir la línea base: sin esto, el migrador de una base
        // de desarrollo nueva fallaba con 3D000 antes de que MigrateAsync
        // pudiera crearla.
        var inexistente = BaseDatosPostgresDePruebas.CadenaConexionUnica();
        try
        {
            (await TransicionLineaBaseCompactada.AplicarAsync(inexistente, ProductInfo.GetVersion(), CancellationToken.None))
                .Should().Be(ResultadoTransicionLineaBase.BaseSinHistoria);

            await using (var contexto = NuevoContexto(inexistente))
                await contexto.Database.MigrateAsync();
            (await LeerHistorialAsync(inexistente)).Select(f => f.Id).Should().Equal(TransicionLineaBaseCompactada.IdLineaBase);
        }
        finally
        {
            await BaseDatosPostgresDePruebas.EliminarAsync(inexistente);
        }
    }

    [Fact]
    public async Task Tabla_de_historial_vacia_se_deja_a_ef()
    {
        await ReemplazarHistorialAsync([]);

        (await TransicionLineaBaseCompactada.AplicarAsync(_cadena, ProductInfo.GetVersion(), CancellationToken.None))
            .Should().Be(ResultadoTransicionLineaBase.BaseSinHistoria);
        (await LeerHistorialAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Dos_migradores_a_la_vez_transicionan_una_sola_vez()
    {
        await ReemplazarHistorialAsync(TransicionLineaBaseCompactada.IdsHistoriaPrevia);
        var barrera = new Barrier(2);

        var resultados = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Task.Run(async () =>
        {
            barrera.SignalAndWait();
            return await TransicionLineaBaseCompactada.AplicarAsync(
                _cadena, ProductInfo.GetVersion(), CancellationToken.None);
        })));

        resultados.Should().BeEquivalentTo(
            [ResultadoTransicionLineaBase.Aplicada, ResultadoTransicionLineaBase.YaTransicionada]);
        (await LeerHistorialAsync()).Select(f => f.Id).Should().Equal(TransicionLineaBaseCompactada.IdLineaBase);
    }

    // Las dos pruebas siguientes fijan el entrelazado en vez de confiar en él:
    // «dos migradores a la vez» de arriba pasa aunque se retiren los dos
    // cerrojos si uno termina antes de que el otro lea (medido con mutación).
    [Theory]
    [InlineData("SELECT pg_advisory_xact_lock(hashtextextended('transicion_linea_base_compactada', 0))")]
    [InlineData("LOCK TABLE \"__EFMigrationsHistory\" IN ROW EXCLUSIVE MODE")]
    public async Task La_transicion_espera_a_quien_tiene_el_cerrojo_y_despues_la_aplica(string cerrojoAjeno)
    {
        await ReemplazarHistorialAsync(TransicionLineaBaseCompactada.IdsHistoriaPrevia);

        await using var ajena = new NpgsqlConnection(_cadena);
        await ajena.OpenAsync();
        await using var transaccionAjena = await ajena.BeginTransactionAsync();
        await using (var cerrojo = new NpgsqlCommand(cerrojoAjeno, ajena, transaccionAjena))
            await cerrojo.ExecuteNonQueryAsync();

        var transicion = TransicionLineaBaseCompactada.AplicarAsync(_cadena, ProductInfo.GetVersion(), CancellationToken.None);
        var primera = await Task.WhenAny(transicion, Task.Delay(TimeSpan.FromSeconds(2)));

        primera.Should().NotBeSameAs(transicion, "con el cerrojo tomado por otra sesión la transición tiene que esperar");
        await transaccionAjena.CommitAsync();
        (await transicion).Should().Be(ResultadoTransicionLineaBase.Aplicada);
    }

    /// <summary>Lo mismo que <c>MigrarBaseDeDatosAsync</c>: transición y después MigrateAsync.</summary>
    private async Task<ResultadoTransicionLineaBase> MigrarComoElMigradorAsync()
    {
        var resultado = await TransicionLineaBaseCompactada.AplicarAsync(
            _cadena, ProductInfo.GetVersion(), CancellationToken.None);
        await using var contexto = NuevoContexto();
        await contexto.Database.MigrateAsync();
        return resultado;
    }

    private CaeManagerDbContext NuevoContexto(string? cadena = null)
    {
        var opciones = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(cadena ?? _cadena, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .Options;
        return new CaeManagerDbContext(opciones, new EphemeralDataProtectionProvider(), new TenantActualAmbiental());
    }

    private async Task ReemplazarHistorialAsync(IReadOnlyList<string> ids)
    {
        await using var conexion = new NpgsqlConnection(_cadena);
        await conexion.OpenAsync();
        await using var transaccion = await conexion.BeginTransactionAsync();
        await using (var borrado = new NpgsqlCommand("DELETE FROM \"__EFMigrationsHistory\"", conexion, transaccion))
            await borrado.ExecuteNonQueryAsync();
        foreach (var id in ids)
        {
            await using var alta = new NpgsqlCommand(
                "INSERT INTO \"__EFMigrationsHistory\" (\"MigrationId\", \"ProductVersion\") VALUES (@id, @v)",
                conexion, transaccion);
            alta.Parameters.AddWithValue("id", id);
            alta.Parameters.AddWithValue("v", VersionPrevia);
            await alta.ExecuteNonQueryAsync();
        }

        await transaccion.CommitAsync();
    }

    private async Task<List<(string Id, string Version)>> LeerHistorialAsync(string? cadena = null)
    {
        await using var conexion = new NpgsqlConnection(cadena ?? _cadena);
        await conexion.OpenAsync();
        await using var lectura = new NpgsqlCommand(
            "SELECT \"MigrationId\", \"ProductVersion\" FROM \"__EFMigrationsHistory\" ORDER BY \"MigrationId\"", conexion);
        await using var filas = await lectura.ExecuteReaderAsync();
        var historial = new List<(string, string)>();
        while (await filas.ReadAsync())
            historial.Add((filas.GetString(0), filas.GetString(1)));
        return historial;
    }

    private static async Task CrearBaseVaciaAsync(string cadena)
    {
        var nombre = new NpgsqlConnectionStringBuilder(cadena).Database;
        await using var conexion = new NpgsqlConnection(BaseDatosPostgresDePruebas.CadenaDeMantenimientoSinPool());
        await conexion.OpenAsync();
        await using var comando = new NpgsqlCommand($"CREATE DATABASE \"{nombre}\"", conexion);
        await comando.ExecuteNonQueryAsync();
    }
}
