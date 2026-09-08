using System.Text.RegularExpressions;
using FluentAssertions;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// Toda pantalla de lista con filtros tiene que distinguir <b>«aún no hay
/// nada»</b> de <b>«nada coincide con el filtro»</b>. Son situaciones opuestas
/// y llevan a acciones opuestas: ofrecer «crea el primero» a quien acaba de
/// filtrar lo manda a duplicar un registro que ya existe — en Trabajadores,
/// con el DNI repetido que eso arrastra.
///
/// <para>
/// El defecto estaba en NUEVE pantallas a la vez y ninguna lo notaba, porque
/// un estado vacío equivocado no rompe nada: se ve bien, es una frase
/// razonable, y solo miente. Por eso hace falta un trinquete y no basta con
/// haberlo arreglado una vez.
/// </para>
///
/// <para>
/// <b>Contrato efectivo, más estrecho que el nombre.</b> Esto comprueba
/// ESTRUCTURA sobre el fuente: que una página con barra de filtros declare
/// <c>HayFiltrosActivos</c> y lo use para separar dos estados vacíos
/// distintos. NO comprueba que el estado se pinte bien, ni que su texto sea
/// cierto, ni que el botón de limpiar funcione.
/// </para>
///
/// <para>
/// <b>Dónde está la prueba por render, y dónde no.</b> Existe para una pantalla
/// de cada forma de lista, las dos ya en <c>main</c>:
/// <c>EmpresasVacioPorFiltroTests</c> (acordeón) y
/// <c>TrabajadoresVacioPorFiltroTests</c> (QuickGrid). Clientes, Centros,
/// Subcontratas y Documentos son la MISMA construcción y aquí solo se verifican
/// estructuralmente — es un hueco declarado, no una propiedad demostrada. Un
/// trinquete de texto da la alarma, no la garantía.
/// </para>
/// </summary>
public class ListasDistinguenVacioPorFiltroTests
{
    /// <summary>
    /// Una pantalla "de lista con filtros" se reconoce por su barra, no por el
    /// nombre del fichero: eso dejaría fuera cualquier lista futura que se llame
    /// de otra forma.
    ///
    /// <para>
    /// <b>Son cuatro marcas y hay que quedarse con la UNIÓN, no elegir.</b> El
    /// detector empezó mirando solo <c>barra-filtros</c> y dejaba fuera a
    /// Centros y Subcontratas (<c>barra-trabajo-centros</c>): daba verde sobre
    /// dos pantallas que nunca miró. Al descubrir que <c>Bandeja.razor</c> se
    /// maqueta con clases propias, la tentación fue cambiar a detectar por
    /// componentes — y medido, ESO HABRÍA PERDIDO CINCO pantallas que el
    /// criterio por clase sí veía (Alertas, Auditoría, Auditoría IA, Macros y
    /// DocumentosGeneradosPanel), todas en la deuda de abajo: habrían salido
    /// del radar en silencio.
    /// </para>
    ///
    /// <para>
    /// Los dos criterios se solapan sin contenerse, así que van los dos: el
    /// estilo es libre, y los controles también se pueden envolver. Al añadir
    /// una marca, comprobar que la lista de detectadas CRECE — cambiar un
    /// criterio por otro es lo que a punto estuvo de reducir la cobertura
    /// mientras el trinquete seguía en verde.
    /// </para>
    /// </summary>
    private static readonly string[] MarcasDeListaConFiltros =
    [
        "barra-filtros",                   // la barra compartida de la mayoría
        "barra-trabajo",                   // Centros y Subcontratas
        "<FiltroEstado",                   // filtro por estado documental
        "CampoTexto Placeholder=\"Buscar", // buscador de lista (Bandeja y otras)
    ];

