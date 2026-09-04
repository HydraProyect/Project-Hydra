using System.Text;
using System.Text.RegularExpressions;
using CaeManager.Domain.Tenants;
using FluentAssertions;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// <b>Los sitios que dependen de <c>Tenant.EsPlataforma</c> son exactamente estos, y
/// solo pueden decrecer.</b>
///
/// <para>
/// A4 es un <b>trinquete inicial</b> (decisión D-1, opción b): se congela la lista
/// antes de que B y C empiecen, no después. El motivo no es teórico —
/// <c>RetiradaTenantDemoService</c> apareció como consumidor nuevo entre el informe
/// de readiness y hoy, sin que nada lo dijera—. Cada retirada de B o de C tendrá que
/// pasar por esta lista; esa fricción es el producto, no un efecto secundario.
/// </para>
///
/// <para>
/// <b>La propiedad vigilada no es "el texto aparece N veces".</b> Es: <i>el conjunto de
/// sitios de <c>src/</c> que dependen del flag —por lectura, escritura, nombre o
/// metadatos— es exactamente el enumerado, con su categoría y su motivo</i>. Por eso hay
/// tres instrumentos con puntos ciegos distintos, y por eso la comparación es de
/// <b>igualdad</b>, nunca de umbral.
/// </para>
///
/// <para>
/// <b>Por qué igualdad y no <c>&lt;=</c>.</b> Un umbral deja morir entradas en silencio:
/// una retirada de B que no actualiza la lista pasaría en verde, y la lista seguiría
/// afirmando una revisión que ya no corresponde al código. La auditoría de ratchets del
/// 2026-08-23 encontró exactamente eso en 13 de 14 ratchets — nunca fallaban porque no
/// miraban. Aquí el rojo por <b>defecto</b> de apariciones es tan válido como el rojo por
/// exceso. Si esto molesta, la respuesta es actualizar la lista en el mismo commit que
/// retira el uso, no aflojar la comparación.
/// </para>
///
/// <para>
/// <b>Por qué la clave no lleva número de línea.</b> Los números churnean con cualquier
/// edición y convertirían el ratchet en ruido que se aprende a re-sellar sin leer. Van en
/// el motivo, como documentación de dónde mirar.
/// </para>
///
/// <para>
/// <b>Por qué se cuentan también los comentarios.</b> Editar un <c>&lt;summary&gt;</c> que
/// menciona el flag pondrá esto en rojo, y es coste real. Filtrarlos exigiría un parser
/// que distinga comentario de código y que, cuando se equivoque, falle <b>hacia verde</b>
/// y en silencio — además de reintroducir el juicio de forma que es el cuarto modo de
/// fallo de aquella auditoría. Varios de esos comentarios documentan <b>por qué NO</b> se
/// consulta el flag, que es justo lo que B y C necesitan leer antes de tocar nada.
/// </para>
///
/// <para>
/// <b>Convivencia con <see cref="RaizDeBootstrapConUnSoloConsumidorTests"/>.</b> Aquél
/// prohíbe el flag en <b>un fichero concreto</b>, la apertura de sesión privilegiada, que
/// no aparece en esta lista (cero apariciones). No hay solapamiento ni contradicción:
/// reintroducirlo allí pondría los dos en rojo, con mensajes distintos y ambos correctos.
/// Este ratchet <b>no</b> debe absorber aquella aserción — duplicarla la debilitaría al
/// dejar de estar acotada al fichero que importa.
/// </para>
///
/// <para>
/// <b>Lo que este ratchet NO demuestra</b>, para que nadie lo dé por demostrado:
/// (1) que los seis usos de autoridad sean <i>correctos</i> — congela que existan y que
/// sean seis, no que autoricen bien; (2) que no exista autoridad equivalente por otra vía
/// — reconstruir "es el tenant #1" comparando contra <c>TenantSeedData.IdPorDefecto</c>
/// no se vería aquí, y es justo lo que <c>Tenant</c> dice evitar a propósito; (3) nada
/// sobre SQL vivo en la base (políticas RLS, vistas, triggers que consulten la columna);
/// (4) un acceso por reflexión con el nombre <i>calculado en tiempo de ejecución</i>;
/// (5) nada sobre <c>tests/</c>, que queda fuera por definición de la propiedad —
/// "ningún consumidor de producción nuevo"—, no por descuido; (6) la granularidad es de
/// fichero, así que sustituir un uso de autoridad por otro dentro del mismo fichero
/// mantiene el conteo y pasa en verde. Es un hueco, no una virtud.
/// </para>
/// </summary>
public class UsosDeEsPlataformaCongeladosTests
{
    private enum CategoriaUso
    {
        /// <summary>Decide quién puede hacer algo. Es lo que B y C tienen que retirar.</summary>
        Autoridad,

