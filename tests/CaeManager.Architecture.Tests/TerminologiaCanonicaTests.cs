using System.Text.RegularExpressions;
using FluentAssertions;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// <b>Trinquete de deuda terminológica</b> — § 5 del contrato de terminología TALVEG
/// (<c>CONTRATO_TERMINOLOGIA.md</c>, repositorio de negocio).
///
/// <para>
/// <b>Por qué existe.</b> El contrato prohíbe renombrar automáticamente estos nombres
/// —el renombrado es un incremento propio, con sus productores, consumidores, tests y
/// ratchets (§ 5 y § 6.4)—, así que la deuda se queda mientras tanto. Lo que no debe
/// quedarse es que <b>crezca sin que nadie lo vea</b>: este fichero se escribió una
/// primera vez el 2026-08-31 en la rama <c>claude/b1-3-cierre-sesion-soporte</c>, que
/// nunca tuvo PR y no llegó a <c>main</c> — la deuda siguió creciendo esos días sin
/// ningún instrumento que lo notara. El detalle completo de esa historia, con sus
/// cifras concretas, está en el registro de la Oficina de Reconciliación (REC-171) y
/// no se repite aquí: esta versión recalibra la línea base contra el árbol real en
/// lugar de rescatar los números de aquella rama, precisamente porque llevaban días
/// envejeciendo y ya discrepaban entre sí en el propio fichero original — el mismo
/// defecto de fondo por el que la Oficina degradó REC-155 a P3, un comentario con
/// cifras muertas conviviendo con un mecanismo que sí mide. Regla de este fichero en
/// adelante: <b>ninguna cifra viaja en la prosa si no está también verificada por el
/// mecanismo de abajo para el commit actual</b> — evita reproducir aquí, aunque sea a
/// título histórico, números que ya no se pueden volver a medir.
/// </para>
///
/// <para>
/// <b>Igualdad exacta, no un techo.</b> Mismo criterio que
/// <see cref="UsosDeEsPlataformaCongeladosTests"/>: si el número baja, este test se pone
/// rojo y obliga a actualizarlo en el mismo commit que lo bajó. Un <c>&lt;=</c> dejaría
/// el inventario mintiendo hacia abajo, y el objetivo es que la cifra de aquí sea
/// citable sin volver a medirla — hasta que alguien la vuelva a bajar.
/// </para>
///
/// <para>
/// <b>Qué cuenta cada patrón, y por qué.</b> Se cuenta con <see cref="Regex.Matches(string)"/>
/// (no overlapping) sobre el texto completo del fichero — <b>incluye comentarios y
/// cadenas</b>, deliberadamente: editar un <c>&lt;summary&gt;</c> que menciona el término
/// legacy es coste real y filtrar comentarios exigiría un parser que, al equivocarse,
/// fallaría hacia verde y en silencio (mismo razonamiento que
/// <see cref="UsosDeEsPlataformaCongeladosTests"/>). <c>EjecutivoUsuarioId</c> y
/// <c>Delegacion</c>/<c>ClienteActivo</c> son subcadena, no <c>\bpalabra\w*</c>: una
/// frontera de palabra por delante dejaría fuera tipos con prefijo como
/// <c>IDelegacionesQueryContext</c>, que es la misma deuda de ADR-004 (así lo destapó la
/// versión original de este test, cuando discrepó del grep con el que se preparó).
/// <c>Hydra</c> lleva un <i>lookbehind</i> negativo que excluye <c>Project-Hydra</c>: es
/// el nombre del repositorio y de su gemelo de negocio, y esa referencia de ruta no es
/// deuda de marca — la deuda es la que ve el usuario ("Detectado por Hydra").
/// </para>
///
/// <para>
/// <b>Límite conocido, no decidido aquí: sensibilidad a mayúsculas.</b> Los cuatro
/// patrones son <i>case-sensitive</i> — igual que el trinquete original y que la tabla
/// § 5 del contrato, para que las cifras sigan siendo comparables (§ 5.1: "mismo
/// alcance... para que las cifras sean comparables entre sí"). Consecuencia medida el
/// 2026-09-03: una variable local <c>delegacion</c> o un parámetro <c>clienteActivo</c>
/// en <i>camelCase</i> no cuentan, y esa forma de la deuda es mayoritaria — el recuento
/// case-insensitive del mismo árbol da 764 apariciones de <c>Delegacion</c> frente a las
/// 390 que congela este trinquete. Decidir si el § 5 del contrato debe cubrir también el
/// uso en minúsculas es una decisión de alcance de la deuda, no de instrumento —
/// corresponde al propietario del contrato, no a este incremento (REC-171).
/// </para>
///
/// <para>
/// <b>Qué NO vigila, y es deliberado.</b>
/// <list type="bullet">
/// <item><c>Workspace</c> queda fuera. ADR-011 § 2.2 lo declara <i>canónico</i> como
/// concepto de negocio ("Workspace operativo derivado"), y la regla del § 3.6 del
/// contrato es sobre <b>prosa</b> que pueda tocar UI, no sobre identificadores. Contar
/// <c>WorkspaceService</c> castigaría código correcto, que es la peor clase de trinquete:
/// el que enseña a ignorarlo.</item>
/// <item>Las <b>migraciones</b> quedan fuera. Son historia aplicada y no se editan, así
/// que su cuenta no puede bajar nunca — incluirla haría que el número lo dominara un
/// suelo inmutable en vez de la deuda viva.</item>
/// <item><c>Project-Hydra</c> queda fuera del contador de marca (ver el lookbehind
/// arriba): es el nombre del repositorio, y se llama así legítimamente.</item>
/// </list>
/// </para>
/// </summary>
public class TerminologiaCanonicaTests
{
    /// <summary>
    /// Deuda medida por ESTE test, no por un <c>grep</c> de fuera. Importa: <c>grep -c</c>
    /// cuenta líneas con coincidencia y <c>grep -o</c> cuenta coincidencias, así que dos
    /// mediciones "del mismo número" pueden diferir sin que ninguna esté mal. La línea
    /// base sale del instrumento que la vigila, o no serían comparables.
    /// </summary>
    private static readonly Dictionary<string, (string Patron, string ConceptoCanonico)> Deuda = new()
    {
        ["Hydra"] = (@"(?<!Project-)Hydra", "la marca es TALVEG (§ 5)"),

        ["EjecutivoUsuarioId"] = ("EjecutivoUsuarioId",
            "Asignación de Cartera — eje único de responsabilidad que ADR-011 sustituye (§ 5)"),

        ["Delegacion"] = ("Delegacion",
            "Asignación de Operación — vocabulario de ADR-004 que ADR-011 § 2.7 generaliza (§ 5)"),

        // NO está en el § 5 del contrato: lo encontró el primer barrido de auditoría
        // terminológica, y es justo lo que una regex no podía encontrar. El nombre dice
        // "Cliente" —plano 4, comercial— y el tipo expone TenantIdSeleccionado (plano 1),
        // AsignacionOperacionIdSeleccionada (plano 2) y SesionPrivilegiadaIdSeleccionada
        // (plano 3). Es una selección de CONTEXTO que cruza tres planos con el nombre de
        // un cuarto: fuga de modelo de la era ADR-004.
        //
        // Se congela, no se renombra: es una interfaz de producción con decenas de
        // consumidores, y parte del vocabulario es visible al usuario (SelectorClienteActivo),
        // donde "tenant" está prohibido — así que el sustituto no es obvio y el renombrado
        // es un incremento propio con su decisión de producto detrás.
        ["ClienteActivo"] = ("ClienteActivo",
            "selección de contexto (tenant + vía de acceso), no un 'Cliente' de ninguno de sus siete sentidos"),
    };

