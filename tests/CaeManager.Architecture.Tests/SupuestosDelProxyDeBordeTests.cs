using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// Los dos supuestos de despliegue de los que depende que la IP del cliente sea
/// auténtica (REC-019). La aplicación acepta <c>X-Forwarded-For</c> de
/// cualquier remitente —eso lo mide <c>CabecerasDeProxyDeBordeTests</c>—, así
/// que lo único que impide falsificarla vive fuera del código C#: en el
/// Caddyfile y en el compose. Aquí se vigilan esos dos ficheros para que el
/// supuesto no se erosione en silencio.
///
/// <para>
/// <b>Medido el 2026-09-18</b> contra <c>caddy:2.11.4-alpine</c> con el digest
/// que fija el compose de producción: con el Caddyfile tal cual está, una
/// petición que llega con <c>X-Forwarded-For: 1.2.3.4</c> alcanza al backend
/// con la IP real del cliente y sin rastro del valor falsificado —igual con una
/// cadena de varios saltos y con la cabecera repetida—. La mutación
/// <c>trusted_proxies static 0.0.0.0/0</c> hace pasar el valor falso
/// (<c>X-Forwarded-For: 1.2.3.4, 172.20.0.5</c>), que es lo que demuestra que
/// la propiedad la gobierna esa directiva. La documentación de Caddy lo declara
/// en la opción global homónima: por defecto no se confía en ningún proxy.
/// </para>
/// </summary>
public class SupuestosDelProxyDeBordeTests
{
    [Fact]
    public void El_Caddyfile_no_confia_en_las_cabeceras_entrantes()
    {
        var caddyfile = File.ReadAllText(RutaDeDespliegue("Caddyfile"));

        caddyfile.Should().NotContainEquivalentOf("trusted_proxies",
            "es la directiva que hace a Caddy conservar el X-Forwarded-For que envía el cliente en vez de " +
            "reemplazarlo por la IP real. La aplicación no filtra por remitente (KnownProxies/KnownIPNetworks " +
            "vacíos, ver CabecerasDeProxyDeBorde), así que activarla aquí permitiría a cualquiera dictar la IP " +
            "con la que se particiona el limitador de tasa del login. Si hace falta de verdad —una CDN delante " +
            "de Caddy—, hay que acotar KnownIPNetworks en el mismo incremento y actualizar este trinquete.");

        caddyfile.Should().NotContainEquivalentOf("client_ip_headers",
            "cambia de qué cabecera sale la IP del cliente, con el mismo efecto que trusted_proxies");
    }

    /// <summary>
    /// La compresión del borde (ver la cabecera del snippet <c>comprimir</c> en el
    /// Caddyfile): un solo <c>encode</c>, el del snippet, que no comprime las
    /// peticiones con <c>Range</c> ni nada bajo <c>/cuenta</c>, importado en el
    /// nivel del sitio de los tres que sirven contenido. Un <c>encode</c> suelto en
    /// otro bloque comprimiría los 206 parciales, cuyo <c>Content-Range</c> cuenta
    /// bytes sin comprimir, y la clave TOTP de <c>/cuenta/configurar-2fa</c>, que
    /// convive con el <c>ReturnUrl</c> reflejado (BREACH).
    /// </summary>
    [Theory]
    [InlineData("{$DOMINIO} {")]
    [InlineData("staging.talveg.es {")]
    [InlineData("talveg.es {")]
    public void El_sitio_comprime_con_el_snippet_que_excluye_Range_y_cuenta(string cabeceraDelSitio)
    {
        var lineas = File.ReadAllText(RutaDeDespliegue("Caddyfile")).Replace("\r\n", "\n").Split('\n');

        var snippet = BloqueDeNivelSuperior(lineas, "(comprimir) {");
        DirectivasDelNivel(snippet).Should().Contain("encode @comprimible zstd gzip",
            "el encode del snippet tiene que ir condicionado por el matcher que excluye Range y /cuenta");
        DirectivasDelNivel(SubBloque(snippet, "@comprimible {")).Should()
            .Contain("not header Range *", "un 206 comprimido deja de cuadrar con su Content-Range")
            .And.Contain("not path /cuenta /cuenta/*",
                "/cuenta/configurar-2fa pinta la clave TOTP estable junto al ReturnUrl reflejado (BREACH)");

        lineas.Count(l => l.TrimStart().StartsWith("encode ", StringComparison.Ordinal)).Should().Be(1,
            "el único encode es el del snippet: uno suelto en un sitio no llevaría las exclusiones");

        DirectivasDelNivel(BloqueDeNivelSuperior(lineas, cabeceraDelSitio)).Should().Contain("import comprimir",
            $"el sitio '{cabeceraDelSitio}' tiene que importar el snippet en su propio nivel: dentro de " +
            "reverse_proxy o de un handle no vale (Caddy lo rechaza, o solo comprime esa rama)");
    }

