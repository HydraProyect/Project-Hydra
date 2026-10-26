using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Migrations.PostgreSQL;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Xunit;

namespace CaeManager.IntegrationTests.Migraciones;

/// <summary>
/// P6, fase «contraer»: el <c>Down</c> de <c>ContraeContextoRlsFirmado</c>
/// devuelve el catálogo de políticas al de una base migrada solo hasta la
/// migración anterior, y volver a subir deja el mismo catálogo: la reescritura
/// de <see cref="ReescrituraPoliticasContextoRls"/> es simétrica, no pierde ni
/// añade ninguna política, cláusula o función.
///
/// <para>
/// Vive en <c>Migraciones/</c> porque maneja el migrador: parte de una base sin
/// crear y la migra ella misma (<c>ClonDeLaPlantillaSoloParaQuienNoMigraTests</c>).
/// Las propiedades del contexto firmado en la base ya contraída están en
/// <c>Tenants/ContextoRlsFirmadoTests</c>.
/// </para>
///
/// <para>
/// <b>Por qué la referencia pasa por <see cref="ReanalizarPoliticasAsync"/>.</b>
/// La reescritura hace <c>ALTER POLICY</c> con el texto que deparsea
/// <c>pg_get_expr</c>, y PostgreSQL no garantiza que reanalizar su propio deparse
/// dé el mismo árbol. Un <c>IN</c> sobre una columna <c>varchar</c>, la única de
/// la línea base (<c>ConcesionesPrivilegio.privilegio_del_usuario</c>), es el
/// caso conocido: en PostgreSQL 17 pasa de
/// <c>(ARRAY['x'::character varying])::text[]</c> a
/// <c>ARRAY[('x'::character varying)::text]</c> tras un ciclo, con la misma
/// semántica. La base de referencia pasa por el mismo número de ciclos
/// <c>ALTER POLICY</c> que la base que sube y baja, sin la sustitución; así la
/// comparación sigue siendo exacta, texto contra texto, y solo absorbe el
/// reanálisis de PostgreSQL, no un error de la sustitución: un <c>Down</c> que
/// dejara otra expresión seguiría difiriendo de la referencia.
/// </para>
/// </summary>
public class ContraeContextoRlsFirmadoTests : IAsyncLifetime
{
    private const string MigracionContraer = "20261025120000_ContraeContextoRlsFirmado";

    private readonly string _cadena = BaseDatosPostgresDePruebas.CadenaConexionUnica();

