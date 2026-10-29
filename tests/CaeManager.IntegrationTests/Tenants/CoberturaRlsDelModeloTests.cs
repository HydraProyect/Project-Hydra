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
/// hay un camino sistémico legítimo (el seeder de backfill, vía
/// <c>FabricaContextoDeBootstrap</c>) que opera como propietario y necesita el
/// grafo completo. El job de expiración no es uno de ellos: conecta como
/// <c>cae_app_runtime</c> y recorre los Tenants propietarios uno a uno. La protección frente a una
/// sesión de usuario la da que los roles restringidos no son propietarios, y
/// eso se comprueba <b>por rol</b> en <c>RlsCatalogosDeAsignacionTests</c>, no
/// por la ausencia de FORCE: si mañana cambiara el propietario de las tablas, el
/// comportamiento podría cambiar sin que <c>relforcerowsecurity</c> se moviera.
/// </description>
/// </item>
/// <item>
/// <term>Catálogo de privilegio (plano 3)</term>
/// <description>
/// <c>ConcesionesPrivilegio</c>, <c>SesionesPrivilegiadas</c> y
/// <c>TenantsAlcanzadosPorConcesion</c>. Sin <c>TenantId</c> —una concesión cruza
/// Tenants por definición— → RLS + <b>FORCE</b> + política
/// <c>privilegio_del_usuario</c>, atada a <c>app.usuario_id</c>. Hasta F2b-5 era
/// la lista de «RLS pendiente por dependencia arquitectónica»; dejó de serlo
/// cuando la política resultó no necesitar una identidad de plataforma en la
/// sesión, sino la coordenada de usuario.
/// </description>
/// </item>
/// </list>
///
/// La distinción importa más de lo que parece: si el test no representara las
/// tres categorías, dentro de seis meses alguien podría "arreglar" el código
/// para satisfacerlo —añadiendo FORCE a un catálogo global, por ejemplo— sin
/// entender que eso rompería el arranque el día en que el rol propietario de las
/// tablas dejara de ser superusuario (ver <c>TablasConRlsSinForce</c>).
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

    /// <summary>
    /// Políticas <c>AS RESTRICTIVE</c> revisadas, además de
    /// <c>aislamiento_tenant</c>, por tabla. Una restrictiva se combina con AND
    /// con la de tenant, así que solo puede estrechar el acceso, nunca
    /// ensancharlo; aun así cada una tiene que quedar escrita aquí, con motivo,
    /// en el commit que la introduce. Una restrictiva que no esté en la lista, o
    /// una de la lista que falte en la base, pone el test en rojo. <c>Menciona</c>
    /// son los fragmentos que su USING y su WITH CHECK tienen que contener: sin
    /// ellos, una restrictiva reescrita a <c>true</c> seguiría contando como revisada.
    /// <c>Comando</c> es el pg_policy.polcmd que la política tiene que tener ('*' todas, 'a' INSERT,
    /// 'w' UPDATE): una restrictiva solo de INSERT no tiene USING y ahí basta con su WITH CHECK, pero eso
    /// solo vale para la que se revisó como de INSERT. <c>SoloUsing</c> y <c>SoloWithCheck</c> son los
    /// fragmentos que solo lleva una de las dos expresiones (el estado de partida y el de llegada).
    /// Una tabla puede tener más de una.
    /// </summary>
    private static readonly (string Tabla, string Politica, char Comando, string Motivo, string[] Menciona, string[] SoloUsing, string[] SoloWithCheck)[] PoliticasRestrictivasRevisadas =
    [
        ("TareasAsistente", "solo_su_persona", '*',
            "cada Tarea del asistente es de una sola persona dentro del Tenant: ActorRealUsuarioId = app_ctx_usuario_id()",
            ["\"ActorRealUsuarioId\"", "app_ctx_usuario_id()"], [], []),
        ("TurnosTareaAsistente", "solo_su_persona", '*',
            "un turno solo es visible si su Tarea del asistente lo es (EXISTS sobre la raíz)",
            ["EXISTS", "FROM \"TareasAsistente\"", "\"TurnosTareaAsistente\".\"TareaAsistenteId\""], [], []),
        ("PasosTareaAsistente", "solo_su_persona", '*',
            "un paso solo es visible si su Tarea del asistente lo es (EXISTS sobre la raíz)",
            ["EXISTS", "FROM \"TareasAsistente\"", "\"PasosTareaAsistente\".\"TareaAsistenteId\""], [], []),
        ("ContactosSeguridadTenant", "solo_el_administrador_propone", 'a',
            "el Tenant propietario solo da de alta contactos Propuestos, sin resolución, a nombre de quien firma el " +
            "contexto y si es su Administrador (validar y rechazar son de app_resolver_contacto_seguridad)",
            ["app_es_administrador_del_tenant(", "app_ctx_usuario_id()", "'Propuesto'"], [], []),
        ("ContactosSeguridadTenant", "solo_el_administrador_sustituye_la_propuesta", 'w',
            "lo único que el Tenant propietario actualiza es la propuesta pendiente a Sustituida, y solo su Administrador",
            ["app_es_administrador_del_tenant(", "app_ctx_usuario_id()"], ["'Propuesto'"], ["'Sustituido'"]),
        ("SesionesUsuario", "solo_la_propia_cuenta_revoca", 'w',
            "solo la cuenta que firma el contexto revoca sus sesiones de cookie, aunque otra cuenta del mismo Tenant " +
            "pudiera escribir en él (el alta llega desde el inicio de sesión, sin usuario en el contexto)",
            ["\"UsuarioId\"", "app_ctx_usuario_id()"], [], []),
    ];

    /// <summary>
    /// Políticas PERMISSIVE de lectura del plano de Plataforma revisadas, además de
    /// <c>aislamiento_tenant</c>, por tabla. Ensanchan la lectura —por eso cada una va aquí, con motivo, en
    /// el commit que la introduce— y nada más: tienen que ser solo de SELECT, sin WITH CHECK, y su USING
    /// tiene que ser la concesión AdminPlataforma global vigente sobre la identidad del contexto RLS firmado,
    /// sin disyunciones.
    /// </summary>
    private static readonly Dictionary<string, (string Politica, string Motivo)> LecturasDePlataformaRevisadas = new()
    {
        ["ContactosSeguridadTenant"] = ("actor_de_plataforma_lee",
            "el Actor de Plataforma TALVEG valida o rechaza los contactos de seguridad propuestos por los " +
            "Administradores de todos los Tenants (aviso de violaciones de seguridad, incremento 3a)"),
    };

    /// <summary>Política de los catálogos globales de asignación.</summary>
    private const string PoliticaPosicion = "posicion_en_la_asignacion";

    /// <summary>
    /// <c>DelegacionesTenant</c> enlaza dos Tenants: RLS sin FORCE (ver <see cref="TablasConRlsSinForce"/>)
    /// y dos políticas permisivas. La de sus extremos, para todas las órdenes, solo mira el Tenant
    /// activo. La del Tenant de origen es solo SELECT: en una política de todas las órdenes su USING
    /// decidiría también qué filas se borran o se reapuntan, y el Tenant de origen da lectura, nunca
    /// escritura.
    /// </summary>
    [Fact]
    public async Task Las_delegaciones_entre_Tenants_tienen_RLS_y_solo_las_politicas_de_sus_extremos()
    {
        var estado = await LeerEstadoRlsAsync(["DelegacionesTenant"]);
        var tabla = estado["DelegacionesTenant"];
        tabla.Habilitado.Should().BeTrue();
        tabla.Politicas.Select(p => p.Nombre).Should().BeEquivalentTo(
            ["extremos_de_la_delegacion", "organizacion_de_origen_lee_sus_delegaciones"]);

        var extremos = tabla.Politicas.Single(p => p.Nombre == "extremos_de_la_delegacion");
        extremos.Permisiva.Should().BeTrue();
        extremos.Comando.Should().Be('*');
        foreach (var expresion in new[] { extremos.Using, extremos.WithCheck })
        {
            expresion.Should().NotBeNull();
            expresion!.Should().Contain("TenantConsultoraId").And.Contain("TenantClienteId")
                .And.Contain("app_ctx_tenant_id()").And.NotContain("app_ctx_tenant_origen_id()")
                .And.NotContain("current_setting(");
        }

        var origen = tabla.Politicas.Single(p => p.Nombre == "organizacion_de_origen_lee_sus_delegaciones");
        origen.Permisiva.Should().BeTrue();
        origen.Comando.Should().Be('r', "el Tenant de origen solo abre la lectura");
        origen.WithCheck.Should().BeNull();
        origen.Using.Should().NotBeNull();
        origen.Using!.Should().Contain("TenantConsultoraId").And.Contain("TenantClienteId")
            .And.Contain("app_ctx_tenant_origen_id()").And.NotContain("current_setting(");
    }

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
    /// La única política adicional de un catálogo de privilegio (ADR-011 § 8.7,
    /// incremento 2): el Administrador del Tenant propietario lee las Sesiones
    /// Privilegiadas que apuntan a su Tenant. Solo SELECT, sin WITH CHECK.
    /// </summary>
    private const string PoliticaAdministradorTenantObjetivo = "administrador_del_tenant_objetivo";

    /// <summary>
    /// Categoría 3: <b>catálogo de privilegio</b>. Sin <c>TenantId</c> —una
    /// concesión cruza tenants por definición— pero, al contrario que los
    /// catálogos de asignación, <b>con FORCE</b>. La misma pregunta sobre roles
    /// da respuestas contrarias en los dos casos: allí hay lectores sistémicos
    /// legítimos que eximir (el backfill y la retirada de un Tenant de demo; el job
    /// de expiración no, que corre como cae_app_runtime); aquí hay un solo lector,
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

    /// <summary>
    /// <b>Tablas con RLS y deliberadamente sin FORCE</b>, cada una con el camino que lo necesita
    /// (<c>Camino</c>, ruta del repositorio que el test comprueba que existe) y el motivo.
    ///
    /// <para>
    /// FORCE solo cambia algo para el rol propietario de las tablas, y el tráfico ya no lo es:
    /// <c>VerificacionIdentidadDeRuntime</c> detiene el arranque si la conexión de tráfico es
    /// propietaria de alguna tabla con RLS. Los caminos que conectan COMO rol propietario a
    /// propósito son la identidad administrativa del arranque (<c>FabricaContextoDeBootstrap</c>,
    /// cadena <c>CaeManagerDb</c>), las migraciones y las funciones <c>SECURITY DEFINER</c>, que
    /// corren con la identidad de quien las creó, la de las migraciones. Las siete de abajo tienen
    /// un camino así que necesita ver filas de varios Tenants a la vez; con FORCE, un rol
    /// propietario sometido a RLS las vería vacías sin ningún error — RLS no falla, solo no muestra.
    /// </para>
    ///
    /// <para>
    /// <b>Hoy la ausencia de FORCE es inocua, no protectora.</b> El rol propietario es el
    /// superusuario <c>postgres</c> en todos los entornos (<c>deploy/local/docker-compose.staging.yml</c>,
    /// <c>docker-compose.produccion.yml</c> y CI), y PostgreSQL no somete a RLS al superusuario ni con
    /// FORCE. La invariante real de estos caminos es «el rol propietario evita RLS», y no la
    /// necesitan solo estas siete: <c>EstadoBootstrapPlataforma</c> lleva FORCE y el arranque lo lee
    /// sin <c>app.usuario_id</c>, y la guarda de <c>ParticionadoMensualEventos</c> aborta si el rol no
    /// evita RLS. Lo que la lista afirma es más estrecho: en estas siete, añadir FORCE no aporta
    /// nada hoy y es una rotura más el día en que la propiedad pasara a un rol sometido a RLS.
    /// Cualquier otra tabla con RLS lleva FORCE, y una tabla nueva sin él pone en rojo
    /// <see cref="Toda_tabla_con_RLS_lleva_FORCE_salvo_las_excepciones_con_su_camino"/> hasta que se le
    /// añade o se escribe aquí su camino.
    /// </para>
    /// </summary>
    private static readonly Dictionary<string, (string Camino, string Motivo)> TablasConRlsSinForce = new()
    {
        ["AsignacionesOperacion"] = ("src/CaeManager.Infrastructure/Persistence/Seed/AsignacionesOperativasBackfillSeeder.cs",
            "el backfill de asignaciones del arranque (identidad de bootstrap) escribe una fila por Tenant propietario " +
            "en un solo SaveChanges y reconcilia contra el grafo completo; la retirada de un Tenant de demo " +
            "(RetiradaTenantDemoService) las borra también por la posición de Operador CAE; y las migraciones de " +
            "datos las recorren enteras (ConvierteCarterasPorClienteATenantEnteroTests)"),
        ["AsignacionesCartera"] = ("src/CaeManager.Infrastructure/Persistence/Seed/AsignacionesOperativasBackfillSeeder.cs",
            "mismo backfill y misma retirada que AsignacionesOperacion: la cartera cuelga de la operación"),
        ["DelegacionesTenant"] = ("src/CaeManager.Infrastructure/Persistence/Seed/AsignacionesOperativasBackfillSeeder.cs",
            "el backfill de asignaciones del arranque (identidad de bootstrap) recorre las delegaciones comerciales de " +
            "todos los Tenants sin app.tenant_id para trasladarlas a AsignacionesOperacion; y la retirada de un Tenant " +
            "de demo (RetiradaTenantDemoService) las borra con el rol propietario"),
        ["SolicitudesIncorporacionCartera"] = ("src/CaeManager.Infrastructure/MultiTenancy/RetiradaTenantDemoService.cs",
            "la retirada de un Tenant de demo corre con el rol propietario de las tablas y con app.tenant_id en el " +
            "Tenant retirado, pero sin app.tenant_origen_id; borra las solicitudes en las que el Tenant retirado es " +
            "Tenant propietario u Operador CAE, y la política operador_de_la_solicitud solo deja ver por " +
            "OperadorTenantId = app.tenant_origen_id: bajo RLS no vería ninguna"),
        ["PropuestasApoyoCartera"] = ("src/CaeManager.Infrastructure/MultiTenancy/RetiradaTenantDemoService.cs",
            "mismo motivo que SolicitudesIncorporacionCartera: operador_de_la_propuesta solo deja ver por " +
            "app.tenant_origen_id, que la retirada no fija"),
        ["EncargosAdministracion"] = ("src/CaeManager.Infrastructure/MultiTenancy/RetiradaTenantDemoService.cs",
            "mismo motivo: la retirada borra los encargos en los que el Tenant retirado es el Operador CAE externo, que " +
            "posicion_en_el_encargo solo deja ver por app.tenant_origen_id"),
        ["AspNetUsers"] = ("src/CaeManager.Infrastructure/Identity/AlmacenUsuarios.cs",
            "las funciones SECURITY DEFINER de RlsAspNetUsers (app_tenant_de_cuenta, app_cuenta_por_nombre_normalizado, " +
            "app_cuentas_por_email_normalizado), que cae_app_runtime invoca en cada inicio de sesión antes de conocer " +
            "el Tenant, corren con el rol propietario de las tablas y buscan la cuenta en todos los Tenants; también " +
            "app_tenant_tiene_administrador_propio (WITH CHECK de EncargosAdministracion) y " +
            "app_es_administrador_del_tenant. El backfill de asignaciones lee además dbContext.Users de todos los " +
            "Tenants con la identidad de bootstrap. Con FORCE y un rol propietario sometido a RLS no encontrarían " +
            "ninguna cuenta y el inicio de sesión fallaría"),
    };

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
            .Where(t => estado[t].Politicas.Any(p => p.Nombre != PoliticaAislamiento && !EsRestrictivaRevisada(t, p) && !EsLecturaDePlataformaRevisada(t, p)))
            .Select(t => $"{t} ({string.Join("+", estado[t].Politicas.Where(p => p.Nombre != PoliticaAislamiento && !EsRestrictivaRevisada(t, p) && !EsLecturaDePlataformaRevisada(t, p)).Select(p => p.Permisiva ? p.Nombre : $"{p.Nombre} RESTRICTIVE sin revisar"))})")
            .ToList();

        // La lista de revisadas no es solo una lista blanca: cada entrada
        // afirma que la política existe, es restrictiva y sigue ahí.
        var restrictivasAusentes = PoliticasRestrictivasRevisadas
            .Where(r => !estado.TryGetValue(r.Tabla, out var e)
                        || !e.Politicas.Any(p => p.Nombre == r.Politica && !p.Permisiva))
            .Select(r => $"{r.Tabla}.{r.Politica}")
            .ToList();

        var restrictivasQueNoDicenLoQueToca = PoliticasRestrictivasRevisadas
            .Select(r => (Revisada: r, Politica: estado.TryGetValue(r.Tabla, out var e)
                ? e.Politicas.FirstOrDefault(p => p.Nombre == r.Politica)
                : null))
            .Where(x => x.Politica is not null
                        && (x.Politica.Comando != x.Revisada.Comando
                            || !x.Revisada.Menciona.All(m => (x.Revisada.Comando == 'a' || (x.Politica.Using ?? "").Contains(m, StringComparison.Ordinal))
                                                          && (x.Politica.WithCheck ?? "").Contains(m, StringComparison.Ordinal))
                            || !x.Revisada.SoloUsing.All(m => (x.Politica.Using ?? "").Contains(m, StringComparison.Ordinal))
                            || !x.Revisada.SoloWithCheck.All(m => (x.Politica.WithCheck ?? "").Contains(m, StringComparison.Ordinal))
                            // Ninguna lleva disyunciones: un "... OR true" conservaría
                            // los fragmentos y dejaría de restringir. Que restringe de verdad lo
                            // prueban los tests de runtime (TareasAsistenteRlsRuntimeTests,
                            // ContactosSeguridadTenantBajoRuntimeTests); esto solo cierra la
                            // reescritura más barata.
                            || InspeccionTextoPoliticaRls.TieneDisyuncion(x.Politica.Using) || InspeccionTextoPoliticaRls.TieneDisyuncion(x.Politica.WithCheck)))
            .Select(x => $"{x.Revisada.Tabla}.{x.Revisada.Politica} → USING {x.Politica!.Using ?? "(ninguna)"} / WITH CHECK {x.Politica.WithCheck ?? "(ninguna)"}")
            .ToList();

        // Cada lectura de Plataforma revisada existe y solo abre la lectura, con la concesión
        // AdminPlataforma global sobre el contexto firmado.
        var lecturasDePlataformaQueNoDicenLoQueToca = LecturasDePlataformaRevisadas
            .Select(r => (r.Key, r.Value.Politica, Politica: estado.TryGetValue(r.Key, out var e)
                ? e.Politicas.FirstOrDefault(p => p.Nombre == r.Value.Politica)
                : null))
            .Where(x => x.Politica is null
                        || !x.Politica.Permisiva
                        || x.Politica.Comando != 'r'
                        || x.Politica.WithCheck is not null
                        || x.Politica.Using is null
                        || !x.Politica.Using.Contains("app_es_admin_plataforma_global(", StringComparison.Ordinal)
                        || !x.Politica.Using.Contains("app_ctx_usuario_id()", StringComparison.Ordinal)
                        || x.Politica.Using.Contains("current_setting(", StringComparison.Ordinal)
                        || InspeccionTextoPoliticaRls.TieneDisyuncion(x.Politica.Using))
            .Select(x => $"{x.Key}.{x.Item2} → {(x.Politica is null ? "(no existe)" : $"USING {x.Politica.Using ?? "(ninguna)"} / WITH CHECK {x.Politica.WithCheck ?? "(ninguna)"} / comando {x.Politica.Comando}")}")
            .ToList();

        // La de tenant tiene que ser PERMISSIVE: una tabla con solo políticas
        // restrictivas no deja ver nada, y una aislamiento_tenant restrictiva
        // dejaría de ser la que abre el acceso por tenant.
        var aislamientoNoPermisivo = conRls
            .Where(t => estado[t].Politicas.Any(p => p.Nombre == PoliticaAislamiento && !p.Permisiva))
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
            .Where(x => !InspeccionTextoPoliticaRls.MencionaElAislamiento(x.Politica!.Using) || !InspeccionTextoPoliticaRls.MencionaElAislamiento(x.Politica.WithCheck))
            .Select(x => $"{x.Tabla} → USING {x.Politica!.Using ?? "(ninguna)"} / WITH CHECK {x.Politica.WithCheck ?? "(ninguna)"}")
            .ToList();

        using var _ = new AssertionScope();

        string.Join(", ", sinRls).Should().BeEmpty(
            "estas tablas llevan TenantId y el filtro de EF las cubre por reflexión, pero no tienen RLS: falta su " +
            "ALTER TABLE ... ENABLE ROW LEVEL SECURITY en una migración, igual que lo tienen sus hermanas");

        string.Join(", ", sinForzar).Should().BeEmpty(
            "sin FORCE, RLS no restringe al rol propietario de la tabla — el de las migraciones y la identidad de " +
            "bootstrap, y el del tráfico en un entorno sin CaeManagerDbRuntime — y la política queda decorativa para él");

        string.Join(", ", sinLaPolitica).Should().BeEmpty(
            $"RLS habilitado sin la política '{PoliticaAislamiento}' no filtra por tenant: para un rol restringido " +
            "lo niega todo, y para el propietario no protege nada");

        string.Join(", ", conPoliticasDeMas).Should().BeEmpty(
            "una política PERMISSIVE adicional se combina con OR, así que ENSANCHA el acceso en vez de acotarlo; " +
            "si hace falta una política nueva sobre una tabla con TenantId, tiene que revisarse aquí en el mismo " +
            "commit que la introduce (las RESTRICTIVE, en PoliticasRestrictivasRevisadas)");

        string.Join(", ", restrictivasAusentes).Should().BeEmpty(
            "PoliticasRestrictivasRevisadas afirma que estas políticas restrictivas existen; si una desaparece o " +
            "pasa a PERMISSIVE, la tabla pierde la barrera que la lista documenta");

        string.Join(", ", restrictivasQueNoDicenLoQueToca).Should().BeEmpty(
            "una restrictiva revisada tiene que seguir diciendo, en USING y en WITH CHECK, lo que la lista documenta");

        string.Join(" | ", lecturasDePlataformaQueNoDicenLoQueToca).Should().BeEmpty(
            "una lectura de Plataforma revisada tiene que existir, ser PERMISSIVE solo de SELECT y sin WITH CHECK, y " +
            "pedir la concesión AdminPlataforma global sobre app_ctx_usuario_id(): si escribiera o se abriera a " +
            "otra condición, ensancharía el acceso entre Tenants");

        string.Join(", ", aislamientoNoPermisivo).Should().BeEmpty(
            $"'{PoliticaAislamiento}' tiene que ser PERMISSIVE: es la que concede el acceso por tenant");

        string.Join(" | ", conExpresionSospechosa).Should().BeEmpty(
            $"la política '{PoliticaAislamiento}' tiene que comparar la columna TenantId contra la variable de " +
            "sesión firmado (app_ctx_tenant_id(), P6), en USING y también en WITH CHECK — sin WITH CHECK, un INSERT o UPDATE podría " +
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
    /// El barrido que no depende del modelo de EF: <b>toda</b> tabla del esquema con RLS
    /// activado, tenga o no <c>TenantId</c> y sea de la categoría que sea, lleva FORCE, salvo
    /// las de <see cref="TablasConRlsSinForce"/>. Las categorías de arriba nombran sus tablas a
    /// mano, así que una tabla nueva sin <c>TenantId</c> con ENABLE y sin FORCE no la veía
    /// ninguna; así se juntaron las seis de la lista sin que nada se pusiera rojo.
    ///
    /// <para>
    /// La lista es exacta en los dos sentidos: una excepción cuya tabla ya lleva FORCE, ha
    /// perdido RLS o ya no existe también pone el test en rojo, para que no quede una
    /// exención escrita que nadie ejerce. Y su <c>Camino</c> tiene que seguir existiendo: si el
    /// fichero que justificaba la excepción desaparece, el motivo hay que revisarlo.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Toda_tabla_con_RLS_lleva_FORCE_salvo_las_excepciones_con_su_camino()
    {
        await using var conexion = new NpgsqlConnection(_cadenaConexion);
        await conexion.OpenAsync();

        await using var comando = conexion.CreateCommand();
        comando.CommandText = @"
SELECT c.relname, c.relforcerowsecurity
FROM pg_class c
JOIN pg_namespace n ON n.oid = c.relnamespace
WHERE n.nspname = 'public' AND c.relkind IN ('r', 'p') AND c.relrowsecurity;";

        var conRls = new Dictionary<string, bool>();
        await using (var lector = await comando.ExecuteReaderAsync())
        {
            while (await lector.ReadAsync())
                conRls[lector.GetString(0)] = lector.GetBoolean(1);
        }

        conRls.Should().ContainKeys(["AspNetUsers", "AsignacionesOperacion", "EstadoBootstrapPlataforma"],
            "si el barrido no encontrara las tablas con RLS que se sabe que existen, compararía contra un vacío");

        var sinForceNiExcepcion = conRls
            .Where(t => !t.Value && !TablasConRlsSinForce.ContainsKey(t.Key))
            .Select(t => t.Key).OrderBy(t => t).ToList();

        var excepcionesQueYaNoAplican = TablasConRlsSinForce.Keys
            .Where(t => !conRls.TryGetValue(t, out var forzada) || forzada)
            .OrderBy(t => t).ToList();

        var raiz = RaizDelRepositorio();
        var caminosQueNoExisten = TablasConRlsSinForce
            .Where(e => !File.Exists(Path.Combine(raiz, e.Value.Camino.Replace('/', Path.DirectorySeparatorChar))))
            .Select(e => $"{e.Key} → {e.Value.Camino}").ToList();

        using var _ = new AssertionScope();

        string.Join(", ", sinForceNiExcepcion).Should().BeEmpty(
            "estas tablas tienen RLS sin FORCE: o les falta su ALTER TABLE ... FORCE ROW LEVEL SECURITY en una " +
            "migración, o tienen un camino que conecta como propietario y necesita ver varios Tenants, y entonces " +
            "va en TablasConRlsSinForce con ese camino y su motivo, en el mismo commit");

        string.Join(", ", excepcionesQueYaNoAplican).Should().BeEmpty(
            "estas excepciones ya no describen la base (la tabla lleva FORCE, no tiene RLS o no existe): retíralas " +
            "de TablasConRlsSinForce");

        string.Join(", ", caminosQueNoExisten).Should().BeEmpty(
            "el camino que justifica la excepción ya no está en el repositorio: revisa si la tabla sigue necesitándola");

        TablasConRlsSinForce.Should().NotContain(
            e => string.IsNullOrWhiteSpace(e.Value.Motivo),
            "una tabla con RLS exenta de FORCE es una decisión de seguridad, y sin motivo escrito nadie podrá revisarla");
    }

    /// <summary>
    /// Categoría 2. La forma que se exige aquí es <b>distinta</b> a la de la
    /// categoría 1, y la diferencia está afirmada, no tolerada: se comprueba que
    /// <c>FORCE</c> está <b>ausente</b>. Si alguien lo añadiera "por coherencia"
    /// con las tablas tenantizadas, este test se pondría rojo — que es
    /// exactamente lo que hace falta, porque con FORCE y un rol propietario
    /// sometido a RLS (hoy es superusuario, ver <c>TablasConRlsSinForce</c>) el
    /// seeder de backfill leería cero filas al arrancar y reconciliaría contra un vacío.
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
                $"{tabla} NO debe llevar FORCE, y no por descuido: el seeder de backfill opera como " +
                "propietario y necesita ver todos los tenants a la vez. Quien protege frente a una sesión de " +
                "usuario es que los roles restringidos no son propietarios, y eso se comprueba por rol en " +
                "RlsCatalogosDeAsignacionTests");

            e.Politicas.Select(p => p.Nombre).Should().BeEquivalentTo([PoliticaPosicion],
                $"{tabla} tiene que llevar exactamente '{PoliticaPosicion}' y ninguna otra: una política PERMISSIVE " +
                "adicional se combinaría con OR y ensancharía el acceso");

            var politica = e.Politicas.FirstOrDefault(p => p.Nombre == PoliticaPosicion);
            if (politica is null) continue;

            politica.Using.Should().NotBeNull()
                .And.Subject.As<string>().Should().Contain("app_ctx_tenant_origen_id()",
                    "sin la segunda variable de sesión, un operador no vería las asignaciones que opera — su propio " +
                    "tenant no es el que está firmado en app_ctx_tenant_id() dentro de un workspace delegado");

            politica.WithCheck.Should().NotBeNull();
            politica.WithCheck!.Should().NotContain("app_ctx_tenant_origen_id()",
                "el WITH CHECK es asimétrico a propósito: si el operador pudiera escribir por su posición, se " +
                "concedería a sí mismo asignaciones sobre propietarios ajenos sin necesitar ver nada de ellos");
            politica.WithCheck.Should().Contain("PropietarioTenantId",
                "solo se escribe sobre el tenant en cuyo contexto se está");
        }
    }

    /// <summary>
    /// Categoría 4: <b>el estado de bootstrap</b>. Es la única tabla del modelo
    /// cuya forma no cabe en las tres anteriores, y por eso se quedó sin vigilar
    /// hasta 2026-08-23: las tres categorías exigen <i>exactamente una</i>
    /// política, y esta tiene <b>tres</b> —una por verbo—. No era un olvido: era
    /// una categoría que el instrumento no podía representar.
    ///
    /// <para>
    /// Que quedara fuera importa más que en cualquier otra tabla: es la que decide
    /// <b>quién puede acuñar la autoridad fundacional de la plataforma</b>. Sin
    /// esto, una migración que retirase una de sus tres políticas no habría puesto
    /// nada en rojo.
    /// </para>
    /// </summary>
    private static readonly string[] PoliticasDelBootstrap =
    [
        "estado_bootstrap_consumo_por_la_raiz",
        "estado_bootstrap_designacion_al_arrancar",
        "estado_bootstrap_lectura_de_la_raiz",
    ];

    /// <summary>
    /// La forma de la categoría 4, afirmada verbo a verbo.
    ///
    /// <para>
    /// <b>La ausencia de política de DELETE es una aserción</b>, no un descuido del
    /// test: sin política permisiva, ningún rol sujeto a RLS puede borrar la fila,
    /// y así la monotonía del bootstrap no depende solo del dominio. Añadir una
    /// política de borrado "por simetría" tiene que ponerse rojo.
    /// </para>
    /// </summary>
    [Fact]
    public async Task El_estado_de_bootstrap_tiene_sus_tres_politicas_y_ninguna_de_borrado()
    {
        var estado = await LeerEstadoRlsAsync(["EstadoBootstrapPlataforma"]);

        estado.Should().ContainKey("EstadoBootstrapPlataforma");
        var e = estado["EstadoBootstrapPlataforma"];

        using var _ = new AssertionScope();

        e.Habilitado.Should().BeTrue("decide quién puede acuñar la autoridad fundacional de la plataforma");
        e.Forzado.Should().BeTrue(
            "SÍ lleva FORCE: sin él la política no ataría al propietario de la tabla, que es el rol con el " +
            "que migran todos los entornos");

        e.Politicas.Select(p => p.Nombre).OrderBy(n => n).Should().BeEquivalentTo(PoliticasDelBootstrap,
            "tres políticas, una por verbo, y NINGUNA de DELETE: sin política permisiva de borrado ningún " +
            "rol sujeto a RLS puede eliminar la fila, y la monotonía del bootstrap deja de depender solo " +
            "del dominio");

        var lectura = e.Politicas.FirstOrDefault(p => p.Nombre == "estado_bootstrap_lectura_de_la_raiz");
        lectura.Should().NotBeNull();
        lectura!.Using.Should().NotBeNull()
            .And.Subject.As<string>().Should().Contain("UsuarioRaizId").And.Contain("app_ctx_usuario_id()",
                "solo la identidad raíz ve la fila: para cualquier otro usuario la tabla está vacía");

        var designacion = e.Politicas.FirstOrDefault(p => p.Nombre == "estado_bootstrap_designacion_al_arrancar");
        designacion.Should().NotBeNull();
        designacion!.WithCheck.Should().NotBeNull()
            .And.Subject.As<string>().Should().Contain("app_ctx_valido()").And.Contain("app_ctx_usuario_id()",
                "la designación la escribe el ARRANQUE, que no es una sesión de usuario: " +
                "un contexto firmado sin usuario es el discriminante (sin token no hay excepción), y es una coordenada del modelo, no una propiedad del rol");

        var consumo = e.Politicas.FirstOrDefault(p => p.Nombre == "estado_bootstrap_consumo_por_la_raiz");
        consumo.Should().NotBeNull();
        consumo!.Using.Should().NotBeNull()
            .And.Subject.As<string>().Should().Contain("app_ctx_usuario_id()");
        consumo.WithCheck.Should().NotBeNull()
            .And.Subject.As<string>().Should().Contain("UsuarioRaizId",
                "el WITH CHECK repite el predicado a propósito: sin él, la fila podría actualizarse para " +
                "apuntar a otro usuario raíz y saldría del alcance de quien la está tocando");
    }

    /// <summary>
    /// Categoría 5: <b>configuración de plataforma de lectura universal</b>
    /// (<c>OrdenMenuLateral</c>, decisión del 2026-09-23). Como el bootstrap, fila única del
    /// sistema sin TenantId y con FORCE; al contrario que él, la LEE todo el mundo —el orden del
    /// menú no es ni secreto ni autoridad— y la ESCRIBE solo una concesión AdminPlataforma
    /// <b>global</b>. Tres políticas, una por verbo, y ninguna de DELETE.
    /// </summary>
    private static readonly string[] PoliticasDelOrdenDelMenu =
    [
        "orden_menu_alta_por_admin_plataforma_global",
        "orden_menu_cambio_por_admin_plataforma_global",
        "orden_menu_lectura_de_todos",
    ];

    [Fact]
    public async Task El_orden_del_menu_lo_lee_todo_el_mundo_y_solo_lo_escribe_una_concesion_global()
    {
        var estado = await LeerEstadoRlsAsync(["OrdenMenuLateral"]);

        estado.Should().ContainKey("OrdenMenuLateral");
        var e = estado["OrdenMenuLateral"];

        using var _ = new AssertionScope();

        e.Habilitado.Should().BeTrue();
        e.Forzado.Should().BeTrue("sin FORCE la política no ataría al propietario de la tabla");

        e.Politicas.Select(p => p.Nombre).OrderBy(n => n).Should().BeEquivalentTo(PoliticasDelOrdenDelMenu,
            "una por verbo y NINGUNA de DELETE: una política PERMISSIVE más se combinaría con OR y abriría " +
            "la escritura; una de borrado dejaría vaciar la configuración de todos los Tenants");

        foreach (var nombre in new[] { "orden_menu_alta_por_admin_plataforma_global", "orden_menu_cambio_por_admin_plataforma_global" })
        {
            var escritura = e.Politicas.FirstOrDefault(p => p.Nombre == nombre);
            escritura.Should().NotBeNull();
            escritura!.WithCheck.Should().NotBeNull()
                .And.Subject.As<string>().Should().Contain("app_es_admin_plataforma_global(").And.Contain("app_ctx_usuario_id()",
                    "la escritura se ata a la concesión GLOBAL del usuario de la sesión, no a app_es_admin_plataforma, " +
                    "que admitiría una concesión acotada a un solo Tenant");
        }

        var cambio = e.Politicas.First(p => p.Nombre == "orden_menu_cambio_por_admin_plataforma_global");
        cambio.Using.Should().NotBeNull()
            .And.Subject.As<string>().Should().Contain("app_es_admin_plataforma_global(",
                "el USING del UPDATE también: sin él, cualquiera podría seleccionar la fila para cambiarla");
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
                "bajo sesión de usuario, y ningún escritor. Sin FORCE la política no ataría al rol propietario de " +
                "la tabla, el de las migraciones y la identidad de bootstrap");

            string[] esperadas = tabla == "SesionesPrivilegiadas"
                ? [PoliticaPrivilegio, PoliticaAdministradorTenantObjetivo]
                : [PoliticaPrivilegio];
            e.Politicas.Select(p => p.Nombre).Should().BeEquivalentTo(esperadas,
                $"{tabla} tiene que llevar exactamente esas políticas y ninguna otra");

            var transparencia = e.Politicas.FirstOrDefault(p => p.Nombre == PoliticaAdministradorTenantObjetivo);
            if (transparencia is not null)
            {
                transparencia.Comando.Should().Be('r',
                    "la transparencia para el Tenant propietario es solo lectura: una política permisiva FOR ALL " +
                    "dejaría a su Administrador crear o cerrar Sesiones Privilegiadas de Soporte TALVEG");
                transparencia.WithCheck.Should().BeNull();
                transparencia.Permisiva.Should().BeTrue();
                transparencia.Using.Should().NotBeNull()
                    .And.Subject.As<string>().Should().Contain("app_ctx_tenant_id()")
                    .And.Contain("app_es_administrador_del_tenant(",
                        "por Tenant a secas cualquier usuario del Tenant leería el historial de accesos de soporte: " +
                        "la autoridad (Administrador) se exige en la misma frontera que el aislamiento");
            }

            var politica = e.Politicas.FirstOrDefault(p => p.Nombre == PoliticaPrivilegio);
            if (politica is null) continue;

            politica.Using.Should().NotBeNull()
                .And.Subject.As<string>().Should().Contain("app_ctx_usuario_id()",
                    "la autoridad vive en la fila: se ven las que te nombran. Y la coordenada es la identidad " +
                    "autenticada — no existe ni debe existir app.usuario_plataforma_id, que incrustaría una " +
                    "afirmación de privilegio en la sesión");

            politica.WithCheck.Should().NotBeNull()
                .And.Subject.As<string>().Should().Contain("app_ctx_usuario_id()",
                    "sin WITH CHECK se podría crear o reasignar una concesión a nombre de otro usuario");
        }
    }

    /// <summary>
    /// Categoría 5: <b>catálogo del Operador CAE</b>. Las solicitudes de
    /// incorporación a cartera no llevan <c>TenantId</c>: nacen en el Operador
    /// CAE del Gestor CAE que las pide, pero se aceptan en la misma transacción
    /// que escribe la cartera, con <c>app_ctx_tenant_id()</c> puesto en el Tenant
    /// propietario (lo exige <c>posicion_en_la_asignacion</c>). Por eso su
    /// política no mira <c>app_ctx_tenant_id()</c> sino <c>app_ctx_tenant_origen_id()</c>,
    /// la organización de la cuenta, que no cambia al abrir un ámbito
    /// explícito. Sin FORCE por el mismo motivo que los catálogos de
    /// asignación: la retirada de un Tenant de demo corre como propietario.
    /// </summary>
    [Fact]
    public async Task Las_solicitudes_de_incorporacion_se_aislan_por_el_Operador_CAE_de_origen_sin_FORCE()
    {
        const string tabla = "SolicitudesIncorporacionCartera";
        var estado = await LeerEstadoRlsAsync([tabla]);

        estado.Should().ContainKey(tabla, "la migración de la solicitud tiene que haber creado la tabla");
        var (habilitado, forzado, politicas) = estado[tabla];

        using var _ = new AssertionScope();
        habilitado.Should().BeTrue("sin RLS, un Gestor CAE de otro Operador CAE leería las solicitudes ajenas");
        forzado.Should().BeFalse("con FORCE, la retirada de un Tenant de demo no vería las solicitudes que tiene que borrar");
        politicas.Select(p => p.Nombre).Should().Equal(["operador_de_la_solicitud"],
            "una política PERMISSIVE adicional se combina con OR y ensancharía el acceso");

        var politica = politicas.Single();
        foreach (var expresion in new[] { politica.Using, politica.WithCheck })
        {
            expresion.Should().NotBeNull("USING protege la lectura y WITH CHECK la escritura; hacen falta las dos")
                .And.Subject.As<string>().Should().Contain("OperadorTenantId")
                .And.Contain("app_ctx_tenant_origen_id()")
                .And.NotContain("app_ctx_tenant_id()",
                    "el Tenant activo es el propietario al aceptar; aislar por él rompería la aceptación");
        }
    }

    /// <summary>
    /// Categoría 6: <b>catálogo del Tenant propietario que el Operador CAE
    /// externo solo lee</b>. El Encargo de administración (D-8, 2026-10-08) no
    /// lleva <c>TenantId</c>: enlaza el Tenant propietario que encarga con el
    /// Operador CAE externo que recibe. Se LEE desde las dos posiciones, como
    /// las asignaciones; se ESCRIBE solo con el Tenant propietario como Tenant
    /// activo y nunca desde una conexión cuyo Tenant de origen sea el Operador
    /// CAE que lo recibe — «nadie del Operador CAE registra su propio encargo»,
    /// llevado a la base. Única excepción (2026-10-15): con encargo pleno, el
    /// Operador CAE renuncia a él, y solo escribe la fila ya retirada. Sin FORCE por el mismo motivo que los
    /// catálogos de asignación.
    /// </summary>
    [Fact]
    public async Task Los_encargos_de_administracion_se_leen_desde_las_dos_posiciones_y_no_los_escribe_el_Operador_CAE()
    {
        const string tabla = "EncargosAdministracion";
        var estado = await LeerEstadoRlsAsync([tabla]);

        estado.Should().ContainKey(tabla, "la migración del encargo tiene que haber creado la tabla");
        var (habilitado, forzado, politicas) = estado[tabla];

        using var _ = new AssertionScope();
        habilitado.Should().BeTrue("sin RLS, cualquier Tenant leería qué organizaciones han encargado su administración a quién");
        forzado.Should().BeFalse("con FORCE, la retirada de un Tenant de demo no vería los encargos que tiene que borrar");
        politicas.Select(p => p.Nombre).Should().Equal(["posicion_en_el_encargo"],
            "una política PERMISSIVE adicional se combina con OR y ensancharía el acceso");

        var politica = politicas.Single();
        politica.Permisiva.Should().BeTrue();

        politica.Using.Should().NotBeNull("USING protege la lectura")
            .And.Subject.As<string>().Should().Contain("PropietarioTenantId").And.Contain("app_ctx_tenant_id()")
            .And.Contain("OperadorTenantId").And.Contain("app_ctx_tenant_origen_id()")
            .And.Contain(" OR ", "las dos posiciones leen: el Operador CAE necesita el encargo para calcular su techo");

        politica.WithCheck.Should().NotBeNull("sin WITH CHECK, cualquiera que lo lea podría escribirlo")
            .And.Subject.As<string>().Should().Contain("PropietarioTenantId").And.Contain("app_ctx_tenant_id()")
            .And.Contain("OperadorTenantId").And.Contain("app_ctx_tenant_origen_id()")
            .And.Contain("IS DISTINCT FROM",
                "la escritura exige que el Tenant de origen de la conexión NO sea el Operador CAE del encargo")
            .And.Contain(" AND ", "las dos condiciones de escritura hacen falta a la vez");

        // Decisión del 2026-10-15 (encargo pleno): la única escritura desde la posición del Operador
        // CAE es la renuncia, una fila que queda retirada en un Tenant propietario sin Administrador
        // propio. Es la única disyunción admitida.
        var withCheck = politica.WithCheck!;
        System.Text.RegularExpressions.Regex.Matches(withCheck, " OR ").Should().HaveCount(1,
            "una disyunción más en la escritura dejaría escribir desde la posición del Operador CAE");
        withCheck[withCheck.IndexOf(" OR ", StringComparison.Ordinal)..].Should()
            .Contain("\"RetiradoEnUtc\" IS NOT NULL", "el Operador CAE solo escribe la fila si queda retirada")
            .And.MatchRegex(@"NOT (public\.)?app_tenant_tiene_administrador_propio\(""PropietarioTenantId""\)",
                "y solo con encargo pleno: en un Tenant con Administrador propio no renuncia");
    }

    /// <summary>
    /// El registro del encargo solo añade, también en los privilegios: runtime y
    /// aprovisionamiento insertan y solo actualizan las columnas de la retirada;
    /// ninguno borra ni reescribe la cláusula, la vigencia, la operación o los
    /// dos Tenants.
    /// </summary>
    [Theory]
    [InlineData("cae_app_runtime")]
    [InlineData("cae_app_aprovisionamiento")]
    public async Task El_encargo_de_administracion_no_se_borra_ni_se_reescribe_con_los_roles_de_la_aplicacion(string rol)
    {
        await using var conexion = new NpgsqlConnection(_cadenaConexion);
        await conexion.OpenAsync();

        async Task<bool> TieneAsync(string privilegio)
        {
            await using var comando = conexion.CreateCommand();
            comando.CommandText = "SELECT has_table_privilege(@rol, 'public.\"EncargosAdministracion\"', @privilegio);";
            comando.Parameters.AddWithValue("rol", rol);
            comando.Parameters.AddWithValue("privilegio", privilegio);
            return (bool)(await comando.ExecuteScalarAsync())!;
        }

        async Task<List<string>> ColumnasActualizablesAsync()
        {
            await using var comando = conexion.CreateCommand();
            comando.CommandText =
                """
                SELECT column_name FROM information_schema.columns
                WHERE table_schema = 'public' AND table_name = 'EncargosAdministracion'
                  AND has_column_privilege(@rol, 'public."EncargosAdministracion"', column_name, 'UPDATE')
                ORDER BY column_name;
                """;
            comando.Parameters.AddWithValue("rol", rol);
            var columnas = new List<string>();
            await using var lector = await comando.ExecuteReaderAsync();
            while (await lector.ReadAsync()) columnas.Add(lector.GetString(0));
            return columnas;
        }

        using var _ = new AssertionScope();
        (await TieneAsync("SELECT")).Should().BeTrue();
        (await TieneAsync("INSERT")).Should().BeTrue();
        (await TieneAsync("DELETE")).Should().BeFalse("un encargo no se borra: se retira");
        (await TieneAsync("TRUNCATE")).Should().BeFalse();
        (await TieneAsync("UPDATE")).Should().BeFalse("UPDATE de tabla dejaría reescribir la cláusula o la vigencia");
        (await ColumnasActualizablesAsync()).Should().Equal(["RetiradoEnUtc", "RetiradoPorUsuarioId", "Version"],
            "la única transición es retirar");
    }

    /// <summary>
    /// Categoría 5 también: las <b>propuestas de apoyo</b> entre Gestores CAE de un mismo
    /// Operador CAE. Mismo motivo que la solicitud de incorporación: se aceptan en la transacción
    /// que escribe la cartera, con <c>app_ctx_tenant_id()</c> en el Tenant propietario, así que se
    /// aíslan por <c>app_ctx_tenant_origen_id()</c>. Lo que la política NO decide es quién, dentro del
    /// Operador CAE, puede aceptar cada una: eso es del Command (solo el destinatario).
    /// </summary>
    [Fact]
    public async Task Las_propuestas_de_apoyo_se_aislan_por_el_Operador_CAE_de_origen_sin_FORCE()
    {
        const string tabla = "PropuestasApoyoCartera";
        var estado = await LeerEstadoRlsAsync([tabla]);

        estado.Should().ContainKey(tabla, "la migración de la propuesta tiene que haber creado la tabla");
        var (habilitado, forzado, politicas) = estado[tabla];

        using var _ = new AssertionScope();
        habilitado.Should().BeTrue("sin RLS, un Gestor CAE de otro Operador CAE leería y aceptaría las propuestas ajenas");
        forzado.Should().BeFalse("con FORCE, la retirada de un Tenant de demo no vería las propuestas que tiene que borrar");
        politicas.Select(p => p.Nombre).Should().Equal(["operador_de_la_propuesta"],
            "una política PERMISSIVE adicional se combina con OR y ensancharía el acceso");

        var politica = politicas.Single();
        foreach (var expresion in new[] { politica.Using, politica.WithCheck })
        {
            expresion.Should().NotBeNull("USING protege la lectura y WITH CHECK la escritura; hacen falta las dos")
                .And.Subject.As<string>().Should().Contain("OperadorTenantId")
                .And.Contain("app_ctx_tenant_origen_id()")
                .And.NotContain("app_ctx_tenant_id()",
                    "el Tenant activo es el propietario al aceptar; aislar por él rompería la aceptación");
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

        texto.Should().Contain("ContextoSesionRls(tenantId, tenantOrigenId, usuarioId", "la coordenada de identidad tiene que seguir firmándose");
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

    private sealed record PoliticaRls(
        string Nombre, string? Using, string? WithCheck, bool Permisiva = true, char Comando = '*');

    private static bool EsRestrictivaRevisada(string tabla, PoliticaRls politica) =>
        !politica.Permisiva
        && PoliticasRestrictivasRevisadas.Any(r => r.Tabla == tabla && r.Politica == politica.Nombre);

    private static bool EsLecturaDePlataformaRevisada(string tabla, PoliticaRls politica) =>
        politica.Permisiva
        && LecturasDePlataformaRevisadas.TryGetValue(tabla, out var revisada)
        && revisada.Politica == politica.Nombre;

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
       pg_get_expr(p.polwithcheck, p.polrelid),
       p.polpermissive,
       p.polcmd
FROM pg_class c
JOIN pg_namespace n ON n.oid = c.relnamespace
LEFT JOIN pg_policy p ON p.polrelid = c.oid
WHERE n.nspname = 'public' AND c.relkind IN ('r', 'p') AND c.relname = ANY(@tablas);";
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
                    lector.IsDBNull(5) ? null : lector.GetString(5),
                    lector.GetBoolean(6),
                    lector.GetChar(7)));
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