        /// <summary>Regla de negocio sobre suscripciones. No autoriza a nadie.</summary>
        ReglaComercial,

        /// <summary>Mitad negativa de un fallo cerrado: impide, no concede.</summary>
        Guarda,

        /// <summary>Siembra y arranque.</summary>
        Bootstrap,

        /// <summary>La propiedad misma y sus mutadores.</summary>
        Portador,

        /// <summary>Solo texto. Varios documentan por qué NO se consulta el flag.</summary>
        Comentario,

        /// <summary>Migración escrita a mano, no generada.</summary>
        MigracionManual,
    }

    private sealed record EntradaBlanca(int Apariciones, CategoriaUso Categoria, string Motivo);

    /// <summary>
    /// Identificadores vigilados. Son disjuntos como texto: <c>MarcarComoPlataforma</c> y
    /// <c>DejarDeSerPlataforma</c> no contienen <c>EsPlataforma</c> como subcadena, así que
    /// cada uno suma por separado y una línea puede aportar dos apariciones.
    ///
    /// <para>
    /// <c>DejarDeSerPlataforma</c> <b>ya no existe</b> —A4.2 lo retiró por código muerto— y
    /// aun así se sigue vigilando <b>a propósito</b>: hoy aporta cero, y el día que alguien
    /// lo reintroduzca el conteo subirá y esto se pondrá rojo. Quitarlo del patrón sería
    /// perder la guarda contra su resurrección justo después de haberlo retirado.
    /// </para>
    /// </summary>
    private static readonly Regex Patron = new(
        @"\b(EsPlataforma|MarcarComoPlataforma|DejarDeSerPlataforma)\b", RegexOptions.Compiled);

    private static readonly string[] ExtensionesVigiladas =
        [".cs", ".razor", ".cshtml", ".sql", ".json", ".csproj", ".yml"];

