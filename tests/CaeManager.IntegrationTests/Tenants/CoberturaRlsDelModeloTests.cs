using CaeManager.Domain.Common;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace CaeManager.IntegrationTests.Tenants;

/// <summary>
/// Deriva entre las dos listas de aislamiento por tenant.
///
/// El filtro global de EF Core se aplica <b>por reflexión</b> sobre todo lo que
/// hereda de <see cref="EntidadConTenant"/>: una entidad nueva queda filtrada
/// sin que nadie tenga que acordarse de nada. Las políticas de Row-Level
/// Security, en cambio, se crean desde una <b>lista escrita a mano</b> en las
/// migraciones. Las dos empezaron cuadrando, y nada obligaba a que siguieran
/// haciéndolo: añadir una entidad con <c>TenantId</c> y olvidar su migración de
/// RLS deja una tabla con el filtro de EF puesto y la segunda línea de defensa
/// ausente. No falla nada, no se ve en ninguna revisión, y solo se nota el día
/// en que la primera línea falla — que es exactamente el día para el que existe
/// la segunda.
///
/// Este test compara las dos: toma el modelo de EF como fuente de verdad y
/// exige que Postgres tenga RLS activo, forzado y con política para cada tabla
/// que lleve <c>TenantId</c>. Convierte un olvido silencioso en un test rojo.
///
/// Vive en integración y no en arquitectura porque necesita las dos mitades a
/// la vez: el modelo de EF y el catálogo de una base ya migrada.
/// </summary>
public class CoberturaRlsDelModeloTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenant = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto();
        await contexto.Database.MigrateAsync();
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    /// <summary>
    /// Nombre de la política que crean las migraciones de RLS. Comprobarlo por
    /// nombre y no "que haya alguna" es deliberado: una política cualquiera no
    /// es la política correcta.
    /// </summary>
    private const string PoliticaAislamiento = "aislamiento_tenant";

    /// <summary>
    /// Entidades con <c>TenantId</c> cuya tabla NO debe llevar RLS.
    ///
    /// Vacía hoy, y esa es la afirmación: no hay ninguna excepción. Existe la
    /// lista, y no una rama implícita en el código del test, porque una
    /// excepción legítima que aparezca mañana tiene que quedar escrita, con
    /// nombre y motivo, en el commit que la introduce — y no camuflada dentro de
    /// un <c>if</c> que nadie vuelve a leer. Si se añade una entrada aquí sin
    /// justificación, se ve en la revisión; si se añade una rama al test, no.
    /// </summary>
    private static readonly Dictionary<string, string> ExcepcionesDocumentadas = new();

    [Fact]
    public async Task Toda_entidad_con_TenantId_del_modelo_tiene_RLS_activo_forzado_y_con_la_politica_de_aislamiento()
    {
        await using var contexto = CrearContexto();

        var tablasDelModelo = contexto.Model.GetEntityTypes()
            .Where(t => typeof(EntidadConTenant).IsAssignableFrom(t.ClrType))
            .Select(t => t.GetTableName())
            .Where(nombre => nombre is not null)
            .Select(nombre => nombre!)
            .Distinct()
            .OrderBy(nombre => nombre)
            .ToList();

        tablasDelModelo.Should().NotBeEmpty(
            "si el modelo dejara de exponer entidades con TenantId, este test estaría comparando dos listas vacías");

        var exigidas = tablasDelModelo.Where(t => !ExcepcionesDocumentadas.ContainsKey(t)).ToList();
        var estado = await LeerEstadoRlsAsync(exigidas);

        var sinRls = exigidas.Where(t => !estado.TryGetValue(t, out var e) || !e.Habilitado).ToList();
        var conRls = exigidas.Where(t => estado.TryGetValue(t, out var e) && e.Habilitado).ToList();

        var sinForzar = conRls.Where(t => !estado[t].Forzado).ToList();
        var sinLaPolitica = conRls.Where(t => !estado[t].Politicas.Any(p => p.Nombre == PoliticaAislamiento)).ToList();

        // Una política PERMISSIVE de más no restringe: se combina con OR con las
        // demás, así que ensancha el acceso. Es exactamente el "accidentalmente
        // demasiado permisiva" que un recuento de políticas no vería.
        var conPoliticasDeMas = conRls
            .Where(t => estado[t].Politicas.Any(p => p.Nombre != PoliticaAislamiento))
            .Select(t => $"{t} ({string.Join("+", estado[t].Politicas.Select(p => p.Nombre).Where(n => n != PoliticaAislamiento))})")
            .ToList();

        // La política existe y se llama como toca, pero ¿dice lo que toca? Se
        // comprueba que su expresión menciona las dos mitades del contrato: la
        // columna que discrimina y la variable de sesión que la alimenta. No se
        // compara el texto completo a propósito — Postgres lo normaliza y
        // reescribe casts, y un test que dependa de esa forma exacta se romperá
        // con la próxima versión mayor sin que nada esté mal.
        var conExpresionSospechosa = conRls
            .Select(t => (Tabla: t, Politica: estado[t].Politicas.FirstOrDefault(p => p.Nombre == PoliticaAislamiento)))
            .Where(x => x.Politica is not null)
            .Where(x => !MencionaElAislamiento(x.Politica!.Using) || !MencionaElAislamiento(x.Politica.WithCheck))
            .Select(x => $"{x.Tabla} → USING {x.Politica!.Using ?? "(ninguna)"} / WITH CHECK {x.Politica.WithCheck ?? "(ninguna)"}")
            .ToList();

        using var _ = new AssertionScope();

        string.Join(", ", sinRls).Should().BeEmpty(
            "estas tablas llevan TenantId y el filtro de EF las cubre por reflexión, pero no tienen RLS: falta su " +
            "ALTER TABLE ... ENABLE ROW LEVEL SECURITY en una migración, igual que lo tienen sus hermanas");

        string.Join(", ", sinForzar).Should().BeEmpty(
            "sin FORCE, RLS no restringe al propietario de la tabla — que es el rol con el que la aplicación " +
            "conecta hoy — y la política queda decorativa");

        string.Join(", ", sinLaPolitica).Should().BeEmpty(
            $"RLS habilitado sin la política '{PoliticaAislamiento}' no filtra por tenant: para un rol restringido " +
            "lo niega todo, y para el propietario no protege nada");

        string.Join(", ", conPoliticasDeMas).Should().BeEmpty(
            "una política PERMISSIVE adicional se combina con OR, así que ENSANCHA el acceso en vez de acotarlo; " +
            "si hace falta una política nueva sobre una tabla con TenantId, tiene que revisarse aquí en el mismo " +
            "commit que la introduce");

        string.Join(" | ", conExpresionSospechosa).Should().BeEmpty(
            $"la política '{PoliticaAislamiento}' tiene que comparar la columna TenantId contra la variable de " +
            "sesión app.tenant_id, en USING y también en WITH CHECK — sin WITH CHECK, un INSERT o UPDATE podría " +
            "escribir filas de otro tenant aunque no pudiera leerlas");
    }

    [Fact]
    public void Las_excepciones_a_RLS_estan_documentadas_una_a_una()
    {
        // Guarda de la lista de excepciones: hoy afirma que no hay ninguna.
        // El día que haya una, este test obliga a que traiga su motivo escrito.
        // NotContain y no OnlyContain: la lista está vacía hoy, y OnlyContain
        // trata la colección vacía como fallo. Aquí vacío es el caso bueno —
        // significa que no hay ninguna excepción que justificar.
        ExcepcionesDocumentadas.Should().NotContain(
            e => string.IsNullOrWhiteSpace(e.Value),
            "una tabla con TenantId exenta de RLS es una decisión de seguridad, y una decisión de seguridad sin " +
            "motivo escrito es un descuido que nadie podrá revisar después");
    }

    private static bool MencionaElAislamiento(string? expresion) =>
        expresion is not null
        && expresion.Contains("TenantId", StringComparison.Ordinal)
        && expresion.Contains("app.tenant_id", StringComparison.Ordinal);

    private sealed record PoliticaRls(string Nombre, string? Using, string? WithCheck);

    private async Task<Dictionary<string, (bool Habilitado, bool Forzado, List<PoliticaRls> Politicas)>> LeerEstadoRlsAsync(
        IReadOnlyCollection<string> tablas)
    {
        await using var conexion = new NpgsqlConnection(_cadenaConexion);
        await conexion.OpenAsync();

        await using var comando = conexion.CreateCommand();
        comando.CommandText = @"
SELECT c.relname,
       c.relrowsecurity,
       c.relforcerowsecurity,
       p.polname,
       pg_get_expr(p.polqual, p.polrelid),
       pg_get_expr(p.polwithcheck, p.polrelid)
FROM pg_class c
JOIN pg_namespace n ON n.oid = c.relnamespace
LEFT JOIN pg_policy p ON p.polrelid = c.oid
WHERE n.nspname = 'public' AND c.relkind = 'r' AND c.relname = ANY(@tablas);";
        comando.Parameters.AddWithValue("tablas", tablas.ToArray());

        var estado = new Dictionary<string, (bool, bool, List<PoliticaRls>)>();
        await using var lector = await comando.ExecuteReaderAsync();
        while (await lector.ReadAsync())
        {
            var tabla = lector.GetString(0);
            if (!estado.TryGetValue(tabla, out var actual))
                estado[tabla] = actual = (lector.GetBoolean(1), lector.GetBoolean(2), []);

            if (!lector.IsDBNull(3))
                actual.Item3.Add(new PoliticaRls(
                    lector.GetString(3),
                    lector.IsDBNull(4) ? null : lector.GetString(4),
                    lector.IsDBNull(5) ? null : lector.GetString(5)));
        }

        return estado;
    }

    private CaeManagerDbContext CrearContexto()
    {
        var tenantActual = new TenantActualAmbiental { TenantId = _tenant };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
    }
}
