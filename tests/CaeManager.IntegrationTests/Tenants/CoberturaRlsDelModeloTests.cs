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
            .And.Subject.As<string>().Should().Contain("UsuarioRaizId").And.Contain("app.usuario_id",
                "solo la identidad raíz ve la fila: para cualquier otro usuario la tabla está vacía");

        var designacion = e.Politicas.FirstOrDefault(p => p.Nombre == "estado_bootstrap_designacion_al_arrancar");
        designacion.Should().NotBeNull();
        designacion!.WithCheck.Should().NotBeNull()
            .And.Subject.As<string>().Should().Contain("app.usuario_id",
                "la designación la escribe el ARRANQUE, que no es una sesión de usuario: la ausencia de " +
                "app.usuario_id es el discriminante, y es una coordenada del modelo, no una propiedad del rol");

        var consumo = e.Politicas.FirstOrDefault(p => p.Nombre == "estado_bootstrap_consumo_por_la_raiz");
        consumo.Should().NotBeNull();
        consumo!.Using.Should().NotBeNull()
            .And.Subject.As<string>().Should().Contain("app.usuario_id");
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
                .And.Subject.As<string>().Should().Contain("app_es_admin_plataforma_global(").And.Contain("app.usuario_id",
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

    /// <summary>
    /// Categoría 5: <b>catálogo del Operador CAE</b>. Las solicitudes de
    /// incorporación a cartera no llevan <c>TenantId</c>: nacen en el Operador
    /// CAE del Gestor CAE que las pide, pero se aceptan en la misma transacción
    /// que escribe la cartera, con <c>app.tenant_id</c> puesto en el Tenant
    /// propietario (lo exige <c>posicion_en_la_asignacion</c>). Por eso su
    /// política no mira <c>app.tenant_id</c> sino <c>app.tenant_origen_id</c>,
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
                .And.Contain("app.tenant_origen_id")
                .And.NotContain("app.tenant_id'",
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