    /// <summary>
    /// Medido el 2026-08-29: <b>22 ficheros, 34 apariciones</b>. Eran 36 sobre
    /// <c>2e463b1d</c>, antes de que A4.2 retirase <c>DejarDeSerPlataforma</c> y sus dos
    /// apariciones.
    /// Actualizado 2026-09-04 (HO-035-02, REC-035): <b>23 ficheros, 35 apariciones</b> —
    /// nuevo <c>RegistrarInstruccionTratamientoIaTenantPropietarioCommand.cs</c>
    /// (1 aparición, <see cref="CategoriaUso.ReglaComercial"/>, mismo criterio que
    /// <c>RegistrarSuscripcionTenantCommand</c>).
    ///
    /// <para>
    /// Cada entrada se leyó una a una; el conteo <b>no</b> se ajustó a lo que salió del
    /// escaneo — eso es precisamente cómo mueren estos ratchets. Así se explicó, por
    /// ejemplo, que <c>Tenant.cs</c> aportara 7 y no 4: las líneas del mutador contienen
    /// dos identificadores vigilados cada una.
    /// </para>
    ///
    /// <para>
    /// <b>Este número se mantiene a mano y ninguna aserción lo lee.</b> Si se desalinea del
    /// árbol —pasó en A4.2, que actualizó la entrada de <c>Tenant.cs</c> y olvidó esta
    /// cifra— el ratchet sigue siendo correcto, pero el instrumento estaría declarando un
    /// total que ya no es el suyo. Al cambiar cualquier entrada, cuadra también esta suma.
    /// </para>
    /// </summary>
    private static readonly Dictionary<string, EntradaBlanca> Autorizados = new()
    {
        // ── AUTORIDAD ─────────────────────────────────────────────────────────────
        // Los seis comparten predicado y lo evalúan contra el tenant de ORIGEN, nunca
        // contra ITenantActual: quien ya opera un tenant ajeno podría usar el workspace
        // activo para abrirse acceso a otros.
        ["src/CaeManager.Application/ApiKeys/Commands/GenerarClaveApi/GenerarClaveApiCommand.cs"] =
            new(1, CategoriaUso.Autoridad, ":63 — lo retira el bloque C (D-7: direccionamiento propio)"),
        ["src/CaeManager.Application/ApiKeys/Commands/RevocarClaveApi/RevocarClaveApiCommand.cs"] =
            new(1, CategoriaUso.Autoridad, ":43 — lo retira el bloque C"),
        ["src/CaeManager.Application/ApiKeys/Queries/ObtenerClavesApi/ObtenerClavesApiQuery.cs"] =
            new(1, CategoriaUso.Autoridad, ":36 — lo retira el bloque C"),
        ["src/CaeManager.Application/Tenants/Commands/AbrirAccesoSoporte/AbrirAccesoSoporteCommand.cs"] =
            new(1, CategoriaUso.Autoridad, ":91 — lo retira el bloque B (el comando entero desaparece)"),
        ["src/CaeManager.Application/Tenants/Commands/CerrarAccesoSoporte/CerrarAccesoSoporteCommand.cs"] =
            new(1, CategoriaUso.Autoridad, ":46 — lo retira el bloque B (el comando entero desaparece)"),
        ["src/CaeManager.Application/Tenants/Queries/ObtenerActividadSoporte/ObtenerActividadSoporteQuery.cs"] =
            new(1, CategoriaUso.Autoridad,
                ":55, en OR con la vía del cliente visitado. B reescribe el predicado por concesión propia. " +
                "AVISO: D-6 obliga a conservar la rama del cliente, y ESTE RATCHET NO LO COMPRUEBA — solo " +
                "cuenta el flag. Si B rompiera únicamente esa rama, el conteo seguiría en 1 y esto pasaría en " +
                "verde. Hoy la rama del cliente tiene cobertura CERO en los cinco proyectos de test, así que " +
                "nada la protege; B debe añadir esa prueba y enlazarla desde aquí. No leas esta entrada como " +
                "cobertura de la vía del cliente: no lo es"),

        // ── REGLA COMERCIAL ───────────────────────────────────────────────────────
        ["src/CaeManager.Application/Common/GateComercialTenantBehavior.cs"] =
            new(2, CategoriaUso.ReglaComercial,
                ":50 proyección y :53 exención — DOS apariciones en el mismo fichero, que es " +
                "exactamente por lo que esta lista lleva conteo y no solo ruta. D-4 (d) retira la exención " +
                "sin compensación: el tenant #1 nace en SinSuscripcion, que ya pasa el gate"),
        ["src/CaeManager.Application/Comercial/Commands/RegistrarSuscripcionTenant/RegistrarSuscripcionTenantCommand.cs"] =
            new(1, CategoriaUso.ReglaComercial,
                ":69 rechaza suscribir al tenant de plataforma. D-4: SE CONSERVA — es el invariante que " +
                "impide que el tenant #1 salga de SinSuscripcion, y sin él la retirada de la exención sí " +
                "tendría delta de comportamiento"),
        ["src/CaeManager.Application/Comercial/Queries/ObtenerEstadoComercialTenants/ObtenerEstadoComercialTenantsQuery.cs"] =
            new(1, CategoriaUso.ReglaComercial,
                ":53 filtro de presentación; la autorización real está tres líneas antes, en " +
                "PuedeGlobalmenteAsync. Se conserva"),
        ["src/CaeManager.Application/Cumplimiento/Commands/RegistrarInstruccionTratamientoIaTenantPropietario/RegistrarInstruccionTratamientoIaTenantPropietarioCommand.cs"] =
            new(1, CategoriaUso.ReglaComercial,
                ":75 rechaza registrar la instrucción de tratamiento IA (REC-035) contra el tenant de " +
                "plataforma — mismo criterio que RegistrarSuscripcionTenantCommand:69: TALVEG no se " +
                "instruye tratamiento a sí misma. La autorización real es PuedeSobreTenantAsync, dos " +
                "líneas antes"),

        // ── GUARDA ────────────────────────────────────────────────────────────────
        ["src/CaeManager.Infrastructure/MultiTenancy/RetiradaTenantDemoService.cs"] =
            new(2, CategoriaUso.Guarda,
                ":139 rechaza retirar el tenant de plataforma, :55 lo documenta. No concede capacidad a " +
                "nadie: es la mitad negativa de un fallo cerrado, y el servicio ya lleva una segunda barrera " +
                "independiente (allowlist de nombres de demo). Entró en d4114d22 (#312), DESPUÉS de que el " +
                "informe de readiness levantara su inventario — es el consumidor que justifica este ratchet"),

        // ── BOOTSTRAP ─────────────────────────────────────────────────────────────
        ["src/CaeManager.Infrastructure/Persistence/Configurations/TenantConfiguration.cs"] =
            new(3, CategoriaUso.Bootstrap,
                ":63 HasData — única vía de producción que fija el flag; :61 y :71 lo documentan"),
        ["src/CaeManager.Infrastructure/Persistence/Seed/DelegacionesSoporteSeeder.cs"] =
            new(2, CategoriaUso.Bootstrap,
                ":31 localiza el tenant de plataforma, :42 decide a quién aprovisionar. Los retira B: el " +
                "seeder se elimina, porque ConcesionesSoloPorActoExplicitoTests ya nombra su " +
                "pre-aprovisionamiento como el antipatrón a no repetir en el plano 3"),

        // ── PORTADOR ──────────────────────────────────────────────────────────────
        ["src/CaeManager.Domain/Tenants/Tenant.cs"] =
            new(5, CategoriaUso.Portador,
                ":35 propiedad, :87 asignación en el ctor, :93 comentario y :104 MarcarComoPlataforma, que " +
                "aporta DOS apariciones porque el nombre del método y el del campo casan ambos. Eran 7 hasta " +
                "A4.2: DejarDeSerPlataforma se retiró por código muerto —una sola aparición en todo el repo, " +
                "su propia definición— y el ratchet se puso rojo con «= 5 (la lista dice 7)» hasta actualizar " +
                "este número. Ésa es la razón de que la comparación sea de igualdad y no de umbral"),

        // ── COMENTARIO ────────────────────────────────────────────────────────────
        ["src/CaeManager.Application/Plataforma/IAutorizacionAdminPlataforma.cs"] =
            new(1, CategoriaUso.Comentario, ":10 — documenta que la capacidad NO consulta el flag"),
        ["src/CaeManager.Application/Plataforma/IRaizBootstrapPlataforma.cs"] =
            new(2, CategoriaUso.Comentario, ":8 y :19 — documentan que dejó de ser autoridad transversal"),
        ["src/CaeManager.Application/Plataforma/Commands/AutoConcederPrivilegio/AutoConcederPrivilegioCommand.cs"] =
            new(2, CategoriaUso.Comentario, ":123 y :126 — la superficie que le queda como autoridad"),
        ["src/CaeManager.Application/Plataforma/Queries/PuedeInicializarPlataforma/PuedeInicializarPlataformaQuery.cs"] =
            new(1, CategoriaUso.Comentario, ":27 — advierte contra comparar aquí contra el flag"),
        ["src/CaeManager.Application/Tenants/IAutorizacionDelegacionTenant.cs"] =
            new(1, CategoriaUso.Comentario, ":18 — documenta por qué esta autorización NO lo consulta"),
        ["src/CaeManager.Application/Tenants/Queries/EsAdministradorPlataforma/EsAdministradorPlataformaQuery.cs"] =
            new(1, CategoriaUso.Comentario, ":21 — declara la dependencia como transitoria"),
        ["src/CaeManager.Infrastructure/DependencyInjection/InfrastructureServiceCollectionExtensions.cs"] =
            new(1, CategoriaUso.Comentario, ":440 — documenta que NO se consulta a propósito"),
        ["src/CaeManager.Infrastructure/Persistence/Seed/DelegacionDemoSeeder.cs"] =
            new(1, CategoriaUso.Comentario, ":20"),

        // ── MIGRACIÓN MANUAL ──────────────────────────────────────────────────────
        // Las migraciones se excluyen por SUFIJO de nombre generado, no por carpeta. Es
        // deliberado: excluir la carpeta entera —lo cómodo— dejaría un agujero por el que
        // se puede conceder autoridad en SQL crudo sin que nada lo vea.
        ["src/CaeManager.Migrations.PostgreSQL/Migrations/20260731235023_LineaBase.cs"] =
            new(2, CategoriaUso.MigracionManual, ":722 DDL de la columna, :1026 fila sembrada"),
    };

