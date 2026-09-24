using System.Text.RegularExpressions;
using System.Xml.Linq;
using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

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
/// no se repite aquí. Regla de este fichero en adelante: <b>ninguna cifra viaja en la
/// prosa si no está también verificada por el mecanismo de abajo para el commit
/// actual</b>.
/// </para>
///
/// <para>
/// <b>Igualdad exacta, no un techo.</b> Mismo criterio que
/// <see cref="UsosDeEsPlataformaCongeladosTests"/>: si el número baja, este test se pone
/// rojo y obliga a actualizarlo en el mismo commit que lo bajó.
/// </para>
///
/// <para>
/// <b>Segunda versión del instrumento (REC-178, DEC-65, 2026-09-04).</b> La primera
/// versión contaba con <see cref="Regex.Matches(string)"/> sobre el texto completo del
/// fichero — comentarios, doc-comments y literales de cadena incluidos — y eso produjo
/// dos falsos positivos reales el mismo día: la PR #458 subía porque su propio
/// doc-comment citaba <c>DelegacionTenant</c> como precedente de diseño y nombraba
/// <c>Hydra</c> al enumerar tratamientos cubiertos; la PR #448 subía por una línea de
/// comentario dentro de <c>RevalidacionClienteActivoMiddleware.cs</c> — un fichero cuyo
/// propio nombre ya es la deuda. Las dos arreglaban o documentaban bien el problema y
/// las castigaba el instrumento por ello. El § 5 del contrato dice medir
/// <b>«identificador en código»</b>; un observador que no distingue código de comentario
/// no mide esa propiedad. DEC-65 decidió corregir el observador, no sus entradas: **no
/// se reescribe ningún comentario** para esquivar el patrón, y las cifras congeladas
/// **bajan** cuando el instrumento nuevo mide menos, nunca suben.
/// </para>
///
/// <para>
/// <b>Qué cuenta cada patrón, y por qué.</b> <c>EjecutivoUsuarioId</c> y
/// <c>Delegacion</c>/<c>ClienteActivo</c> son subcadena, no <c>\bpalabra\w*</c>: una
/// frontera de palabra por delante dejaría fuera tipos con prefijo como
/// <c>IDelegacionesQueryContext</c>, que es la misma deuda de ADR-004. <c>Hydra</c> lleva
/// un <i>lookbehind</i> negativo que excluye <c>Project-Hydra</c>: es el nombre del
/// repositorio y de su gemelo de negocio, y esa referencia de ruta no es deuda de marca.
/// Este reparto de qué cadena busca cada patrón no lo toca REC-178: lo que cambia es
/// <b>qué región del fichero se deja mirar</b>, no cómo se comparan los tokens dentro.
/// </para>
///
/// <para>
/// <b>Dos regímenes distintos, .cs y .razor — declarado, no por inercia (§ 10 de
/// HO-178-01).</b> Para <c>.cs</c>, <see cref="ContarIdentificadoresEnCodigoCSharp"/>
/// analiza el árbol sintáctico y solo cuenta apariciones dentro de un
/// <see cref="SyntaxKind.IdentifierToken"/> real: excluye comentarios/doc-comments
/// (control 2 de DEC-65) y literales de cadena o carácter (control 3: hoy el § 5 no
/// dice que deban contar). Para <c>.razor</c> se conserva el régimen anterior —contar
/// sobre el texto completo del fichero—, medido y decidido así a propósito, no dejado
/// caer: un analizador de C# no puede parsear un <c>.razor</c> tal cual, que mezcla
/// marcado HTML, directivas <c>@</c> y bloques <c>@code</c>, y extraer con precisión
/// solo los identificadores de C# embebidos en marcado (<c>@variable</c>, atributos
/// <c>@bind-Value</c>, etc.) exige un analizador de Razor que este incremento —una S,
/// un arreglo de instrumento— no tiene mandato para construir. La medición del
/// 2026-09-04 sobre <c>origin/main</c> <c>9b98599f</c> lo confirma para el total, que sí
/// es exacto (recontado con el propio patrón sobre solo los <c>.razor</c>): de las 117
/// apariciones de <c>Hydra</c>, 53 están en <c>.razor</c>. La mayoría de esas 53 —medido
/// con una heurística de balanceo de llaves para localizar bloques <c>@code</c>, no con
/// un analizador de Razor, así que sirve para el orden de magnitud y no como cifra
/// citable— caen fuera de cualquier bloque <c>@code</c>: es texto de interfaz visible al
/// usuario, el propio caso que la versión anterior de este fichero señalaba como "parte
/// de la deuda de marca vive en texto de interfaz". Si en vez del régimen viejo se
/// extrajeran solo identificadores de C# de esos ficheros (dentro de <c>@code</c> y de
/// expresiones <c>@algo</c> embebidas), la cifra de 117 se hundiría a un dígito, un
/// recorte de alcance que el § 5 no pidió y que esta PR no decide. Esa pregunta —si el
/// trinquete debe seguir viendo la prosa de
/// marca en <c>.razor</c> o solo sus identificadores de C#— queda elevada a la Oficina
/// de Reconciliación con la medición hecha, no resuelta aquí. Consecuencia declarada
/// del régimen viejo en <c>.razor</c>: un comentario <c>@* ... *@</c> o una cadena
/// dentro de un <c>.razor</c> todavía puede mover la cifra (5 casos de
/// <c>Delegacion</c>, 7 de <c>Hydra</c>, 2 de <c>ClienteActivo</c>, 0 de
/// <c>EjecutivoUsuarioId</c>, medido el 2026-09-04) — un residuo del mismo defecto que
/// esta versión corrige para <c>.cs</c>, y no para <c>.razor</c>, por la razón de
/// alcance de arriba.
/// </para>
///
/// <para>
/// <b>Tercer régimen, .resx neutral (2026-09-23).</b> La localización es-ES/ca-ES mueve
/// el texto visible de los <c>.razor</c> a <c>Recursos/TextosX.resx</c>. Sin leer esos
/// ficheros, la cifra bajaba sin que la deuda desapareciera: la cadena solo cambiaba de
/// fichero (caso medido en la PR #803, «Pregúntale a Hydra» a
/// <c>TextosAsistenteIa.resx</c>). <see cref="ContarEnValoresDeRecursos"/> cuenta, con el
/// mismo patrón de cada término, el texto de cada <c>&lt;data&gt;&lt;value&gt;</c>, que es
/// lo que la interfaz muestra; no cuenta el atributo <c>name</c> de la clave (un
/// identificador que no llega a la interfaz ni es C#), ni <c>&lt;comment&gt;</c>, ni la
/// cabecera del esquema. <b>Solo el neutral</b> (<c>TextosX.resx</c>): el satélite
/// <c>TextosX.ca-ES.resx</c> nace como copia del neutral y contarlo duplicaría cada
/// aparición; una traducción que conserve la marca se corrige con el neutral, no aparte.
/// Un fichero solo es satélite si lleva segmento de cultura <b>y</b> tiene su neutral al
/// lado (<see cref="EsResxNeutral(string)"/>): <c>Textos.Ui.resx</c> sin <c>Textos.resx</c> cuenta.
/// Consecuencia declarada: un término que aparezca solo en el satélite no se ve.
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
/// <item>El <i>casing</i> sigue sin decidir (§ 9 de HO-178-01): los cuatro patrones
/// siguen siendo <i>case-sensitive</i>, igual que antes de REC-178. Es una pregunta
/// distinta —cómo se comparan los tokens, no qué región se mira— y no es la de este
/// incremento.</item>
/// <item>Un identificador dentro de un bloque desactivado por el preprocesador
/// (<c>#if false</c> y similares) no cuenta: <see cref="CSharpSyntaxTree.ParseText"/>
/// marca ese código como trivia deshabilitada, no como <see cref="SyntaxKind.IdentifierToken"/>,
/// y es coherente con el resto del trinquete — código que no se ejecuta ni se compila no
/// es la deuda viva que esto vigila (mismo razonamiento que excluir migraciones).
/// Verificado por mutación durante la revisión adversarial de esta PR: no hay ningún
/// caso real de esto en el árbol hoy.</item>
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
    /// <b>Recalculadas por REC-178/DEC-65 el 2026-09-04, sobre <c>origin/main</c>
    /// <c>9b98599f</c>, con el instrumento nuevo</b> — no son las mismas cifras que
    /// congeló REC-171 (117/66/390/110): esas medían texto completo, estas miden solo
    /// identificadores de C# en <c>.cs</c> más el régimen viejo, sin cambios, en
    /// <c>.razor</c> (ver el doc-comment de la clase). Todas BAJAN respecto a las
    /// anteriores, nunca suben, tal y como exige DEC-65.
    ///
    /// <para>
    /// <b>La bajada, separada por causa y verificada contra la medición — DEC-65,
    /// riesgo 1.</b> Para <c>.cs</c> hay dos causas distintas y se midieron por
    /// separado para no confundirlas: apariciones dentro de un comentario o doc-comment
    /// (control 2), y apariciones dentro de un literal de cadena o carácter (control 3).
    /// La resta (cifra vieja − cifra nueva) coincide exactamente con
    /// <c>comentario + literal</c> medidos en <c>.cs</c> para los cuatro términos —si no
    /// coincidiera, habría una tercera causa sin identificar y esta tabla no se habría
    /// escrito—:
    /// <list type="bullet">
    /// <item><c>Hydra</c>: 117 − 55 = 62; en <c>.cs</c>, comentario=59 + literal=3 = 62.</item>
    /// <item><c>EjecutivoUsuarioId</c>: 66 − 48 = 18; en <c>.cs</c>, comentario=18 + literal=0 = 18.</item>
    /// <item><c>Delegacion</c>: 390 − 291 = 99; en <c>.cs</c>, comentario=71 + literal=28 = 99.</item>
    /// <item><c>ClienteActivo</c>: 110 − 68 = 42; en <c>.cs</c>, comentario=41 + literal=1 = 42.</item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// <b>Orden de reproducción</b>: ejecutar este test. Si el número real no coincide
    /// con el de abajo, <c>FluentAssertions</c> imprime en el mensaje de fallo tanto el
    /// valor esperado como el medido.
    /// </para>
    ///
    /// <para>
    /// <b>REC-208 (2026-09-05) baja <c>Delegacion</c> de 291 a 290.</b>
    /// <c>RegistroActividadSoporteConfiguration</c> perdía su única línea
    /// <c>builder.Property(r =&gt; r.DelegacionTenantId).IsRequired()</c> al pasar
    /// esa columna a anulable (el agregado admite ahora dos agrupadores
    /// posibles — ver <c>RegistroActividadSoporte</c>): −1. El incremento
    /// necesitaba referenciar la propiedad ya existente <c>DelegacionTenantId</c>
    /// varias veces más (la nueva guarda XOR del constructor privado, la nueva
    /// constraint <c>CK_RegistrosActividadSoporte_UnSoloAgrupador</c>, el nuevo
    /// índice), pero esas referencias se escribieron sin introducir ningún
    /// identificador nuevo con el término —la variable local que sí lo hacía se
    /// llamó <c>tieneViaHeredada</c>, no <c>tieneDelegacion</c>, y la factoría
    /// nueva se llamó <c>PorViaHeredada</c>, no <c>PorDelegacion</c>, siguiendo
    /// la instrucción de este mismo mensaje de fallo ("si has añadido un
    /// identificador nuevo... usa el canónico"); no se usó el canónico literal
    /// <c>AsignacionOperacion</c> porque esa clase generaliza expresamente solo
    /// <c>DelegacionTenant</c> de propósito <b>Comercial</b>
    /// (<c>AsignacionOperacion.cs</c>), y este incremento toca la vía de
    /// propósito <b>Soporte</b>, un caso que ADR-011 § 2.7 no cubre— así que el
    /// neto es <b>−1</b>, no una tercera causa sin identificar.
    /// </para>
    ///
    /// <para>
    /// <b>Subcontrata 360 Gen 2 (2026-09-11) baja <c>Hydra</c> de 55 a 54.</b>
    /// <c>SubcontrataWorkspacePanel.razor</c> decía «Gestionada: su documentación se
    /// sube y valida dentro de Hydra.»: una cadena, pero en <c>.razor</c>, donde
    /// sigue el régimen viejo y sí contaba. Era su única aparición del término; el
    /// texto pasa a «se sube y se valida aquí». Neto <b>−1</b>.
    /// </para>
    ///
    /// <para>
    /// <b><c>Delegacion</c> 312 → 317 (alta de Tenant propietario bajo un Operador CAE externo, 2026-09-20; +5 sobre la siembra multi-Tenant de la demo, #761; medido tras rebasar sobre <c>f537e71f</c>).</b>
    /// Cinco identificadores reales, todos tipos o miembros ya existentes que el incremento
    /// necesita nombrar y que no tienen alternativa canónica sin renombrarlos (renombrado que este
    /// contrato prohíbe hacer de paso): <c>IDelegacionTenantRepository</c> y <c>DelegacionTenant</c>
    /// en <c>CrearTenantPropietarioDeOperadorCaeExternoCommand</c>; <c>DelegacionesTenant</c> y
    /// <c>PropositoDelegacion</c> en <c>ObtenerOperadoresCaeExternosQuery</c>; y el segmento
    /// <c>Delegaciones</c> del espacio de nombres de <c>OperadoresCaeExternosPanel</c>, que vive junto a la
    /// página que lo monta. Las variables locales se llamaron <c>vinculo</c>, no <c>delegacion</c>.
    /// </para>
    ///
    /// <para>
    /// <b><c>Hydra</c> 54 → 53 (Estado comercial Gen 2, 2026-09-11).</b> La única
    /// aparición retirada es texto de marcado de <c>EstadoComercial.razor</c>
    /// —la entradilla antigua decía «se crea en Stripe fuera de Hydra»—, que
    /// cuenta porque en <c>.razor</c> sigue el régimen crudo descrito arriba.
    /// Independiente de la retirada de Subcontrata 360 (otro fichero): las dos
    /// suman. Medido con este mismo test sobre el árbol ya rebasado sobre la
    /// entrada de Subcontrata 360: 53.
    /// </para>
    ///
    /// <para>
    /// <b><c>Delegacion</c> 289 → 303 (matriz de estados de la demo a dirección,
    /// 2026-09-20): +14, todo referencia a superficie legada que la siembra tiene que
    /// invocar, ninguno un nombre propio nuevo.</b> <c>EscenariosDireccionDemo*.cs</c>
    /// reutiliza <c>DelegacionDemoSeeder</c> (10 referencias: sus constantes de nombre y sus
    /// métodos de aprovisionamiento), <c>CrearDelegacionAsync</c> (1) y toca <c>DelegacionTenant</c>,
    /// <c>DelegacionesTenant</c> y <c>DelegacionTenantId</c> (3) para reactivar la delegación
    /// revocada y asignar al equipo: el mecanismo legacy de ADR-011 (<c>AsignacionOperadorDelegado</c>)
    /// sigue siendo el único que sabe abrir una operación externa para
    /// <c>ReasignarCarteraClienteAsync</c>. Medido: el método y las variables propias de la
    /// siembra se nombraron sin el término (<c>AbrirOperacionExternaDeLaRamaAsync</c>) para que el
    /// aumento sea el mínimo inevitable. La cifra baja con la retirada de <c>DelegacionDemoSeeder</c>
    /// y con la migración de <c>DelegacionTenant</c> (D-2/D-3/D-7), no antes.
    /// </para>
    ///
    /// <para>
    /// <b><c>Delegacion</c> 303 → 312 (siembra administrativa de la demo a dirección,
    /// 2026-09-20): +9, todo referencia a <c>DelegacionDemoSeeder</c>, ningún nombre propio
    /// nuevo.</b> <c>SiembraDemoDireccionAdministrativa.cs</c> reutiliza sus cuatro constantes de
    /// nombre de Tenant y sus tres métodos (<c>AprovisionarTenantAsync</c>,
    /// <c>CrearUsuarioConsultoraAsync</c>, <c>CrearDelegacionAsync</c>) porque ese es el único
    /// código que sabe abrir la operación externa de un Operador CAE sobre un Tenant propietario;
    /// duplicarlo crearía un segundo camino con el mismo mecanismo legacy. Baja con la retirada de
    /// <c>DelegacionDemoSeeder</c>, no antes.
    /// </para>
    ///
    /// <para>
    /// <b>Régimen .resx neutral (2026-09-23): ninguna cifra cambia.</b> Medido sobre
    /// <c>origin/main</c> <c>1f46badf</c>: ningún <c>.resx</c> neutral de <c>src/</c> contiene
    /// todavía ninguno de los cuatro términos, así que añadirlos al recorrido suma 0. Desde
    /// aquí, mover un texto de un <c>.razor</c> a su <c>.resx</c> deja la cifra igual.
    /// </para>
    ///
    /// <para>
    /// <b><c>Delegacion</c> 320 → 314 (menú lateral como catálogo, 2026-09-23): −6, ningún
    /// identificador retirado.</b> <c>NavMenu.razor</c> tenía 7 apariciones de «Delegaciones», todas
    /// texto (el rótulo del enlace y comentarios <c>@* *@</c>), que en <c>.razor</c> cuentan por el
    /// régimen crudo. El menú de los usuarios internos pasa a <c>CatalogoMenuLateral.cs</c>, donde
    /// el rótulo es un literal y la justificación un comentario: en <c>.cs</c> ninguno de los dos
    /// cuenta. En <c>NavMenu.razor</c> queda 1 (el comentario de <c>@code</c> que cita
    /// <c>Delegaciones.razor</c>): 7 − 1 = −6. La deuda real de identificadores no cambia.
    /// </para>
    ///
    /// <para>
    /// <b><c>Delegacion</c> 314 → 320 (solicitud de incorporación a cartera, 2026-09-22): +6,
    /// todos en <c>CatalogoIncorporacionCartera.cs</c> y todos nombres ya existentes.</b>
    /// <c>DelegacionesTenant</c> (3), <c>PropositoDelegacion</c> (2) y
    /// <c>DelegacionTenantId</c> (1): el Gestor CAE solo es candidato sobre un Tenant
    /// propietario con la operación externa viva, y al aceptar se escribe la fila
    /// heredada <c>AsignacionOperadorDelegado</c>, que es lo que todavía leen
    /// <c>ObtenerClientesAutorizadosQuery</c> y el rol en ámbito explícito. Las variables
    /// locales se llamaron <c>vinculo</c>. Baja con la migración de <c>DelegacionTenant</c>.
    /// </para>
    ///
    /// <para>
    /// <b><c>Delegacion</c> 320 → 329 (incremento 1b del aprovisionamiento — el Administrador
    /// del Tenant propietario autoriza a un Operador CAE externo, 2026-09-23; recontado tras
    /// fusionar <c>origin/main</c> con el menú lateral y la solicitud de incorporación a
    /// cartera de arriba): +9, todo referencia a superficie existente, ningún nombre propio
    /// nuevo.</b> La decisión fijada (opción 1′) es
    /// reutilizar <c>CrearDelegacionTenantCommand</c> sin entidad nueva, así que la pantalla lo
    /// nombra (espacio de nombres y tipo, 2); el comando consulta <c>DelegacionesTenant</c> y
    /// <c>PropositoDelegacion</c> para rechazar un segundo Operador vigente (2); la consulta del
    /// candidato reutiliza <c>IAutorizacionDelegacionTenant</c> y
    /// <c>PuedeGestionarDelegacionesAsync</c> para no duplicar la regla de autoridad (2); y el
    /// recurso de textos nuevo vive en la carpeta de la funcionalidad,
    /// <c>Features.Delegaciones.Recursos</c> (declaración y dos <c>using</c>, 3). Los tipos
    /// nuevos se nombraron sin el término (<c>OperadorCaeExternoElegible</c>,
    /// <c>AutorizarOperadorCaeExternoQueriesHandler</c>). Baja con la migración de
    /// <c>DelegacionTenant</c> (D-2/D-3/D-7), no antes.
    /// </para>
    ///
    /// <para>
    /// <b><c>ClienteActivo</c> 71 → 73 (petición abortada en
    /// <c>RevalidacionClienteActivoMiddleware</c>, 2026-09-23): +2, mismo identificador ya
    /// congelado, ningún tipo nuevo.</b> La revisión puente del incremento (#822) exigió
    /// ejecutar también en el segundo catch la invalidación de
    /// <c>ClienteActivoSeleccionado</c> que ya existía en el camino feliz, para no diferirla a
    /// la siguiente petición cuando el cliente aborta durante <c>EsVentanaDeSoporteAsync</c>:
    /// dos apariciones más del mismo patrón, no deuda de un tipo distinto.
    /// </para>
    ///
    /// <para>
    /// <b><c>Delegacion</c> 329 → 332 (hallazgo de Codex al integrar <c>origin/main</c> en el
    /// incremento 1b, 2026-09-24): +3, todo superficie existente.</b>
    /// <c>ReactivarDelegacionTenantCommand</c> aplica la misma regla
    /// <c>OtroOperadorVigente</c> que <c>CrearDelegacionTenantCommand</c> antes de reabrir la
    /// operación completa: consulta <c>DelegacionesTenant</c> (1) y compara
    /// <c>PropositoDelegacion</c> del vínculo reactivado y de los demás (2). Sin la regla,
    /// reactivar al Operador CAE externo A tras autorizar a B chocaba con el índice único como
    /// excepción sin traducir. Baja con la migración de <c>DelegacionTenant</c>.
    /// </para>
    /// </summary>
    private static readonly Dictionary<string, int> Congelado = new()
    {
        ["Hydra"] = 48,
        ["EjecutivoUsuarioId"] = 48,
        ["Delegacion"] = 332,
        ["ClienteActivo"] = 73,
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

        var porFichero = ArchivosDeCodigo()
            .Select(archivo => (archivo, apariciones: ContarEnArchivo(archivo, regex)))
            .Where(par => par.apariciones > 0)
            .OrderByDescending(par => par.apariciones)
            .ToList();

        var apariciones = porFichero.Sum(par => par.apariciones);

        var listado = string.Join("\n", porFichero.Select(par =>
            $"  {par.apariciones,4}  {Path.GetRelativePath(RaizDelRepositorio(), par.archivo)}"));

        apariciones.Should().Be(Congelado[termino],
            $"'{termino}' es deuda terminológica: representa {conceptoCanonico}. El contrato prohíbe " +
            "renombrarlo automáticamente, así que la deuda se queda — pero no puede CRECER, y si baja " +
            "hay que actualizar el número aquí en el mismo commit. Si has añadido un identificador nuevo " +
            "en código con este término, usa el canónico; si has retirado alguno, baja la cifra de " +
            "'Congelado'. RÉGIMEN, DISTINTO POR TIPO DE FICHERO (DEC-65): en .cs, un comentario, " +
            "doc-comment o literal de cadena/carácter con esta cadena NO cuenta — solo un identificador " +
            "real del árbol sintáctico cuenta; en .razor SÍ cuenta cualquier aparición en el texto " +
            "completo del fichero (un comentario @* ... *@, una cadena o marcado visible incluidos), " +
            "porque un analizador de C# no puede parsear un .razor tal cual y este trinquete no tiene " +
            "mandato para construir uno de Razor; en el .resx neutral (TextosX.resx, no TextosX.ca-ES.resx) " +
            "cuenta el texto de cada <data><value>, así que mover un texto de un .razor a su .resx no " +
            "baja la cifra (regímenes declarados en el doc-comment de la clase). " +
            $"Ficheros con aparición que cuenta hoy, más frecuente primero:\n{listado}");
    }

    /// <summary>
    /// Cuenta las apariciones de <paramref name="regex"/> que de verdad cuentan para el
    /// § 5 del contrato, según el tipo de fichero (ver el doc-comment de la clase para el
    /// porqué de los dos regímenes).
    /// </summary>
    private static int ContarEnArchivo(string archivo, Regex regex)
    {
        var texto = File.ReadAllText(archivo);

        if (archivo.EndsWith(".razor", StringComparison.OrdinalIgnoreCase))
            return regex.Matches(texto).Count;

        if (archivo.EndsWith(".resx", StringComparison.OrdinalIgnoreCase))
            return ContarEnValoresDeRecursos(texto, regex);

        return ContarIdentificadoresEnCodigoCSharp(texto, regex);
    }

    /// <summary>
    /// Cuenta las apariciones de <paramref name="regex"/> dentro del texto de cada
    /// <c>&lt;data&gt;&lt;value&gt;</c> de un <c>.resx</c>: el texto que la interfaz
    /// muestra. No cuenta el atributo <c>name</c> de la clave, ni <c>&lt;comment&gt;</c>,
    /// ni la cabecera del esquema — ver el doc-comment de la clase.
    /// </summary>
    private static int ContarEnValoresDeRecursos(string textoResx, Regex regex) =>
        XDocument.Parse(textoResx)
            .Root!
            .Elements("data")
            .Elements("value")
            .Sum(valor => regex.Matches(valor.Value).Count);

    /// <summary>
    /// Un <c>.resx</c> satélite lleva la cultura como segunda extensión
    /// (<c>TextosX.ca-ES.resx</c>) <b>y</b> tiene al lado su neutral (<c>TextosX.resx</c>).
    /// Exigir el hermano evita tomar por satélite un neutral cuyo nombre tenga un segmento
    /// corto que no es cultura (<c>Textos.Ui.resx</c>): sin neutral al lado, se cuenta.
    /// </summary>
    private static readonly Regex ResxSatelite =
        new(@"^(?<base>.+)\.[a-z]{2,3}(-[A-Za-z0-9]{2,8})*\.resx$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static bool EsResxNeutral(string archivo) => EsResxNeutral(archivo, File.Exists);

    private static bool EsResxNeutral(string archivo, Func<string, bool> existe)
    {
        if (!archivo.EndsWith(".resx", StringComparison.OrdinalIgnoreCase))
            return false;

        var satelite = ResxSatelite.Match(archivo);
        return !(satelite.Success && existe(satelite.Groups["base"].Value + ".resx"));
    }

    /// <summary>
    /// Cuenta solo las apariciones de <paramref name="regex"/> que caen dentro de un
    /// <see cref="SyntaxKind.IdentifierToken"/> real del árbol sintáctico de
    /// <paramref name="textoFuente"/> — ni en trivia (comentarios, doc-comments,
    /// espacios), ni en un literal de cadena o carácter, ni en cualquier otro token.
    /// Instrumentación sintáctica, no otra regex (preferencia explícita de DEC-65).
    ///
    /// <para>
    /// <b>Por qué <c>findInsideTrivia: false</c> en las dos búsquedas, y no <c>true</c>
    /// — el detalle que se midió mal en el primer intento.</b>
    /// <see cref="SyntaxNode.FindTrivia"/> con <c>findInsideTrivia: true</c> no
    /// devuelve la trivia contenedora cuando la posición cae sobre un TOKEN real dentro
    /// de una trivia ESTRUCTURADA (un doc-comment <c>///</c> es trivia estructurada: su
    /// texto son <see cref="SyntaxKind.XmlTextLiteralToken"/> de verdad, no más trivia
    /// anidada) — en ese caso el método busca otra trivia DENTRO de la estructura, no la
    /// encuentra porque ahí lo que hay es un token, y devuelve <c>default</c>. El
    /// resultado, medido: la búsqueda cae al siguiente paso
    /// (<see cref="SyntaxNode.FindToken(int, bool)"/> con el mismo <c>true</c>), que sí
    /// desciende a la estructura y devuelve el <c>XmlTextLiteralToken</c> real — y ese
    /// token no es <c>IdentifierToken</c> ni un literal de cadena/carácter, así que con
    /// una implementación descuidada cae en una categoría "otro" que ni cuenta ni se
    /// excluye a propósito: exactamente el "identificador legítimo que deja de verse"
    /// que el § 16 de HO-178-01 pide vigilar, solo que al revés — aquí lo que casi se
    /// perdía era la exclusión del comentario, no un identificador. Con
    /// <c>findInsideTrivia: false</c> en ambas llamadas, <see cref="SyntaxNode.FindTrivia"/>
    /// devuelve directamente la trivia contenedora (el bloque de doc-comment entero,
    /// diga lo que diga por dentro) sin intentar bajar a su estructura, que es
    /// exactamente la granularidad que hace falta: "¿esta posición está dentro de ALGÚN
    /// comentario?", no "¿dentro de qué parte concreta de su XML interno?". Medido sobre
    /// el árbol real: con <c>true</c> este método reportaba <c>Delegacion</c>=280
    /// identificadores y "otro"=32 sin clasificar; con <c>false</c>, 268 identificadores
    /// y cero sin clasificar — los 12 que cambiaron de bando eran, los 12, coincidencias
    /// dentro de un <c>&lt;c&gt;DelegacionTenant&lt;/c&gt;</c> de un doc-comment.
    /// </para>
    /// </summary>
    private static int ContarIdentificadoresEnCodigoCSharp(string textoFuente, Regex regex)
    {
        var root = CSharpSyntaxTree.ParseText(textoFuente).GetRoot();
        var total = 0;

        foreach (Match coincidencia in regex.Matches(textoFuente))
        {
            var posicion = coincidencia.Index;

            var trivia = root.FindTrivia(posicion, findInsideTrivia: false);
            if (trivia.Span.Contains(posicion) && EsComentario(trivia.Kind()))
                continue;

            var token = root.FindToken(posicion, findInsideTrivia: false);
            if (token.IsKind(SyntaxKind.IdentifierToken))
                total++;

            // Cualquier otro tipo de token (literal de cadena, literal de carácter,
            // palabra clave, puntuación...) no cuenta: solo el identificador real
            // cuenta (control 1 de DEC-65), y hoy el § 5 no pide contar literales de
            // texto aparte (control 3).
        }

        return total;
    }

    private static bool EsComentario(SyntaxKind kind) => kind
        is SyntaxKind.SingleLineCommentTrivia
        or SyntaxKind.MultiLineCommentTrivia
        or SyntaxKind.SingleLineDocumentationCommentTrivia
        or SyntaxKind.MultiLineDocumentationCommentTrivia
        or SyntaxKind.DocumentationCommentExteriorTrivia;

    /// <summary>
    /// Control 1 y control 2 de DEC-65, emparejados en la misma prueba: el identificador
    /// real en código cuenta; la misma cadena dentro de un comentario de línea no cuenta.
    /// </summary>
    [Fact]
    public void Un_identificador_real_cuenta_y_la_misma_cadena_en_un_comentario_de_linea_no()
    {
        var regex = new Regex("Delegacion", RegexOptions.Compiled);

        const string conIdentificador = "namespace N; public class Delegacion { }";
        ContarIdentificadoresEnCodigoCSharp(conIdentificador, regex).Should().Be(1,
            "control 1 (DEC-65): un identificador real en código sí debe contar");

        const string soloEnComentario = "namespace N;\n// Esto menciona Delegacion de pasada\npublic class Otro { }";
        ContarIdentificadoresEnCodigoCSharp(soloEnComentario, regex).Should().Be(0,
            "control 2 (DEC-65): la misma cadena dentro de un comentario de línea no debe contar");
    }

    /// <summary>
    /// Control 2 de DEC-65 específicamente contra un doc-comment XML — el caso real que
    /// puso en rojo la PR #458 con el instrumento anterior (su <c>&lt;summary&gt;</c>
    /// citaba <c>DelegacionTenant</c> como precedente de diseño).
    /// </summary>
    [Fact]
    public void Un_identificador_real_cuenta_y_la_misma_cadena_en_un_doc_comment_no()
    {
        var regex = new Regex("Delegacion", RegexOptions.Compiled);

        const string conIdentificador = "namespace N; public class Delegacion { }";
        ContarIdentificadoresEnCodigoCSharp(conIdentificador, regex).Should().Be(1,
            "control 1 (DEC-65): un identificador real en código sí debe contar");

        const string soloEnDocComment = """
            namespace N;
            /// <summary>
            /// Sigue el precedente de <c>DelegacionTenant</c> para este diseño.
            /// </summary>
            public class Otro { }
            """;
        ContarIdentificadoresEnCodigoCSharp(soloEnDocComment, regex).Should().Be(0,
            "control 2 (DEC-65): la misma cadena dentro de un doc-comment no debe contar — " +
            "es el caso real que puso en rojo la PR #458 con el instrumento anterior");
    }

    /// <summary>
    /// Control 3 de DEC-65: si la implementación captura también literales de cadena o
    /// carácter, no deben contar salvo que el § 5 del contrato lo diga expresamente — y
    /// hoy no lo dice (comprobado leyendo el § 5 de <c>CONTRATO_TERMINOLOGIA.md</c>, no
    /// asumido).
    /// </summary>
    [Fact]
    public void Un_identificador_real_cuenta_y_el_mismo_texto_en_un_literal_de_cadena_no()
    {
        var regex = new Regex("ClienteActivo", RegexOptions.Compiled);

        const string conIdentificador = "namespace N; public class ClienteActivo { }";
        ContarIdentificadoresEnCodigoCSharp(conIdentificador, regex).Should().Be(1,
            "control 1 (DEC-65): un identificador real en código sí debe contar");

        const string enLiteralDeCadena = """namespace N; class Otro { string M() => "ClienteActivo"; }""";
        ContarIdentificadoresEnCodigoCSharp(enLiteralDeCadena, regex).Should().Be(0,
            "control 3 (DEC-65): un literal de cadena con la misma cadena no debe contar — " +
            "el § 5 del contrato no pide contar literales de texto aparte de identificadores");
    }

    /// <summary>
    /// Ciclo de mutación de instrumento (no de deuda): comprueba que
    /// <see cref="ContarIdentificadoresEnCodigoCSharp"/> sigue vivo y no se ha quedado
    /// devolviendo cero por ceguera. Un identificador legítimo y sin relación con la
    /// deuda vigilada debe seguir contando: si esto fallara, el trinquete completo
    /// dejaría de ver identificadores de verdad y los cuatro casos de
    /// <see cref="La_deuda_terminologica_no_crece"/> pasarían en falso por no encontrar
    /// nunca nada, deuda incluida.
    /// </summary>
    [Fact]
    public void El_analizador_de_identificadores_no_esta_ciego()
    {
        var regex = new Regex("Ejemplo", RegexOptions.Compiled);
        const string codigo = "namespace N; public class Ejemplo { public int EjemploId; }";

        ContarIdentificadoresEnCodigoCSharp(codigo, regex).Should().Be(2,
            "control positivo del propio analizador: dos identificadores reales, ninguno en comentario " +
            "ni en literal, deben contar los dos — si esto da 0, el analizador dejó de ver el árbol");
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
    /// <b>Conservado sin cambios por REC-178</b>: sigue vigilando <see cref="ArchivosDeCodigo"/>,
    /// que no cambió — lo que cambió es <see cref="ContarEnArchivo"/>, cómo se cuenta
    /// dentro de cada fichero, no qué ficheros se recorren.
    /// </para>
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

        archivos.Should().Contain(a => a.EndsWith(".resx", StringComparison.OrdinalIgnoreCase),
            "los .resx neutrales entran: la localización mueve a ellos el texto de interfaz de los .razor");

        archivos.Should().NotContain(a => a.EndsWith(".ca-ES.resx", StringComparison.OrdinalIgnoreCase),
            "el satélite ca-ES nace como copia del neutral: contarlo duplicaría cada aparición");

        // Todo .resx de src/ que se queda fuera tiene que ser un satélite cuyo neutral sí
        // está dentro: si no, se está perdiendo texto de interfaz sin duplicado que lo cubra.
        var dentro = archivos.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var fuera = Directory
            .EnumerateFiles(Path.Combine(RaizDelRepositorio(), "src"), "*.resx", SearchOption.AllDirectories)
            .Where(a => !dentro.Contains(a))
            .ToList();

        fuera.Should().NotBeEmpty("hoy hay satélites ca-ES en src/; si no sale ninguno, el control no mira");
        fuera.Should().OnlyContain(a => dentro.Contains(ResxSatelite.Match(a).Groups["base"].Value + ".resx"),
            "un .resx excluido solo puede ser un satélite con su neutral contado");
    }

    /// <summary>
    /// El texto de <c>&lt;value&gt;</c> cuenta; la clave (<c>name</c>) y el
    /// <c>&lt;comment&gt;</c> con la misma cadena no.
    /// </summary>
    [Fact]
    public void En_un_resx_cuenta_el_valor_y_no_la_clave_ni_el_comentario()
    {
        var regex = new Regex(@"(?<!Project-)Hydra", RegexOptions.Compiled);

        const string resx = """
            <?xml version="1.0" encoding="utf-8"?>
            <root>
              <data name="PreguntaleAHydra" xml:space="preserve">
                <value>Pregúntale a Hydra</value>
                <comment>Hydra era la marca antigua</comment>
              </data>
              <data name="Otro" xml:space="preserve">
                <value>Sin la marca</value>
              </data>
            </root>
            """;

        ContarEnValoresDeRecursos(resx, regex).Should().Be(1,
            "solo el texto visible de <value> cuenta; la clave y el comentario no llegan a la interfaz");
    }

    [Theory]
    [InlineData(@"src/Web/Recursos/TextosComunes.resx", true)]
    [InlineData(@"src/Web/Recursos/TextosComunes.ca-ES.resx", false)]
    [InlineData(@"src/Web/Recursos/TextosComunes.es-ES.resx", false)]
    [InlineData(@"src/Web/Recursos/TextosComunes.en.resx", false)]
    [InlineData(@"src/Web/Recursos/Textos.Ui.resx", true)]
    [InlineData(@"src/Web/Recursos/TextosComunes.cs", false)]
    public void Solo_el_resx_neutral_entra_en_el_recuento(string archivo, bool entra)
    {
        // Árbol simulado: TextosComunes.resx existe; Textos.resx no (Textos.Ui.resx no es satélite).
        var existentes = new HashSet<string> { @"src/Web/Recursos/TextosComunes.resx" };
        EsResxNeutral(archivo, existentes.Contains).Should().Be(entra);
    }

    /// <summary>
    /// <c>src/</c> completo menos migraciones, <c>obj/</c> y <c>bin/</c>. Las migraciones
    /// se excluyen aquí y no en el patrón para que la exclusión sea visible en un sitio y
    /// no repetida en cuatro expresiones regulares. Entran <c>.cs</c>, <c>.razor</c> y
    /// <c>.resx</c> neutrales (ver <see cref="EsResxNeutral"/>).
    /// </summary>
    private static IEnumerable<string> ArchivosDeCodigo()
    {
        var raiz = Path.Combine(RaizDelRepositorio(), "src");
        var separador = Path.DirectorySeparatorChar;

        return Directory
            .EnumerateFiles(raiz, "*", SearchOption.AllDirectories)
            .Where(a => a.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                        || a.EndsWith(".razor", StringComparison.OrdinalIgnoreCase)
                        || EsResxNeutral(a))
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
