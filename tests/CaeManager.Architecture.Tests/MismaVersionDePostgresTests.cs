using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// Todo PostgreSQL que el repositorio levanta —servicios de CI, compose de
/// desarrollo, staging, ensayo de restauración— usa exactamente la misma imagen
/// que producción: etiqueta de versión mayor y menor más digest (P1-C1).
///
/// <para>
/// <b>Por qué.</b> Hasta el 2026-09-26 los cuatro servicios de CI y el compose
/// de desarrollo corrían <c>postgres:17</c> mientras producción corría
/// <c>postgres:18.6</c>: la suite de integración, la de E2E y las pruebas de
/// RLS daban evidencia de un motor que no era el de producción. La referencia
/// es el servicio <c>db</c> de <c>deploy/local/docker-compose.produccion.yml</c>:
/// la CI se alinea con producción, nunca al revés. Cuando Dependabot proponga
/// el siguiente parche en ese compose, este test se pone en rojo hasta que la
/// misma PR suba también el resto de sitios — ese es el comportamiento buscado.
/// </para>
///
/// <para>
/// <b>Registro de origen.</b> Los servicios de CI descargan esa misma imagen del
/// espejo público de las imágenes oficiales de Docker en Amazon ECR Public
/// (<c>public.ecr.aws/docker/library/</c>) porque Docker Hub limita las
/// descargas sin autenticar desde las IP compartidas de los runners (2026-10-09:
/// <c>toomanyrequests</c> en «Initialize containers», cola de fusión caída). El
/// digest identifica el contenido con independencia del registro que lo sirva,
/// así que ese prefijo —y solo ese— se retira antes de comparar: la etiqueta y
/// el digest tienen que seguir siendo los de producción. Una referencia desde
/// cualquier otro registro se ve y cuenta como divergente, y producción misma
/// se sigue exigiendo sin prefijo. En <c>.github/workflows/</c> el espejo es
/// además obligatorio en todas las referencias: una sola que vuelva a Docker
/// Hub devuelve el fallo a su job aunque el digest coincida.
/// </para>
///
/// <para>
/// Fuera de su alcance: el PostgreSQL local que no levanta el repositorio (un
/// servicio instalado en la máquina del desarrollador). Ese no lo puede
/// observar un test que lee ficheros.
/// </para>
/// </summary>
public class MismaVersionDePostgresTests
{
    private const string ComposeDeProduccion = "deploy/local/docker-compose.produccion.yml";

    // Único origen distinto de Docker Hub que se da por equivalente: el espejo
    // de las imágenes oficiales en Amazon ECR Public (ver el resumen de la clase).
    private const string EspejoDeImagenesOficiales = "public.ecr.aws/docker/library/";

    // Prefijo de registro y ruta delante del nombre (`docker.io/library/`,
    // `public.ecr.aws/docker/library/`, `localhost:5000/`…). Se captura
    // cualquiera, no solo los conocidos: un registro que la expresión no viera
    // dejaría la referencia fuera del recorrido en vez de marcarla.
    private const string PrefijoDeRegistro = @"(?:[\w.\-]+(?::\d+)?/)*";

    // Línea `image: postgres…` (con o sin prefijo de registro y con o sin
    // etiqueta: una imagen sin etiqueta es `latest` y también diverge).
    private static readonly Regex LineaDeImagen = new(
        @"^\s*-?\s*image:\s*[""']?(?<ref>" + PrefijoDeRegistro + @"postgres(?:[:@][^\s""'}]*)?)[""']?\s*$",
        RegexOptions.Compiled);

    // Cualquier otra referencia con etiqueta numérica fuera de `image:`, como el
    // valor por defecto de una variable de shell (`${X:-postgres:18}`). Un
    // guion delante solo cuenta como separador si es el de `:-`; `mi-postgres:1`
    // es otra imagen.
    private static readonly Regex ReferenciaSuelta = new(
        @"(?<![\w./]|(?<!:)-)(?<ref>" + PrefijoDeRegistro + @"postgres:\d[^\s""'}]*)",
        RegexOptions.Compiled);

    [Fact]
    public void Produccion_fija_postgres_por_version_exacta_y_digest()
    {
        ImagenDeProduccion().Should().MatchRegex(
            @"^postgres:\d+\.\d+@sha256:[0-9a-f]{64}$",
            "la referencia contra la que se compara todo lo demás no puede ser una etiqueta flotante");
    }