    // ══════════════════════════════════════════════════════════════════════════════
    // INSTRUMENTO 1 — barrido de fuente por identificador
    // ══════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Los_usos_de_EsPlataforma_en_el_codigo_fuente_son_exactamente_los_congelados()
    {
        var (conteos, _) = EscanearFuente();

        var esperado = Autorizados.ToDictionary(e => e.Key, e => e.Value.Apariciones);

        var sobrantes = conteos
            .Where(c => !esperado.TryGetValue(c.Key, out var n) || n != c.Value)
            .Select(c => $"{c.Key} = {c.Value}" +
                         (esperado.TryGetValue(c.Key, out var n) ? $" (la lista dice {n})" : " (RUTA NUEVA)"))
            .OrderBy(x => x);

        var faltantes = esperado
            .Where(e => !conteos.ContainsKey(e.Key))
            .Select(e => $"{e.Key} = {e.Value} (LA LISTA LO AFIRMA, EL ÁRBOL NO LO TIENE)")
            .OrderBy(x => x);

        string.Join(Environment.NewLine, sobrantes.Concat(faltantes)).Should().BeEmpty(
            "el conjunto de sitios que dependen de Tenant.EsPlataforma está congelado por A4 (decisión D-1). " +
            "Una ruta nueva es un consumidor sin clasificar: dale categoría y motivo en Autorizados, en este " +
            "mismo commit, o no entra. Un conteo distinto en una ruta conocida es una lectura añadida o " +
            "retirada dentro de un fichero ya autorizado — si la retiró B o C, actualiza el número aquí; si " +
            "la añadió alguien, justifícala. Y un conteo que la lista afirma pero el árbol no tiene es una " +
            "entrada muerta: la lista estaría declarando una revisión que ya no corresponde al código");
    }