    /// <summary>
    /// <b>Medido el 2026-09-03 sobre <c>origin/main</c> <c>e6efa4fe</c></b> — no rescatado
    /// de la rama <c>claude/b1-3-cierre-sesion-soporte</c> ni de la tabla § 5 del contrato
    /// (ambas fechadas el 2026-08-31 y por tanto más viejas que este commit). El propio
    /// § 5.1 del contrato exige volver a medir antes de citar.
    ///
    /// <para>
    /// <b>Orden de reproducción</b> (recuento por reflexión del propio árbol, mismo
    /// alcance que <see cref="ArchivosDeCodigo"/>): ejecutar este test. Si el número real
    /// no coincide con el de abajo, <c>FluentAssertions</c> imprime en el mensaje de fallo
    /// tanto el valor esperado como el medido — no hace falta instrumentación externa para
    /// reobtener la cifra, el propio test es el instrumento reproducible.
    /// </para>
    /// </summary>
    private static readonly Dictionary<string, int> Congelado = new()
    {
        ["Hydra"] = 117,
        ["EjecutivoUsuarioId"] = 66,
        ["Delegacion"] = 390,
        ["ClienteActivo"] = 110,
    };

    [Theory]
    [InlineData("Hydra")]
    [InlineData("EjecutivoUsuarioId")]
    [InlineData("Delegacion")]
    [InlineData("ClienteActivo")]
    public void La_deuda_terminologica_no_crece(string termino)
    {
        var (patron, conceptoCanonico) = Deuda[termino];
        var regex = new Regex(patron, RegexOptions.Compiled);

        var apariciones = ArchivosDeCodigo().Sum(a => regex.Matches(File.ReadAllText(a)).Count);

        apariciones.Should().Be(Congelado[termino],
            $"'{termino}' es deuda terminológica: representa {conceptoCanonico}. El contrato prohíbe " +
            "renombrarlo automáticamente, así que la deuda se queda — pero no puede CRECER, y si baja " +
            "hay que actualizar el número aquí en el mismo commit. Si has añadido apariciones nuevas, " +
            "usa el término canónico; si las has retirado, baja la cifra de 'Congelado'");
    }

