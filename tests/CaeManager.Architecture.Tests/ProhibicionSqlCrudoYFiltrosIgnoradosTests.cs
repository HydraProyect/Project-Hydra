using System.Text.RegularExpressions;
using FluentAssertions;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// Horizonte 2.5 (MACRO_PLAN_2026-08-13.md § 2.5, regla 2): CLAUDE.md documenta
/// hoy la regla "nada de <c>IgnoreQueryFilters()</c> ni SQL crudo fuera de los
/// usos ya revisados" como convención — nada la hacía cumplir. Este test la
/// convierte en gate.
///
/// Es el único test de <c>CaeManager.Architecture.Tests</c> que escanea texto
/// fuente en vez de reflexionar sobre el ensamblado compilado, y es deliberado:
/// <c>IgnoreQueryFilters()</c> y el SQL crudo (<c>FromSqlRaw</c>,
/// <c>ExecuteSqlRaw</c>, comandos ADO.NET construidos a mano) son llamadas a
/// método, no dependencias de tipo — verlas por IL con
/// <see cref="System.Reflection"/> exigiría desensamblar cada cuerpo de método
/// (frágil y mucho más código que el problema que resuelve). Grepear el texto
/// es la solución pragmática: barata, legible, y suficiente porque lo único
/// que hace falta es "avisa si aparece una línea nueva que no estaba".
///
/// Ratchet igual que <see cref="FronterasEntrePersistenciaDeFeaturesTests"/>:
/// se congela cada línea de hoy que usa uno de estos mecanismos, identificada
/// por (ruta relativa, contenido de la línea sin espacios de borde) — no por
/// número de línea, para no romperse con un reformateo que no toca la línea
/// en sí. Como dos usos legítimos distintos pueden compartir literalmente el
/// mismo texto de línea (p. ej. dos <c>.IgnoreQueryFilters()</c> en el mismo
/// archivo, uno por método), la lista blanca guarda cuántas veces se permite
/// cada (ruta, línea) — no solo si se permite — así que una repetición nueva
/// por encima de ese número también hace fallar el test.
/// </summary>
public class ProhibicionSqlCrudoYFiltrosIgnoradosTests
{
    private static readonly Regex PatronSospechoso = new(
        @"\.IgnoreQueryFilters\(|\bFromSqlRaw\(|\bFromSqlInterpolated\(|\bExecuteSqlRaw\(|\bExecuteSqlInterpolated\(|\bnew\s+NpgsqlCommand\(|\bnew\s+SqlCommand\(|\.CreateCommand\(",
        RegexOptions.Compiled);