    /// <summary>
    /// Guarda anti-vacío. Corre <b>el mismo</b> escaneo que vigila el test de arriba — no
    /// una reimplementación —, porque el primer modo de fallo de la auditoría del
    /// 2026-08-23 fue exactamente ése: una guarda que sumaba su propia lista y dejaba el
    /// ratchet inerte con los dos tests en verde.
    /// </summary>
    [Fact]
    public void El_escaneo_de_fuente_mira_donde_dice_mirar()
    {
        var (conteos, universo) = EscanearFuente();

        conteos.Should().NotBeEmpty("un escaneo que no encuentra nada no está vigilando nada");

        universo.Count.Should().BeGreaterThan(1200,
            "el árbol tenía ~1570 ficheros con extensión vigilada bajo src/ el 2026-08-29; un universo muy " +
            "por debajo significa que el barrido dejó de recorrer lo que cree recorrer");

        universo.Should().Contain(r => r.EndsWith(".razor", StringComparison.OrdinalIgnoreCase),
            "un bloque @code es C#: si los .razor se caen del barrido, la mitad de la UI deja de estar vigilada");

        universo.Should().Contain(r => r.StartsWith("src/CaeManager.Web/", StringComparison.Ordinal),
            "CaeManager.Web entra en el barrido; dejarlo fuera fue el tercer modo de fallo de la auditoría");

        conteos.Should().ContainKey("src/CaeManager.Domain/Tenants/Tenant.cs",
            "centinela: si el escaneo deja de ver el fichero que DEFINE la propiedad, todo lo demás es aire");
    }