    [Fact]
    public void Todo_postgres_del_repositorio_usa_la_imagen_de_produccion()
    {
        var produccion = ImagenDeProduccion();
        var referencias = ReferenciasDelRepositorio();

        // Control positivo: si el recorrido deja de ver estos sitios, un verde
        // no diría nada (un recorrido vacío también "coincide" con producción).
        referencias.Count(r => r.Fichero == ".github/workflows/ci.yml").Should().BeGreaterThanOrEqualTo(3,
            "ci.yml tiene tres servicios de PostgreSQL (tests-integracion, E2E y k6)");
        referencias.Select(r => r.Fichero).Should().Contain(new[]
        {
            ".github/workflows/integraciones-con-clave.yml",
            "docker-compose.yml",
            "deploy/local/docker-compose.staging.yml",
            "scripts/ensayo-restauracion-borg.sh",
            ComposeDeProduccion,
        });

        // Todo PostgreSQL de los workflows se descarga del espejo, no solo alguno:
        // un único servicio que vuelva a `image: postgres:…` (un merge mal
        // resuelto, un servicio nuevo copiado de un compose) seguiría
        // coincidiendo con producción por etiqueta y digest, y devolvería el
        // `toomanyrequests` de Docker Hub a ese job. Se afirma por negación para
        // que el fallo nombre fichero y línea; que la lista de partida no está
        // vacía lo garantizan los controles positivos de arriba.
        var workflowsSinEspejo = referencias
            .Where(r => r.Fichero.StartsWith(".github/workflows/", StringComparison.Ordinal))
            .Where(r => !r.Imagen.StartsWith(EspejoDeImagenesOficiales, StringComparison.Ordinal))
            .Select(r => $"{r.Fichero}:{r.Linea}: {r.Imagen}")
            .ToList();

        workflowsSinEspejo.Should().BeEmpty(
            $"los servicios de PostgreSQL de la CI se descargan de {EspejoDeImagenesOficiales} y no de Docker Hub, " +
            "que limita las descargas sin autenticar desde las IP compartidas de los runners");

        var divergentes = referencias
            .Where(r => SinEspejo(r.Imagen) != produccion)
            .Select(r => $"{r.Fichero}:{r.Linea}: {r.Imagen}")
            .ToList();

        divergentes.Should().BeEmpty(
            $"producción ({ComposeDeProduccion}) usa {produccion} y la CI se alinea con producción, nunca al revés");
    }

    // Mismo nombre, etiqueta y digest servidos desde el espejo son la misma
    // imagen; cualquier otro prefijo se conserva y hace divergir la referencia.
    private static string SinEspejo(string imagen) =>
        imagen.StartsWith(EspejoDeImagenesOficiales, StringComparison.Ordinal)
            ? imagen[EspejoDeImagenesOficiales.Length..]
            : imagen;

    private static string ImagenDeProduccion()
    {
        var enProduccion = ReferenciasDe(ComposeDeProduccion).ToList();
        enProduccion.Should().ContainSingle("el compose de producción tiene un único servicio de PostgreSQL (db)");
        return enProduccion[0].Imagen;
    }

    private static List<Referencia> ReferenciasDelRepositorio()
    {
        var raiz = RaizDelRepositorio();
        var candidatos = new List<string>();

        candidatos.AddRange(Directory.EnumerateFiles(raiz, "docker-compose*.y*ml", SearchOption.TopDirectoryOnly));
        foreach (var dir in new[] { ".github", "deploy", "scripts" })
        {
            var ruta = Path.Combine(raiz, dir);
            if (!Directory.Exists(ruta))
                continue;
            candidatos.AddRange(Directory.EnumerateFiles(ruta, "*", SearchOption.AllDirectories)
                .Where(f => f.EndsWith(".yml") || f.EndsWith(".yaml") || f.EndsWith(".sh"))
                // Los *.tests.sh llevan etiquetas de juguete como datos de prueba.
                .Where(f => !f.EndsWith(".tests.sh")));
        }

        return candidatos
            .Select(f => Path.GetRelativePath(raiz, f).Replace('\\', '/'))
            .Distinct()
            .SelectMany(ReferenciasDe)
            .ToList();
    }

    private static IEnumerable<Referencia> ReferenciasDe(string ficheroRelativo)
    {
        var lineas = File.ReadAllLines(Path.Combine(RaizDelRepositorio(), ficheroRelativo));
        for (var i = 0; i < lineas.Length; i++)
        {
            var linea = lineas[i];
            if (linea.TrimStart().StartsWith('#'))
                continue;

            var deImagen = LineaDeImagen.Match(linea);
            if (deImagen.Success)
            {
                yield return new Referencia(ficheroRelativo, i + 1, deImagen.Groups["ref"].Value);
                continue;
            }

            foreach (Match suelta in ReferenciaSuelta.Matches(linea))
                yield return new Referencia(ficheroRelativo, i + 1, suelta.Groups["ref"].Value);
        }
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

    private sealed record Referencia(string Fichero, int Linea, string Imagen);
}