    // Congelado desde el escaneo real a 2026-08-14 (Horizonte 2.5). Cada
    // entrada es (ruta relativa desde la raíz del repo con '/', línea exacta
    // recortada de espacios) → número de apariciones permitidas de esa línea
    // exacta en ese archivo. Comentario junto a cada grupo: por qué está
    // justificado, no repite aquí el razonamiento completo que ya vive en el
    // propio archivo.
    private static readonly Dictionary<(string Ruta, string Linea), int> UsosPermitidos = new()
    {
        // Fase D "deshacer al eliminar": el registro borrado (soft-delete)
        // queda fuera del filtro global de tenant/borrado — hay que saltarlo
        // a propósito para poder restaurarlo, con el TenantId comprobado a mano.
        [("src/CaeManager.Application/Clientes/Commands/RestaurarCliente/RestaurarClienteCommand.cs", ".IgnoreQueryFilters()")] = 1,
        [("src/CaeManager.Application/Centros/Commands/RestaurarCentro/RestaurarCentroCommand.cs", ".IgnoreQueryFilters()")] = 1,
        [("src/CaeManager.Application/Empresas/Commands/RestaurarEmpresa/RestaurarEmpresaCommand.cs", ".IgnoreQueryFilters()")] = 1,
        [("src/CaeManager.Application/Trabajadores/Commands/RestaurarTrabajador/RestaurarTrabajadorCommand.cs", ".IgnoreQueryFilters()")] = 1,
        [("src/CaeManager.Application/Documentos/Commands/RestaurarDocumento/RestaurarDocumentoCommand.cs", ".IgnoreQueryFilters()")] = 1,

        // Backfill de asignaciones operativas (F1): recorre los Clientes de
        // TODOS los tenants en un solo barrido, al arrancar y sin sesión, para
        // trasladar el reparto que ya existe (Cliente.EjecutivoUsuarioId) a las
        // tablas de asignación. Es la única lectura del sistema que cruza la
        // frontera de tenant a propósito, y solo escribe autorización que ya
        // estaba concedida por la vía antigua — no concede nada nuevo. Los
        // eliminados quedan fuera con un Where explícito.
        [("src/CaeManager.Infrastructure/Persistence/Seed/AsignacionesOperativasBackfillSeeder.cs", ".IgnoreQueryFilters()")] = 1,

        // Retención (purga de datos vencidos): tiene que alcanzar también los
        // registros ya borrados lógicamente, así que el filtro global se
        // salta a propósito con el TenantId explícito. Dos apariciones en
        // cada archivo: una para Documentos y otra para Trabajadores.
        [("src/CaeManager.Application/Retencion/EjecucionPurgaService.cs", ".IgnoreQueryFilters()")] = 2,
        [("src/CaeManager.Application/Retencion/DeteccionPurgaService.cs", ".IgnoreQueryFilters()")] = 2,

        // Resolución de tenant a partir de un webhook entrante (Graph/WhatsApp):
        // en ese punto todavía no hay tenant resuelto en el contexto — es
        // precisamente lo que esta consulta calcula — así que no hay filtro
        // de tenant que pueda aplicarse todavía.
        [("src/CaeManager.Infrastructure/Integraciones/WebhookTenantResolver.cs", ".IgnoreQueryFilters()")] = 1,
        [("src/CaeManager.Infrastructure/Integraciones/WebhookWhatsAppTenantResolver.cs", ".IgnoreQueryFilters()")] = 1,

        // Autenticación de API Key: el tenant lo determina la propia clave
        // que se está validando, así que no puede depender del filtro global
        // de tenant (que todavía no se puede fijar sin haber leído la clave).
        [("src/CaeManager.Infrastructure/Persistence/Repositories/ClaveApiRepository.cs", "dbContext.ClavesApi.IgnoreQueryFilters()")] = 1,

        // Segunda línea de defensa RLS (TenantRlsConnectionInterceptor): fija
        // la variable de sesión de Postgres app.tenant_id en cada apertura de
        // conexión con un comando parametrizado (set_config(...)), no SQL
        // interpolado — es el mecanismo en sí, no un atajo alrededor de EF.
        //
        // Sube de 1 a 3 con el enforcement de solo lectura del plano 3
        // (ADR-011 § 4bis.7.4): los otros dos son el SET ROLE al rol
        // cae_app_soporte cuando la petición viene por una sesión privilegiada,
        // y su RESET ROLE al cerrar. Los tres son sentencias fijas del código
        // sobre la sesión de Postgres — no consultan ni modifican datos, y
        // ninguna concatena valores de entrada. Es lo contrario de lo que este
        // ratchet vigila: no rodean a EF, configuran la conexión por debajo para
        // que EF quede MÁS restringido, no menos.
        //
        // Sube de 3 a 4 con las políticas RLS de los catálogos globales de
        // asignación: la cuarta fija app.tenant_origen_id, la variable que dice
        // desde qué tenant se opera. Hace falta aparte de app.tenant_id porque
        // esa refleja el workspace ACTIVO, que dentro de un workspace delegado es
        // el del propietario — con ella sola, un operador no encontraría nunca
        // las asignaciones que opera. Mismo carácter que las otras tres:
        // sentencia fija, parametrizada, sin tocar datos.
        //
        // Sube de 4 a 5 con las políticas RLS del plano de privilegio: la quinta
        // fija app.usuario_id, la identidad autenticada. Es una COORDENADA, no
        // una capacidad — deliberadamente no se llama app.usuario_plataforma_id,
        // porque ese nombre incrustaría en la sesión una afirmación de
        // privilegio. Ser de plataforma se deriva de que existan filas de
        // concesión que te nombren, no de lo que la conexión declare al abrirse.
        [("src/CaeManager.Infrastructure/Persistence/Interceptors/TenantRlsConnectionInterceptor.cs", "await using var comando = connection.CreateCommand();")] = 5,

        // Elección de líder entre réplicas con pg_try_advisory_lock/
        // pg_advisory_unlock: no existe equivalente en EF Core, así que va
        // por una conexión Npgsql propia con comandos parametrizados (uno
        // para adquirir el lock, otro para liberarlo).
        [("src/CaeManager.Infrastructure/Coordinacion/EleccionLiderPostgresService.cs", "await using (var comandoLock = new NpgsqlCommand(")] = 1,
        [("src/CaeManager.Infrastructure/Coordinacion/EleccionLiderPostgresService.cs", "await using var comandoUnlock = new NpgsqlCommand(")] = 1,

        // Verificación de arranque (auditoría Módulo 8): consulta pg_roles
        // por una conexión Npgsql propia — catálogo de sistema, no tabla de
        // dominio, sin TenantId ni borrado lógico que un filtro global
        // tuviera que aplicar. Ver VerificacionRolRuntimeHostedService.
        [("src/CaeManager.Infrastructure/Persistence/VerificacionRolRuntimeHostedService.cs", "await using var comando = conexion.CreateCommand();")] = 1,

        // Retirada de tenant de demo (incidente de siembra parcial del
        // 2026-08-28): borra POR COMPLETO un tenant, así que tiene que
        // alcanzar también las filas ya soft-deleted de ese tenant — el
        // mismo motivo que las Restaurar*Command de arriba, pero para el
        // tenant entero en vez de una fila. El TenantId se comprueba a mano
        // en el Where() que sigue a cada IgnoreQueryFilters(), y el tenant en
        // sí ya pasó por la allowlist de RetiradaTenantDemoService.NombresTenantsDeDemo
        // antes de llegar aquí — nunca se resuelve del ambiente.
        [("src/CaeManager.Infrastructure/MultiTenancy/RetiradaTenantDemoService.cs", "await dbContext.Set<TEntidad>().IgnoreQueryFilters().Where(e => e.TenantId == tenantId).ToListAsync(cancellationToken);")] = 1,
        [("src/CaeManager.Infrastructure/MultiTenancy/RetiradaTenantDemoService.cs", "var tenant = await dbContext.Tenants.IgnoreQueryFilters()")] = 1,
    };