    /// <summary>
    /// La exclusión por sufijo de fichero generado tiene que estar excluyendo algo. Una
    /// exclusión que no excluye nada es una condición muerta que nadie volvería a mirar.
    /// </summary>
    [Fact]
    public void La_exclusion_de_ficheros_generados_sigue_excluyendo_algo()
    {
        var raiz = RaizDelRepositorio();
        var src = Path.Combine(raiz, "src");

        var generados = Directory
            .EnumerateFiles(src, "*", SearchOption.AllDirectories)
            .Where(NoEstaEnBinNiObj)
            .Count(EsGenerado);

        generados.Should().BeGreaterThan(50,
            "había 113 snapshots EF el 2026-08-29; si esto baja a cero, el filtro por sufijo dejó de casar y " +
            "los ~220 falsos positivos de los snapshots entrarían en el conteo");
    }

    // ══════════════════════════════════════════════════════════════════════════════
    // INSTRUMENTO 2 — barrido del ensamblado compilado
    // ══════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// El barrido de fuente tiene un punto ciego: el literal partido
    /// (<c>"EsPlat" + "aforma"</c>, que Roslyn pliega en compilación) y la dependencia
    /// cableada sin nombrar el identificador. Ambos dejan el nombre en el heap
    /// <c>#Strings</c> del ensamblado, así que se ven aquí y no allí.
    ///
    /// <para>
    /// <c>CaeManager.Web</c> tiene <b>cero</b> apariciones en fuente, y esa es la
    /// afirmación que esta línea protege de un plumazo para los 164 <c>.razor</c> y sus
    /// code-behind, sin depender de que el barrido de texto acierte con el formato.
    /// </para>
    /// </summary>
    [Fact]
    public void El_ensamblado_de_Web_no_depende_de_EsPlataforma()
    {
        ContieneLaSecuencia(BytesDelEnsamblado("CaeManager.Web.dll"), "EsPlataforma")
            .Should().BeFalse(
                "CaeManager.Web no consulta el flag por ninguna vía. Si esto se pone rojo, alguien introdujo " +
                "una dependencia en la capa web —posiblemente en un .razor, posiblemente con el identificador " +
                "partido o cableado— y hay que clasificarla antes de aceptarla");
    }

    /// <summary>
    /// Prueba de vida del instrumento 2. Sin ella, un barrido que no encontrase la
    /// secuencia en <i>ningún</i> ensamblado pasaría el test de arriba por vacuidad —
    /// que es el falso negativo perfecto.
    /// </summary>
    [Fact]
    public void El_barrido_de_ensamblados_sabe_encontrar_la_secuencia()
    {
        ContieneLaSecuencia(BytesDelEnsamblado("CaeManager.Domain.dll"), "EsPlataforma")
            .Should().BeTrue(
                "CaeManager.Domain DEFINE la propiedad, así que su nombre está en el heap de cadenas. Si esto " +
                "falla, el instrumento no sabe leer ensamblados y su compañero verde no significa nada");

        ContieneLaSecuencia(BytesDelEnsamblado("CaeManager.Domain.dll"), "EstaSecuenciaNoExisteEnNingunEnsamblado")
            .Should().BeFalse(
                "control negativo: si el buscador dijera que sí a cualquier cosa, el control positivo de " +
                "arriba también saldría verde y ninguno de los dos significaría nada");
    }