    /// <summary>
    /// Control positivo del propio trinquete. Sin él, los cuatro casos de arriba pasarían
    /// igual si <see cref="ArchivosDeCodigo"/> devolviera una lista vacía —por un cambio
    /// de rutas, por correr desde otro directorio base o por un filtro de más—, y un
    /// inventario que da cero por ceguera se lee igual que uno que da cero por estar
    /// limpio. Es la diferencia entre "no hay" y "no miré". Mismo patrón que
    /// <c>ModeloTenantTests</c> ("si esto está vacío, el propio test dejó de poder ver el
    /// modelo real"): no se copia el código, se copia la idea de exigir un mínimo que solo
    /// puede cumplirse mirando de verdad.
    ///
    /// <para>
    /// <b>Lo que este control NO cubre.</b> Protege contra perder de vista el árbol casi
    /// entero (ruta rota, filtro vacío), pero no contra perder un subárbol parcial que no
    /// contuviera ninguno de los cuatro términos: el recuento de ficheros seguiría por
    /// encima de mil y los cuatro contadores de deuda no se moverían, así que el trinquete
    /// completo pasaría en verde con una porción real del árbol fuera de vista. Si ese
    /// subárbol perdido sí contuviera alguno de los cuatro términos, la igualdad exacta de
    /// <see cref="La_deuda_terminologica_no_crece"/> sí lo delataría — el hueco es solo
    /// para la combinación "subárbol perdido" + "sin deuda dentro".
    /// </para>
    /// </summary>
    [Fact]
    public void El_trinquete_esta_mirando_codigo_de_verdad()
    {
        var archivos = ArchivosDeCodigo().ToList();

        archivos.Should().HaveCountGreaterThan(1000,
            "el repositorio tiene más de mil ficheros de código fuera de migraciones: muchos menos " +
            "significa que el filtro dejó fuera lo que debía vigilar");

        archivos.Should().Contain(a => a.EndsWith(".razor", StringComparison.OrdinalIgnoreCase),
            "los .razor entran igual que los .cs — un bloque @code es C#, y parte de la deuda de marca " +
            "vive precisamente en texto de interfaz");
    }

    /// <summary>
    /// <c>src/</c> completo menos migraciones, <c>obj/</c> y <c>bin/</c>. Las migraciones
    /// se excluyen aquí y no en el patrón para que la exclusión sea visible en un sitio y
    /// no repetida en cuatro expresiones regulares.
    /// </summary>
    private static IEnumerable<string> ArchivosDeCodigo()
    {
        var raiz = Path.Combine(RaizDelRepositorio(), "src");
        var separador = Path.DirectorySeparatorChar;

        return Directory
            .EnumerateFiles(raiz, "*", SearchOption.AllDirectories)
            .Where(a => a.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                        || a.EndsWith(".razor", StringComparison.OrdinalIgnoreCase))
            .Where(a => !a.Contains($"{separador}obj{separador}")
                        && !a.Contains($"{separador}bin{separador}")
                        && !a.Contains($"{separador}Migrations{separador}"));
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