    /// <summary>
    /// Las tres capas que pueden hablar con la base de datos.
    ///
    /// <para>
    /// <c>CaeManager.Web</c> se añadió tras demostrar por mutación que su
    /// ausencia era un hueco real y no una acotación: cuatro ficheros de esa
    /// capa usan <c>CaeManagerDbContext</c> directamente, y uno de ellos es
    /// <c>CurrentUserService</c> —el que resuelve el rol efectivo dentro de un
    /// workspace—. Un <c>.IgnoreQueryFilters()</c> ahí es un bypass de
    /// aislamiento en el camino de autorización; compilaba y el ratchet pasaba
    /// en verde. Que hoy Web tenga cero usos no era una garantía: era una
    /// coincidencia no vigilada.
    /// </para>
    /// </summary>
    private static readonly string[] CarpetasEscaneadas =
    [
        "src/CaeManager.Application",
        "src/CaeManager.Infrastructure",
        "src/CaeManager.Web",
    ];

    /// <summary>
    /// El escaneo, compartido por los dos tests a propósito: el que vigila usos
    /// nuevos y el que comprueba que el escaneo sigue vivo tienen que mirar
    /// exactamente lo mismo, o el segundo dejaría de respaldar al primero.
    ///
    /// <para>
    /// Se incluyen los <c>.razor</c> —un bloque <c>@code</c> es C# igual que un
    /// code-behind— y se excluyen <c>obj/</c> y <c>bin/</c>, sin lo cual el C#
    /// que el compilador genera a partir de cada <c>.razor</c> contaría cada
    /// línea por duplicado.
    /// </para>
    /// </summary>
    private static Dictionary<(string Ruta, string Linea), int> EscanearUsos()
    {
        var raiz = RaizDelRepositorio();
        var apariciones = new Dictionary<(string Ruta, string Linea), int>();

        foreach (var carpeta in CarpetasEscaneadas)
        {
            var directorio = Path.Combine(raiz, carpeta.Replace('/', Path.DirectorySeparatorChar));

            var archivos = Directory
                .EnumerateFiles(directorio, "*", SearchOption.AllDirectories)
                .Where(a => a.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                            || a.EndsWith(".razor", StringComparison.OrdinalIgnoreCase))
                .Where(a => !a.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                            && !a.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));

            foreach (var archivo in archivos)
            {
                var rutaRelativa = Path.GetRelativePath(raiz, archivo).Replace(Path.DirectorySeparatorChar, '/');

                foreach (var lineaCruda in File.ReadLines(archivo))
                {
                    var linea = lineaCruda.Trim();
                    if (!PatronSospechoso.IsMatch(linea)) continue;

                    var entrada = (rutaRelativa, linea);
                    apariciones[entrada] = apariciones.GetValueOrDefault(entrada) + 1;
                }
            }
        }