    // ══════════════════════════════════════════════════════════════════════════════
    // INSTRUMENTO 3 — reflexión sobre la superficie pública de Tenant
    // ══════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Se compara el <b>conjunto completo</b> de miembros públicos declarados, no la
    /// ausencia de un nombre concreto. Vigilar un nombre exacto fue el cuarto modo de
    /// fallo de la auditoría: se esquiva renombrando, sin tocar el ratchet.
    /// </summary>
    [Fact]
    public void La_superficie_publica_de_Tenant_no_gana_mutadores_del_flag()
    {
        var miembros = typeof(Tenant)
            .GetMembers(System.Reflection.BindingFlags.Public
                        | System.Reflection.BindingFlags.Instance
                        | System.Reflection.BindingFlags.Static
                        | System.Reflection.BindingFlags.DeclaredOnly)
            .Where(m => m.MemberType is System.Reflection.MemberTypes.Method
                                     or System.Reflection.MemberTypes.Property)
            .Select(m => m.Name)
            .Where(n => !n.StartsWith("get_", StringComparison.Ordinal)
                        && !n.StartsWith("set_", StringComparison.Ordinal))
            .Distinct()
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        miembros.Should().BeEquivalentTo(SuperficiePublicaDeTenant,
            "cualquier método público nuevo en Tenant puede ser un mutador del flag con otro nombre. Si el " +
            "miembro es legítimo, añádelo a SuperficiePublicaDeTenant en el mismo commit; si retiras " +
            "DejarDeSerPlataforma (A4.2), quítalo de aquí y baja el conteo de Tenant.cs de 7 a 5");
    }

    [Fact]
    public void EsPlataforma_conserva_su_forma_y_no_tiene_setter_publico()
    {
        var propiedad = typeof(Tenant).GetProperty(nameof(Tenant.EsPlataforma));

        propiedad.Should().NotBeNull("es el portador que este ratchet vigila");
        propiedad!.PropertyType.Should().Be(typeof(bool));
        propiedad.GetSetMethod(nonPublic: false).Should().BeNull(
            "un setter público convertiría el flag en algo que cualquiera puede cambiar desde fuera del dominio");
    }

    /// <summary>
    /// Evita el "DTO portador": un tipo de Application o de Web que reexporte el flag y
    /// lo convierta en autoridad de facto sin que el nombre aparezca en un predicado.
    /// </summary>
    [Fact]
    public void Ningun_tipo_publico_de_Application_ni_de_Web_reexporta_el_flag()
    {
        var ensamblados = new[]
        {
            typeof(CaeManager.Application.Common.AmbitoTenantExplicito).Assembly,
            typeof(CaeManager.Web.Services.CabecerasSeguridadExtensions).Assembly,
        };

        var reexportadores = ensamblados
            .SelectMany(a => a.GetExportedTypes())
            .SelectMany(t => t.GetMembers(System.Reflection.BindingFlags.Public
                                          | System.Reflection.BindingFlags.Instance
                                          | System.Reflection.BindingFlags.Static
                                          | System.Reflection.BindingFlags.DeclaredOnly)
                .Where(m => m.Name == "EsPlataforma")
                .Select(m => $"{t.FullName}.{m.Name}"))
            .Distinct()
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        string.Join(Environment.NewLine, reexportadores).Should().BeEmpty(
            "reexportar el flag en un DTO lo convierte en autoridad de facto en una capa que este ratchet " +
            "vigila por texto, donde el predicado ya no menciona a Tenant");
    }

    /// <summary>
    /// La superficie pública de <see cref="Tenant"/> el 2026-08-29. No es una lista de
    /// prohibidos: es el conjunto entero, para que un mutador nuevo no pueda colarse con
    /// cualquier nombre.
    /// </summary>
    private static readonly string[] SuperficiePublicaDeTenant =
    [
        "ActualizarEstadoComercial",
        "CambiarPerfilVocabulario",
        "CreadoEnUtc",
        "DatosDemoCompletadosEnUtc",
        "EsPlataforma",
        "Estado",
        "EstadoComercial",
        "EstadoComercialActualizadoEnUtc",
        "MarcarComoPlataforma",
        "MarcarDatosDemoCompletados",
        "Nombre",
        "PerfilVocabulario",
        "Reactivar",
        "RenombrarA",
        "StripeCustomerId",
        "StripeSubscriptionId",
        "Suspender",
        "VincularSuscripcionStripe",
    ];