    /// <summary>
    /// Deuda congelada: pantallas que hoy NO distinguen los dos vacíos. El
    /// trinquete no las arregla — impide que la lista crezca. Cada vez que una
    /// se corrige, se borra de aquí; una pantalla nueva con filtros no puede
    /// entrar sin la rama, porque nadie va a añadirla a esta lista sin darse
    /// cuenta de lo que está haciendo.
    ///
    /// <para>
    /// <b>Está vacía desde el 2026-09-08</b>, y esa es toda la gracia: mientras
    /// tuvo entradas, el trinquete solo impedía que la lista creciera. Vacía,
    /// cualquier pantalla de lista nueva tiene que nacer distinguiendo los dos
    /// vacíos o no pasa. Volver a meter una entrada aquí es una decisión
    /// explícita, no un descuido — y por eso el motivo es obligatorio.
    /// </para>
    ///
    /// <para>
    /// Las nueve se cerraron en tres tandas: Auditoría, Auditoría IA y Tipos de
    /// Documento (#505); Vehículos, Incidencias y Gestiones (#506); y Alertas,
    /// Macros y DocumentosGeneradosPanel al final, <b>que son las que no se
    /// podían arreglar copiando</b>:
    /// <list type="bullet">
    /// <item><b>Alertas</b> filtra EN MEMORIA —<c>_alertas</c> trae todo—, así
    /// que es la única que SÍ sabe cuántas quedan fuera del filtro y la única
    /// cuya copia puede decir un número sin mentir.</item>
    /// <item><b>Macros</b> tiene un filtro que ENSANCHA: sin cliente devuelve
    /// solo las genéricas; con cliente, las genéricas más las suyas. «Quitar el
    /// filtro» ahí enseñaría MENOS, así que esa pantalla no lo ofrece — es la
    /// misma trampa de Visitas, donde copiar el patrón la habría empeorado.</item>
    /// <item><b>DocumentosGeneradosPanel</b> no es una página con ruta propia,
    /// sino un panel embebido en la pestaña «Generados» de Plantillas.</item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// Dos motivos apuntados aquí resultaron <b>falsos</b> al ir a arreglarlos.
    /// El de Tipos de Documento decía «ya tiene chips de filtro»: sus
    /// <c>chips-filtros</c> son los alias del formulario de edición, dentro del
    /// Drawer — ni chips ni estado vacío alguno. El de Macros decía «pantalla de
    /// Operación» sin más, y lo que tenía era un filtro con la semántica
    /// invertida. <b>Un motivo escrito de memoria no es una medición</b>: se
    /// comprueba en el código antes de actuar sobre él.
    /// </para>
    /// </summary>
    private static readonly Dictionary<string, string> DeudaCongelada = new();

    [Fact]
    public void Toda_lista_con_filtros_distingue_vacio_sin_registros_de_vacio_por_filtro()
    {
        var paginas = LocalizarPaginasRazor();

        paginas.Should().NotBeEmpty(
            "si el escáner no encuentra ninguna página, este trinquete estaría en verde por no mirar nada");

        var sinDistinguir = new List<string>();

        foreach (var (ruta, contenido) in paginas)
        {
            if (!MarcasDeListaConFiltros.Any(m => contenido.Contains(m, StringComparison.Ordinal))) continue;

            var nombre = Path.GetFileName(ruta);
            if (DeudaCongelada.ContainsKey(nombre)) continue;

            if (!DistingueLosDosVacios(contenido, LeerCodeBehind(ruta)))
                sinDistinguir.Add(nombre);
        }

        string.Join("\n", sinDistinguir.OrderBy(x => x)).Should().BeEmpty(
            "una lista con filtros que solo tiene un estado vacío le dice «crea el primero» a quien acaba de "
            + "filtrar, y eso termina en registros duplicados. Añade la rama con HayFiltrosActivos, o añade la "
            + "pantalla a DeudaCongelada CON su motivo");
    }

    /// <summary>
    /// Prueba de que el escáner mira donde dice mirar. Sin esto, un cambio de
    /// rutas dejaría el trinquete en verde por no encontrar ficheros — el modo
    /// de fallo más común de un ratchet de fuente.
    /// </summary>
    [Fact]
    public void El_escaner_encuentra_las_listas_conocidas()
    {
        var nombres = LocalizarPaginasRazor().Select(p => Path.GetFileName(p.Ruta)).ToList();

        nombres.Should().Contain("Empresas.razor").And.Contain("Trabajadores.razor")
            .And.Contain("Clientes.razor").And.Contain("Documentos.razor")
            .And.Contain("Centros.razor").And.Contain("Subcontratas.razor")
            .And.Contain("Bandeja.razor", "detectar por clase CSS la dejaba fuera: se maqueta con la suya propia");
    }