        return apariciones;
    }

    [Fact]
    public void Ningun_uso_nuevo_de_IgnoreQueryFilters_o_sql_crudo_sin_lista_blanca()
    {
        var infractores = EscanearUsos()
            .Where(kv => kv.Value > UsosPermitidos.GetValueOrDefault(kv.Key))
            .Select(kv => $"{kv.Key.Ruta}: \"{kv.Key.Linea}\" ({kv.Value} apariciones, {UsosPermitidos.GetValueOrDefault(kv.Key)} permitidas)")
            .OrderBy(x => x)
            .ToList();

        string.Join("\n", infractores).Should().BeEmpty(
            "un uso nuevo de IgnoreQueryFilters()/SQL crudo tiene que ser una decisión deliberada y revisada " +
            "(el filtro global de tenant/borrado lógico es la primera línea de defensa multi-tenant, ver " +
            "docs/MULTITENANCY.md) — si el uso listado está justificado, añádelo (o sube el contador) en " +
            "UsosPermitidos en este mismo commit explicando por qué");
    }

    /// <summary>
    /// Guarda del propio ratchet: cada uso congelado tiene que seguir
    /// <b>observándose</b> donde la lista dice y las veces que dice.
    ///
    /// <para>
    /// Sustituye a una guarda que solo sumaba las entradas de la lista blanca y
    /// comprobaba que pasaban de diez. Prometía en su comentario detectar
    /// "carpeta movida, extensión de archivo distinta, regex roto" — y ninguna
    /// de esas tres cosas toca el diccionario, porque el diccionario es una
    /// constante del test. Demostrado por mutación: cambiar el patrón de
    /// búsqueda de <c>*.cs</c> a <c>*.csx</c> deja el escaneo sin un solo
    /// fichero, el test principal pasa en verde sobre un conjunto vacío y la
    /// guarda pasa también. Es el "falso vacío" exacto que decía impedir.
    /// </para>
    ///
    /// <para>
    /// La comprobación es de igualdad, no de mínimo, y eso congela el contrato
    /// en las dos direcciones: un uso que <i>desaparece</i> también cambia la
    /// lista de mecanismos que rodean a EF, y merece pasar por esta lista igual
    /// que uno que aparece. De paso hace imposible la entrada muerta —fichero
    /// renombrado, línea reformateada— que abriría un hueco en silencio.
    /// </para>
    /// </summary>
    [Fact]
    public void Cada_uso_congelado_sigue_observandose_donde_dice_la_lista()
    {
        var observado = EscanearUsos();

        var discrepancias = UsosPermitidos
            .Where(kv => observado.GetValueOrDefault(kv.Key) != kv.Value)
            .Select(kv => $"{kv.Key.Ruta}: \"{kv.Key.Linea}\" " +
                          $"(lista: {kv.Value}, observadas: {observado.GetValueOrDefault(kv.Key)})")
            .OrderBy(x => x)
            .ToList();

        string.Join(Environment.NewLine, discrepancias).Should().BeEmpty(
            "si una línea congelada ya no se observa, el escaneo dejó de mirar donde cree que mira —o el uso " +
            "se retiró sin actualizar la lista—; en ambos casos el ratchet principal estaría dando verde sobre " +
            "un conjunto más pequeño del que cree");
    }

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
