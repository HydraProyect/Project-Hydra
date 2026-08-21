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
/// <b>Matriz de clasificación de seguridad de las tablas.</b> No "toda tabla
/// tiene RLS", que sería falso, ni "toda tabla con <c>TenantId</c> tiene RLS",
/// que era cierto hasta F2b-4 y dejó de bastar en cuanto apareció una segunda
/// clase de aislamiento.
///
/// El origen del problema no ha cambiado: el filtro global de EF Core se aplica
/// <b>por reflexión</b> sobre todo lo que hereda de <see cref="EntidadConTenant"/>,
/// mientras que las políticas de RLS se crean desde <b>listas escritas a mano</b>
/// en las migraciones. Añadir una entidad y olvidar su migración deja la primera
/// línea de defensa puesta y la segunda ausente: no falla nada, no se ve en
/// ninguna revisión, y solo se nota el día en que la primera falla — que es
/// exactamente el día para el que existe la segunda.
///
/// Lo que cambia es que ahora hay <b>tres categorías</b>, y cada una tiene una
/// forma distinta y deliberada:
///
/// <list type="table">
/// <item>
/// <term>Tabla tenantizada</term>
/// <description>
/// Lleva <c>TenantId</c> → RLS + <b>FORCE</b> + política <c>aislamiento_tenant</c>,
/// y ninguna otra política. FORCE incluido porque nadie, ni el propietario,
/// tiene por qué ver filas de varios tenants a la vez.
/// </description>
/// </item>
/// <item>
/// <term>Catálogo global protegido</term>
/// <description>
/// Sin <c>TenantId</c> — la fila enlaza dos tenants — → RLS + <b>sin FORCE, a
/// propósito</b> + política <c>posicion_en_la_asignacion</c>. Sin FORCE porque
/// hay caminos sistémicos legítimos (backfill, job de expiración) que operan
/// como propietario y necesitan el grafo completo. La protección frente a una
/// sesión de usuario la da que los roles restringidos no son propietarios, y
/// eso se comprueba <b>por rol</b> en <c>RlsCatalogosDeAsignacionTests</c>, no
/// por la ausencia de FORCE: si mañana cambiara el propietario de las tablas, el
/// comportamiento podría cambiar sin que <c>relforcerowsecurity</c> se moviera.
/// </description>
/// </item>
/// <item>
/// <term>Plano 3, RLS pendiente por dependencia arquitectónica</term>
/// <description>
/// <c>ConcesionesPrivilegio</c>, <c>SesionesPrivilegiadas</c> y
/// <c>TenantsAlcanzadosPorConcesion</c>. <b>No es una excepción del mismo tipo
/// que la anterior</b>, y por eso no comparte lista: allí RLS está implementado
/// con una semántica distinta; aquí no está implementado todavía porque su
/// política necesitaría una variable de sesión con el usuario de plataforma
/// actual, y esa identidad no existe. Se declaran aquí para que el hueco tenga
/// nombre y fecha en vez de ser un olvido.
/// </description>
/// </item>
/// </list>
///
/// La distinción importa más de lo que parece: si el test no representara las
/// tres categorías, dentro de seis meses alguien podría "arreglar" el código
/// para satisfacerlo —añadiendo FORCE a un catálogo global, por ejemplo— sin
/// entender que eso rompe el arranque.
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

    /// <summary>Política de los catálogos globales de asignación.</summary>
    private const string PoliticaPosicion = "posicion_en_la_asignacion";

    /// <summary>
    /// Categoría 2: RLS implementado con semántica distinta. No llevan
    /// <c>TenantId</c> —la fila enlaza dos tenants— así que el barrido por el
    /// modelo no las alcanza y hay que nombrarlas.
    /// </summary>
    private static readonly string[] CatalogosGlobalesProtegidos =
    [
        "AsignacionesOperacion", "AsignacionesCartera",
    ];

    /// <summary>Política del plano de privilegio de plataforma.</summary>
    private const string PoliticaPrivilegio = "privilegio_del_usuario";

    /// <summary>
    /// Categoría 3: <b>catálogo de privilegio</b>. Sin <c>TenantId</c> —una
    /// concesión cruza tenants por definición— pero, al contrario que los
    /// catálogos de asignación, <b>con FORCE</b>. La misma pregunta sobre roles
    /// da respuestas contrarias en los dos casos: allí había lectores sistémicos
    /// legítimos que eximir (backfill, expiración); aquí hay un solo lector,
    /// siempre bajo sesión de usuario, y ningún escritor.
    ///
    /// Esta categoría era, hasta F2b-5, la lista de "RLS pendiente por
    /// dependencia arquitectónica". Dejó de serlo cuando quedó claro que la
    /// política no necesitaba una identidad de plataforma —que habría sido una
    /// afirmación de privilegio en la sesión— sino la coordenada de usuario.
    /// </summary>
    private static readonly string[] CatalogosDePrivilegio =
    [
        "ConcesionesPrivilegio", "SesionesPrivilegiadas", "TenantsAlcanzadosPorConcesion",
    ];

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

    /// <summary>
    /// Categoría 2. La forma que se exige aquí es <b>distinta</b> a la de la
    /// categoría 1, y la diferencia está afirmada, no tolerada: se comprueba que
    /// <c>FORCE</c> está <b>ausente</b>. Si alguien lo añadiera "por coherencia"
    /// con las tablas tenantizadas, este test se pondría rojo — que es
    /// exactamente lo que hace falta, porque con FORCE el seeder de backfill
    /// leería cero filas al arrancar y reconciliaría contra un vacío.
    /// </summary>
    [Fact]
    public async Task Los_catalogos_globales_tienen_RLS_con_su_politica_y_deliberadamente_sin_FORCE()
    {
        var estado = await LeerEstadoRlsAsync(CatalogosGlobalesProtegidos);

        using var _ = new AssertionScope();

        foreach (var tabla in CatalogosGlobalesProtegidos)
        {
            estado.Should().ContainKey(tabla);
            if (!estado.TryGetValue(tabla, out var e)) continue;

            e.Habilitado.Should().BeTrue(
                $"{tabla} es un catálogo global protegido: sin RLS, un rol restringido leería el grafo entero de " +
                "quién opera para quién");

            e.Forzado.Should().BeFalse(
                $"{tabla} NO debe llevar FORCE, y no por descuido: el backfill y el job de expiración operan como " +
                "propietario y necesitan ver todos los tenants a la vez. Quien protege frente a una sesión de " +
                "usuario es que los roles restringidos no son propietarios, y eso se comprueba por rol en " +
                "RlsCatalogosDeAsignacionTests");

            e.Politicas.Select(p => p.Nombre).Should().BeEquivalentTo([PoliticaPosicion],
                $"{tabla} tiene que llevar exactamente '{PoliticaPosicion}' y ninguna otra: una política PERMISSIVE " +
                "adicional se combinaría con OR y ensancharía el acceso");

            var politica = e.Politicas.FirstOrDefault(p => p.Nombre == PoliticaPosicion);
            if (politica is null) continue;

            politica.Using.Should().NotBeNull()
                .And.Subject.As<string>().Should().Contain("app.tenant_origen_id",
                    "sin la segunda variable de sesión, un operador no vería las asignaciones que opera — su propio " +
                    "tenant no es el que está fijado en app.tenant_id dentro de un workspace delegado");

            politica.WithCheck.Should().NotBeNull();
            politica.WithCheck!.Should().NotContain("app.tenant_origen_id",
                "el WITH CHECK es asimétrico a propósito: si el operador pudiera escribir por su posición, se " +
                "concedería a sí mismo asignaciones sobre propietarios ajenos sin necesitar ver nada de ellos");
            politica.WithCheck.Should().Contain("PropietarioTenantId",
                "solo se escribe sobre el tenant en cuyo contexto se está");
        }
    }

    /// <summary>
    /// Categoría 3. Hasta F2b-5 esta lista afirmaba un hueco —"todavía sin RLS,
    /// y este es el motivo"— para que un pendiente no se quedara pendiente para
    /// siempre. Ese test cumplió su función: al implementarse la política se
    /// puso rojo y obligó a mover las tablas de categoría, que es exactamente lo
    /// que tenía que pasar. Ahora afirma la forma nueva.
    ///
    /// Con <c>FORCE</c>, al contrario que los catálogos de asignación. No es
    /// incoherencia: es la misma pregunta sobre roles con respuesta contraria
    /// porque la población de lectores es otra.
    /// </summary>
    [Fact]
    public async Task Los_catalogos_de_privilegio_tienen_RLS_forzado_y_su_politica_propia()
    {
        var estado = await LeerEstadoRlsAsync(CatalogosDePrivilegio);

        using var _ = new AssertionScope();

        foreach (var tabla in CatalogosDePrivilegio)
        {
            estado.Should().ContainKey(tabla);
            if (!estado.TryGetValue(tabla, out var e)) continue;

            e.Habilitado.Should().BeTrue(
                $"{tabla} dice qué usuario de TALVEG puede abrir los datos de qué cliente y hasta cuándo");

            e.Forzado.Should().BeTrue(
                $"{tabla} SÍ lleva FORCE: no hay ningún lector sistémico que eximir — un solo lector, siempre " +
                "bajo sesión de usuario, y ningún escritor. Sin FORCE la política no ataría al propietario, que " +
                "es el rol con el que la aplicación conecta hoy");

            e.Politicas.Select(p => p.Nombre).Should().BeEquivalentTo([PoliticaPrivilegio],
                $"{tabla} tiene que llevar exactamente '{PoliticaPrivilegio}' y ninguna otra");

            var politica = e.Politicas.FirstOrDefault(p => p.Nombre == PoliticaPrivilegio);
            if (politica is null) continue;

            politica.Using.Should().NotBeNull()
                .And.Subject.As<string>().Should().Contain("app.usuario_id",
                    "la autoridad vive en la fila: se ven las que te nombran. Y la coordenada es la identidad " +
                    "autenticada — no existe ni debe existir app.usuario_plataforma_id, que incrustaría una " +
                    "afirmación de privilegio en la sesión");

            politica.WithCheck.Should().NotBeNull()
                .And.Subject.As<string>().Should().Contain("app.usuario_id",
                    "sin WITH CHECK se podría crear o reasignar una concesión a nombre de otro usuario");
        }
    }

    [Fact]
    public void No_existe_ninguna_variable_de_sesion_que_afirme_privilegio_de_plataforma()
    {
        // Guarda del contrato, por texto sobre el interceptor: las coordenadas
        // de sesión describen QUIÉN y DÓNDE, nunca QUÉ PUEDE. Una variable
        // llamada app.usuario_plataforma_id sería una afirmación de privilegio
        // hecha por el proceso que abre la conexión, en vez de una consecuencia
        // de las filas de concesión — y quien la introdujera probablemente no
        // vería que está cambiando el modelo, no solo añadiendo un dato.
        var raiz = RaizDelRepositorio();
        var interceptor = Path.Combine(
            raiz, "src", "CaeManager.Infrastructure", "Persistence", "Interceptors",
            "TenantRlsConnectionInterceptor.cs");

        var texto = File.ReadAllText(interceptor);

        texto.Should().Contain("app.usuario_id", "la coordenada de identidad tiene que seguir fijándose");
        texto.Should().NotContain("set_config('app.usuario_plataforma_id",
            "la pertenencia a plataforma se deriva de las filas de concesión, no se declara en la sesión");
    }

    private static string RaizDelRepositorio()
    {
        var actual = new DirectoryInfo(AppContext.BaseDirectory);

        while (actual is not null && !File.Exists(Path.Combine(actual.FullName, "CaeManager.slnx")))
            actual = actual.Parent;

        if (actual is null)
            throw new InvalidOperationException(
                "No se encontró CaeManager.slnx subiendo desde " + AppContext.BaseDirectory);

        return actual.FullName;
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