    /// <summary>
    /// Se acepta la PROPIEDAD, no una implementación concreta. Hay dos formas
    /// legítimas y las dos valen:
    /// <list type="bullet">
    /// <item>una guarda sobre <c>HayFiltrosActivos</c> — filtrado de servidor,
    /// donde la pantalla solo conoce el total ya filtrado (Empresas,
    /// Trabajadores, Centros…);</item>
    /// <item>un segundo estado vacío cuyo título habla de coincidencia con el
    /// filtro — filtrado en memoria, donde la pantalla sí sabe cuántos hay sin
    /// filtro (Usuarios: «Ningún usuario coincide»).</item>
    /// </list>
    /// Exigir solo la primera daba un falso positivo sobre Usuarios, que
    /// distingue los dos vacíos perfectamente desde antes que nadie escribiera
    /// este trinquete.
    ///
    /// <para>
    /// <b>El <c>[^)]*</c> obliga a una guarda plana</b>, sin paréntesis
    /// interiores: <c>if ((a || b) &amp;&amp; HayFiltrosActivos)</c> no casa y da
    /// falsa alarma sobre una pantalla correcta (le pasó a Auditoría y a
    /// Auditoría IA, y se resolvió aplanando la condición en una propiedad).
    /// <b>No se ensancha a la ligera</b> —por ejemplo, a un patrón que admita
    /// cualquier carácter hasta el fin de línea—: ensancharlo
    /// hace que pasen MÁS pantallas, que es la dirección que encoge en
    /// silencio la lista de infractoras. Avisar de más es el fallo barato.
    /// </para>
    /// </summary>
    /// <param name="contenido">El <c>.razor</c>.</param>
    /// <param name="codeBehind">Su <c>.razor.cs</c>, o cadena vacía si no tiene.</param>
    private static bool DistingueLosDosVacios(string contenido, string codeBehind)
    {
        // La propiedad se DECLARA en el code-behind y se USA en la plantilla, así
        // que hay que mirar los dos ficheros. Mirando solo el .razor, cambiar la
        // guarda por "if (false)" borraba de ahí la única mención y el trinquete
        // caía al criterio de título — que seguía escrito— y daba verde sobre una
        // rama muerta. Se descubrió por mutación sobre Centros, y hubo que
        // repetir la mutación DOS veces para verlo: la primera corrección
        // tampoco servía, por esta misma razón.
        if (Regex.IsMatch(codeBehind, @"\bHayFiltrosActivos\b"))
            return Regex.IsMatch(contenido, @"@?(else\s+)?if\s*\([^)]*HayFiltrosActivos");

        // Sin esa propiedad, vale el idioma de filtrado en memoria: un segundo
        // estado vacío que hable de coincidencia con el filtro (Usuarios).
        return Regex.IsMatch(contenido, @"Titulo=""[^""]*(coincide|con est[eo]s? filtro|con esta búsqueda)",
            RegexOptions.IgnoreCase);
    }

    /// <summary>El .razor.cs hermano, o cadena vacía si la página no tiene code-behind.</summary>
    private static string LeerCodeBehind(string rutaRazor)
    {
        var companero = rutaRazor + ".cs";
        return File.Exists(companero) ? File.ReadAllText(companero) : string.Empty;
    }

    private static List<(string Ruta, string Contenido)> LocalizarPaginasRazor()
    {
        var raiz = RaizDelRepositorio();
        var features = Path.Combine(raiz, "src", "CaeManager.Web", "Features");

        if (!Directory.Exists(features)) return [];

        return Directory
            .EnumerateFiles(features, "*.razor", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Select(f => (Ruta: f, Contenido: File.ReadAllText(f)))
            .ToList();
    }

    private static string RaizDelRepositorio()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "CaeManager.slnx")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? AppContext.BaseDirectory;
    }
}