    /// <summary>
    /// Hasta «contraer» y no hasta la última: una migración posterior que
    /// añadiera políticas o funciones no tiene que mover esta comparación.
    /// </summary>
    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto();
        await contexto.GetService<IMigrator>().MigrateAsync(MigracionContraer);
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadena);

    [Fact]
    public async Task La_migracion_de_contraer_es_reversible_y_cubre_todas_las_politicas()
    {
        var politicasTrasContraer = await CatalogoDePoliticasAsync(_cadena);
        var funcionesTrasContraer = await DefinicionesDeFuncionesAsync(_cadena);

        var anterior = await MigrarAAnteriorAContraerAsync(_cadena);

        var politicasTrasDown = await CatalogoDePoliticasAsync(_cadena);
        var conGuc = politicasTrasDown.Count(p => p.Value.Contains("current_setting('app.", StringComparison.Ordinal));
        conGuc.Should().BeGreaterThan(100, "control del instrumento: antes de contraer las políticas leían app.*");
        politicasTrasDown.Values.Should().NotContain(v => v.Contains("app_ctx_", StringComparison.Ordinal),
            "ninguna política leía el contexto validado antes de contraer");
        politicasTrasContraer.Count(p => p.Value.Contains("app_ctx_", StringComparison.Ordinal)).Should().Be(conGuc,
            "cada política que leía una GUC suelta pasa al contexto validado, ni una menos");
        (await DefinicionesDeFuncionesAsync(_cadena))["app_enmascarar_auditoria_de_trabajador_anonimizado(uuid)"]
            .Should().Contain("NULLIF(current_setting('app.tenant_id', true), '')::uuid")
            .And.NotContain(ReescrituraPoliticasContextoRls.MarcaFuncion);

        var referencia = BaseDatosPostgresDePruebas.CadenaConexionDeBaseVacia();
        try
        {
            await using (var contexto = CrearContexto(referencia))
                await contexto.GetService<IMigrator>().MigrateAsync(anterior);

            (await DefinicionesDeFuncionesAsync(_cadena)).Should().Equal(await DefinicionesDeFuncionesAsync(referencia),
                "el Down deja cada función como la creó la migración anterior: su cuerpo no pasa por reanálisis");

            // Up y Down: dos ciclos ALTER POLICY sobre la base que sube y baja.
            await ReanalizarPoliticasAsync(referencia);
            await ReanalizarPoliticasAsync(referencia);
            DiferenciasDeCatalogo(politicasTrasDown, await CatalogoDePoliticasAsync(referencia)).Should().BeEmpty(
                "el Down deja cada política como la creó la migración anterior");

            // Volver a subir: el tercer ciclo, en las dos bases.
            await using (var contexto = CrearContexto())
                await contexto.GetService<IMigrator>().MigrateAsync(MigracionContraer);
            await using (var contexto = CrearContexto(referencia))
                await contexto.GetService<IMigrator>().MigrateAsync(MigracionContraer);

            var politicasTrasVolverASubir = await CatalogoDePoliticasAsync(_cadena);
            DiferenciasDeCatalogo(politicasTrasVolverASubir, await CatalogoDePoliticasAsync(referencia)).Should().BeEmpty(
                "subir otra vez reescribe cada política igual que la primera");
            politicasTrasVolverASubir.Keys.Should().BeEquivalentTo(politicasTrasContraer.Keys,
                "ni se pierde ni se añade ninguna política al subir y bajar");
            politicasTrasVolverASubir.Values.Count(v => v.Contains("current_setting('app.", StringComparison.Ordinal))
                .Should().Be(0, "tras volver a subir ninguna política lee las GUC sueltas");
            (await DefinicionesDeFuncionesAsync(_cadena)).Should().Equal(funcionesTrasContraer);
        }
        finally
        {
            await BaseDatosPostgresDePruebas.EliminarAsync(referencia);
        }
    }

    /// <summary>
    /// Las políticas que faltan, sobran o difieren, con los dos textos: un
    /// <c>Equal</c> de diccionarios solo enseña el esperado, truncado.
    /// </summary>
    private static List<string> DiferenciasDeCatalogo(
        Dictionary<string, string> actual, Dictionary<string, string> esperado)
    {
        var diferencias = new List<string>();
        foreach (var (clave, valor) in esperado.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (!actual.TryGetValue(clave, out var valorActual))
                diferencias.Add($"{clave}: falta");
            else if (!string.Equals(valorActual, valor, StringComparison.Ordinal))
                diferencias.Add($"{clave}:\n  esperado: {valor}\n  actual:   {valorActual}");
        }

        diferencias.AddRange(actual.Keys.Where(k => !esperado.ContainsKey(k))
            .OrderBy(k => k, StringComparer.Ordinal).Select(k => $"{k}: sobra"));
        return diferencias;
    }

    /// <summary>
    /// Un ciclo <c>ALTER POLICY</c> con el texto que deparsea PostgreSQL, sin
    /// sustituir nada, sobre las mismas políticas que reescribe
    /// <see cref="ReescrituraPoliticasContextoRls.Sql"/> (las que leen <c>app.*</c>).
    /// </summary>
    private static async Task ReanalizarPoliticasAsync(string cadena)
    {
        await using var conexion = new NpgsqlConnection(cadena);
        await conexion.OpenAsync();
        await using var comando = conexion.CreateCommand();
        comando.CommandText = """
DO $reanalisis$
DECLARE
    p record;
    sentencia text;
BEGIN
    FOR p IN
        SELECT pol.polname, n.nspname, cl.relname,
               pg_get_expr(pol.polqual, pol.polrelid) AS expr_using,
               pg_get_expr(pol.polwithcheck, pol.polrelid) AS expr_check
          FROM pg_policy pol
          JOIN pg_class cl ON cl.oid = pol.polrelid
          JOIN pg_namespace n ON n.oid = cl.relnamespace
         WHERE coalesce(pg_get_expr(pol.polqual, pol.polrelid), '')
               || coalesce(pg_get_expr(pol.polwithcheck, pol.polrelid), '') LIKE '%current_setting(''app.%'
    LOOP
        sentencia := format('ALTER POLICY %I ON %I.%I', p.polname, p.nspname, p.relname);
        IF p.expr_using IS NOT NULL THEN
            sentencia := sentencia || ' USING (' || p.expr_using || ')';
        END IF;
        IF p.expr_check IS NOT NULL THEN
            sentencia := sentencia || ' WITH CHECK (' || p.expr_check || ')';
        END IF;
        EXECUTE sentencia;
    END LOOP;
END
$reanalisis$;
""";
        await comando.ExecuteNonQueryAsync();
    }

    private async Task<string> MigrarAAnteriorAContraerAsync(string cadena)
    {
        await using var contexto = CrearContexto(cadena);
        var migraciones = contexto.Database.GetMigrations().ToList();
        var indice = migraciones.IndexOf(MigracionContraer);
        indice.Should().BeGreaterThan(0, "control del instrumento: la migración de contraer está en el ensamblado");
        var anterior = migraciones[indice - 1];
        await contexto.GetService<IMigrator>().MigrateAsync(anterior);
        return anterior;
    }

    private static async Task<Dictionary<string, string>> CatalogoDePoliticasAsync(string cadena)
    {
        await using var conexion = new NpgsqlConnection(cadena);
        await conexion.OpenAsync();
        await using var comando = conexion.CreateCommand();
        comando.CommandText = @"
SELECT n.nspname || '.' || cl.relname || '.' || pol.polname,
       pol.polcmd::text || '|' || pol.polpermissive::text || '|' || pol.polroles::text || '|' ||
       coalesce(pg_get_expr(pol.polqual, pol.polrelid), '<sin USING>') || '|' ||
       coalesce(pg_get_expr(pol.polwithcheck, pol.polrelid), '<sin WITH CHECK>')
  FROM pg_policy pol
  JOIN pg_class cl ON cl.oid = pol.polrelid
  JOIN pg_namespace n ON n.oid = cl.relnamespace;";
        var resultado = new Dictionary<string, string>();
        await using var lector = await comando.ExecuteReaderAsync();
        while (await lector.ReadAsync())
            resultado[lector.GetString(0)] = lector.GetString(1);
        return resultado;
    }

    private static async Task<Dictionary<string, string>> DefinicionesDeFuncionesAsync(string cadena)
    {
        await using var conexion = new NpgsqlConnection(cadena);
        await conexion.OpenAsync();
        await using var comando = conexion.CreateCommand();
        comando.CommandText = @"
SELECT p.oid::regprocedure::text, pg_get_functiondef(p.oid)
  FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace
 WHERE n.nspname = 'public' AND p.prokind IN ('f', 'p');";
        var resultado = new Dictionary<string, string>();
        await using var lector = await comando.ExecuteReaderAsync();
        while (await lector.ReadAsync())
            resultado[lector.GetString(0)] = lector.GetString(1);
        return resultado;
    }

    private CaeManagerDbContext CrearContexto(string? cadena = null)
    {
        var opciones = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(cadena ?? _cadena, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .Options;

        return new CaeManagerDbContext(opciones, new EphemeralDataProtectionProvider(), new TenantActualAmbiental());
    }
}