    /// <summary>
    /// Las directivas del primer nivel de un bloque (sin las de sus subbloques),
    /// recortadas y sin comentarios. La profundidad se cuenta por llaves, no por
    /// sangría: una línea mal sangrada dentro de <c>reverse_proxy</c> sigue
    /// estando dentro.
    /// </summary>
    private static List<string> DirectivasDelNivel(string[] bloque)
    {
        var directivas = new List<string>();
        var profundidad = 0;
        foreach (var linea in bloque.Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')))
        {
            if (profundidad == 0)
                directivas.Add(linea);
            profundidad += linea.Count(c => c == '{') - linea.Count(c => c == '}');
        }
        return directivas;
    }

    /// <summary>
    /// El interior de un subbloque de primer nivel (p. ej. un matcher con nombre),
    /// desde su cabecera exacta hasta la llave que lo cierra.
    /// </summary>
    private static string[] SubBloque(string[] bloque, string cabecera)
    {
        var inicio = Array.FindIndex(bloque, l => l.Trim() == cabecera);
        inicio.Should().BeGreaterThan(-1, $"sin encontrar '{cabecera}' este trinquete no mira nada");

        var profundidad = 0;
        for (var i = inicio; i < bloque.Length; i++)
        {
            var linea = bloque[i].Trim();
            if (linea.StartsWith('#'))
                continue;
            profundidad += linea.Count(c => c == '{') - linea.Count(c => c == '}');
            if (profundidad == 0)
                return bloque[(inicio + 1)..i];
        }
        throw new InvalidOperationException($"el subbloque '{cabecera}' no se cierra");
    }

    /// <summary>
    /// Las líneas de un bloque de primer nivel del Caddyfile, desde su cabecera
    /// exacta hasta la llave que lo cierra en la columna 0. Comparación exacta
    /// de línea: "talveg.es {" no debe casar con "staging.talveg.es {".
    /// </summary>
    private static string[] BloqueDeNivelSuperior(string[] lineas, string cabecera)
    {
        var inicio = Array.IndexOf(lineas, cabecera);
        inicio.Should().BeGreaterThan(-1,
            $"sin encontrar '{cabecera}' este trinquete daría verde sin haber mirado nada");

        var fin = Array.IndexOf(lineas, "}", inicio);
        fin.Should().BeGreaterThan(inicio, $"el bloque '{cabecera}' tiene que cerrarse");

        return lineas[(inicio + 1)..fin];
    }

    [Theory]
    // Las dos ranuras de cada entorno (P1-F2). La verde hereda la definición de
    // la azul con `extends`: un `ports:` en la azul lo publica también la verde y
    // lo caza el caso de la azul; uno añadido solo a la verde, el de la verde.
    [InlineData("docker-compose.produccion.yml", "caemanager-app-azul")]
    [InlineData("docker-compose.produccion.yml", "caemanager-app-verde")]
    [InlineData("docker-compose.staging.yml", "caemanager-staging-app-azul")]
    [InlineData("docker-compose.staging.yml", "caemanager-staging-app-verde")]
    public void La_aplicacion_no_publica_puertos_al_exterior(string compose, string contenedor)
    {
        var servicio = BloqueDelServicioApp(File.ReadAllText(RutaDeDespliegue(compose)), contenedor);

        servicio.Should().NotMatchRegex(@"(?m)^\s{4}ports:",
            $"publicar el puerto de {contenedor} da acceso directo a Kestrel saltándose Caddy, y desde ahí " +
            "cualquiera impone el X-Forwarded-For que quiera. El único servicio con superficie pública de " +
            "estos stacks es Caddy.");
    }

    /// <summary>
    /// Recorta el bloque YAML del servicio <c>app</c> por su <c>container_name</c>,
    /// que es único en toda la máquina, y no por el nombre de servicio "app",
    /// que existe en los dos proyectos Compose a la vez. Se corta en el
    /// siguiente servicio de primer nivel (dos espacios de sangría) para no
    /// arrastrar el <c>ports:</c> legítimo de <c>caddy</c> o de <c>seq</c>.
    /// </summary>
    private static string BloqueDelServicioApp(string compose, string contenedor)
    {
        var lineas = compose.Split('\n');

        // Comparación exacta sobre la línea recortada, no Contains: el nombre
        // de producción es prefijo de cualquier `caemanager-app-loquesea`, así
        // que con Contains un renombrado casaba igual y el trinquete seguía en
        // verde mirando un servicio que ya no es el que cree (comprobado por
        // mutación: `caemanager-app-renombrada` pasaba sin inmutarse).
        var inicio = Array.FindIndex(lineas, l => l.Trim() == $"container_name: {contenedor}");
        inicio.Should().BeGreaterThan(-1,
            $"sin encontrar '{contenedor}' este trinquete daría verde sin haber mirado nada: un servicio " +
            "renombrado tiene que ponerlo en rojo, no volverlo ciego");

        // Hacia atrás hasta la cabecera del servicio (dos espacios de sangría).
        var cabecera = inicio;
        while (cabecera > 0 && !Regex.IsMatch(lineas[cabecera], @"^  \S.*:\s*$"))
            cabecera--;

        var fin = cabecera + 1;
        while (fin < lineas.Length && !Regex.IsMatch(lineas[fin], @"^  \S.*:\s*$") && !Regex.IsMatch(lineas[fin], @"^\S"))
            fin++;

        return string.Join('\n', lineas[cabecera..fin]);
    }

    private static string RutaDeDespliegue(string fichero) =>
        Path.Combine(RaizDelRepositorio(), "deploy", "local", fichero);

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