    // ══════════════════════════════════════════════════════════════════════════════
    // Mecánica compartida
    // ══════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Único punto de descubrimiento. Devuelve también el universo recorrido para que las
    /// guardas puedan comprobar el alcance sobre <b>este mismo</b> barrido.
    /// </summary>
    private static (Dictionary<string, int> Conteos, List<string> Universo) EscanearFuente()
    {
        var raiz = RaizDelRepositorio();
        var src = Path.Combine(raiz, "src");

        var conteos = new Dictionary<string, int>(StringComparer.Ordinal);
        var universo = new List<string>();

        foreach (var archivo in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
        {
            if (!NoEstaEnBinNiObj(archivo)) continue;
            if (!ExtensionesVigiladas.Contains(Path.GetExtension(archivo), StringComparer.OrdinalIgnoreCase))
                continue;

            var relativa = Path.GetRelativePath(raiz, archivo).Replace(Path.DirectorySeparatorChar, '/');
            universo.Add(relativa);

            if (EsGenerado(archivo)) continue;

            var apariciones = Patron.Matches(File.ReadAllText(archivo)).Count;
            if (apariciones > 0) conteos[relativa] = apariciones;
        }

        return (conteos, universo);
    }

    /// <summary>
    /// Los snapshots de EF se excluyen por <b>sufijo de nombre</b>, nunca por carpeta: una
    /// migración escrita a mano que toque el flag tiene que poner esto en rojo.
    /// </summary>
    private static bool EsGenerado(string archivo)
    {
        var nombre = Path.GetFileName(archivo);
        return nombre.EndsWith(".Designer.cs", StringComparison.OrdinalIgnoreCase)
               || nombre.EndsWith("ModelSnapshot.cs", StringComparison.OrdinalIgnoreCase);
    }

    private static bool NoEstaEnBinNiObj(string archivo) =>
        !archivo.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
        && !archivo.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal);

    /// <summary>
    /// Búsqueda de <b>subsecuencia contigua</b>, escrita a mano a propósito.
    ///
    /// <para>
    /// La primera versión usaba <c>Should().NotContain(bytes)</c> de FluentAssertions, que
    /// sobre una colección compara <b>elemento a elemento</b>: comprobaba si el ensamblado
    /// contiene los bytes de <c>'E'</c>, <c>'s'</c>, <c>'P'</c>… por separado, cosa que
    /// cumple cualquier binario. El test negativo fallaba siempre y —lo grave— el control
    /// positivo pasaba <b>vacuamente</b>. Es el cuarto modo de fallo de la auditoría del
    /// 2026-08-23 en su forma más pura: el instrumento medía otra cosa que la prometida, y
    /// solo se vio al leer los bytes del mensaje de error.
    /// </para>
    /// </summary>
    private static bool ContieneLaSecuencia(byte[] heno, string aguja)
    {
        var patron = Encoding.UTF8.GetBytes(aguja);
        if (patron.Length == 0 || heno.Length < patron.Length) return false;

        for (var i = 0; i <= heno.Length - patron.Length; i++)
        {
            var j = 0;
            while (j < patron.Length && heno[i + j] == patron[j]) j++;
            if (j == patron.Length) return true;
        }

        return false;
    }

    private static byte[] BytesDelEnsamblado(string nombre)
    {
        var ruta = Path.Combine(AppContext.BaseDirectory, nombre);

        File.Exists(ruta).Should().BeTrue(
            $"un barrido de bytes sobre un fichero ausente es el falso negativo perfecto: {nombre} tiene que " +
            "estar en la salida del test para que su ausencia de la secuencia signifique algo");

        return File.ReadAllBytes(ruta);
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
