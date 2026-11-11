using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// Todo comando con apt de los workflows pasa por la acción compuesta
/// <c>.github/actions/apt-acotado</c>.
///
/// <para>
/// <b>Por qué.</b> Desde las 22:50 UTC del 2026-10-31, algunos runners no
/// alcanzaban el espejo de Azure y <c>apt-get update</c> se quedaba parado sin
/// escribir nada hasta que el techo del job lo cortaba, a los 60 o 75 minutos.
/// Un solo job colgado deja el
/// <c>merge_group</c> sin responder y la cola expulsa la PR con
/// <c>checks_timed_out</c>. <c>playwright install --with-deps</c> lanza el mismo
/// <c>apt-get</c> por dentro, así que también entra en la regla.
/// </para>
/// </summary>
public class AptAcotadoEnCiTests
{
    private const string UsoDeLaAccion = "uses: ./.github/actions/apt-acotado";

    private static readonly Regex UsaApt = new(
        @"\b(apt|apt-get|aptitude)\s+(-\S+\s+)*(update|upgrade|dist-upgrade|install)\b" +
        @"|--with-deps|\binstall-deps\b|\badd-apt-repository\b|\bdpkg\s+-i\b",
        RegexOptions.Compiled);

    private static readonly Regex InicioDePaso = new(@"^\s*- [a-z-]+:", RegexOptions.Compiled);

    [Fact]
    public void Ningun_comando_con_apt_de_los_workflows_queda_fuera_de_la_accion_acotada()
    {
        var workflows = Directory.EnumerateFiles(
            Path.Combine(RaizDelRepositorio(), ".github", "workflows"), "*.y*ml").ToList();
        workflows.Should().NotBeEmpty("el test tiene que estar mirando los workflows de verdad");

        var sinAcotar = new List<string>();
        var acotados = 0;

        foreach (var ruta in workflows)
        {
            var lineas = File.ReadAllLines(ruta);

            for (var i = 0; i < lineas.Length; i++)
            {
                if (lineas[i].TrimStart().StartsWith('#') || !UsaApt.IsMatch(lineas[i]))
                    continue;

                if (PasoDe(lineas, i).Any(linea => linea.Trim() == UsoDeLaAccion))
                    acotados++;
                else
                    sinAcotar.Add($"{Path.GetFileName(ruta)}:{i + 1}: {lineas[i].Trim()}");
            }
        }

        acotados.Should().BePositive("si no encuentra ningún comando con apt, el test no prueba nada");
        sinAcotar.Should().BeEmpty(
            "un apt sin límite puede colgar el job una hora y dejar la cola de fusión sin respuesta");
    }

    [Theory]
    [InlineData("timeout -k 15 \"$LIMITE\" bash", "cada intento tiene que tener límite")]
    [InlineData("timeout -k 5 120 dpkg --configure -a", "la limpieza tampoco puede colgar el paso")]
    [InlineData("curl -fsS --max-time 10", "el sondeo del espejo evita el cuelgue conocido")]
    [InlineData("Acquire::http::Timeout", "apt no debe esperar 120 s por conexión")]
    [InlineData("DPkg::Lock::Timeout", "el reintento debe esperar el cerrojo, no fallar")]
    [InlineData("elegir-espejo.sh", "sin Azure ni archive.ubuntu.com, apt tiene que pasar a un espejo de reserva")]
    public void La_accion_acota_de_verdad(string fragmento, string motivo)
    {
        var accion = File.ReadAllText(Path.Combine(
            RaizDelRepositorio(), ".github", "actions", "apt-acotado", "action.yml"));

        accion.Should().Contain(fragmento, motivo);
    }

    /// <summary>
    /// Desde el 2026-11-11 hay runners que no resuelven ningún nombre de ubuntu.com:
    /// la lista de reserva solo sirve si tiene espejos de otro dominio, si sondea con
    /// límite y si comprueba también el pocket de security que va a servir.
    /// </summary>
    [Fact]
    public void La_lista_de_espejos_tiene_reservas_fuera_de_ubuntu_com_y_sondea_security()
    {
        var guion = File.ReadAllText(Path.Combine(
            RaizDelRepositorio(), ".github", "actions", "apt-acotado", "elegir-espejo.sh"));

        var hosts = Regex.Matches(guion, @"^\s*https://([a-z0-9.-]+)/ubuntu/\s*$", RegexOptions.Multiline)
            .Select(m => m.Groups[1].Value)
            .ToList();

        hosts.Should().NotBeEmpty("el test tiene que estar leyendo la lista de candidatos de verdad");
        hosts[0].Should().Be("archive.ubuntu.com", "el archivo oficial va antes que cualquier reserva");
        hosts.Where(h => !h.EndsWith("ubuntu.com", StringComparison.Ordinal)).Should().NotBeEmpty(
            "si todos los candidatos son de ubuntu.com, un fallo de DNS de ese dominio los tumba a la vez");
        guion.Should().Contain("curl -fsS --max-time 10", "una sonda sin límite reintroduce el cuelgue");
        guion.Should().Contain("-security/InRelease", "el espejo elegido sirve también el pocket de security");
    }

    /// <summary>
    /// Un espejo de reserva solo sirve los ficheros: la confianza la da la firma de cada
    /// InRelease con las claves de Ubuntu de la imagen. Nada del CI ni de la imagen puede
    /// desactivar esa verificación para que un espejo «funcione».
    /// </summary>
    [Fact]
    public void Ninguna_fuente_de_apt_desactiva_la_verificacion_de_firma()
    {
        var raiz = RaizDelRepositorio();
        var ficheros = Directory.EnumerateFiles(Path.Combine(raiz, ".github"), "*", SearchOption.AllDirectories)
            .Append(Path.Combine(raiz, "Dockerfile"))
            .ToList();
        var prohibido = new Regex(
            @"trusted\s*=\s*yes|Trusted:\s*yes|AllowInsecureRepositories|AllowDowngradeToInsecureRepositories|" +
            @"allow-unauthenticated|AllowUnauthenticated|allow-insecure|allow-weak|AllowWeak|--force-yes|" +
            @"Verify-Peer\W+false|Verify-Host\W+false|Check-Valid-Until\W+(false|no)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        var hallazgos = ficheros
            .SelectMany(ruta => File.ReadAllLines(ruta)
                .Select((linea, i) => (ruta, linea, i))
                .Where(x => !x.linea.TrimStart().StartsWith('#') && prohibido.IsMatch(x.linea)))
            .Select(x => $"{Path.GetRelativePath(raiz, x.ruta)}:{x.i + 1}: {x.linea.Trim()}")
            .ToList();

        ficheros.Should().Contain(f => f.EndsWith("elegir-espejo.sh", StringComparison.Ordinal),
            "el test tiene que estar mirando la acción de apt de verdad");
        hallazgos.Should().BeEmpty("apt verifica cada InRelease con las claves de Ubuntu, también en un espejo de reserva");
    }

    /// <summary>
    /// La imagen que se despliega usa las fuentes de su base: ESPEJO_UBUNTU va vacío por
    /// defecto y solo el job de imagen del CI lo rellena, a partir de su sonda.
    /// </summary>
    [Fact]
    public void El_espejo_de_la_imagen_va_vacio_por_defecto_y_solo_lo_pasa_la_sonda_del_ci()
    {
        var raiz = RaizDelRepositorio();
        var dockerfile = LineasSinComentario(Path.Combine(raiz, "Dockerfile"));
        var ci = LineasSinComentario(Path.Combine(raiz, ".github", "workflows", "ci.yml"));
        var accion = LineasSinComentario(Path.Combine(raiz, ".github", "actions", "apt-acotado", "action.yml"));
        var despliegue = File.ReadAllText(Path.Combine(raiz, ".github", "workflows", "deploy.yml"));
        var nocturno = File.ReadAllText(Path.Combine(raiz, ".github", "workflows", "trivy-nocturno.yml"));

        dockerfile.Should().Contain("ARG ESPEJO_UBUNTU=",
            "sin valor por defecto vacío, la imagen de producción dejaría de usar archive.ubuntu.com");
        ci.Should().Contain("build-args: ${{ steps.espejo-ubuntu.outputs.build-args }}",
            "el job de imagen tiene que pasar el espejo que eligió su sonda");
        ci.Should().Contain(l => l.Contains("bash .github/actions/apt-acotado/elegir-espejo.sh", StringComparison.Ordinal),
            "la sonda de la imagen usa la misma lista de espejos que apt-acotado");
        accion.Should().Contain(l => l.Contains("bash \"$GITHUB_ACTION_PATH/elegir-espejo.sh\"", StringComparison.Ordinal),
            "apt-acotado tiene que invocar la sonda de espejos, no solo nombrarla en un comentario");
        // Decisión de la coordinadora (2026-11-11): la imagen de producción y la del
        // escaneo nocturno solo se construyen con las fuentes oficiales de la base; si el
        // DNS de ubuntu.com no vuelve, el despliegue espera.
        despliegue.Should().NotContain("ESPEJO_UBUNTU",
            "la imagen de producción solo se construye con las fuentes oficiales de su base");
        nocturno.Should().NotContain("ESPEJO_UBUNTU",
            "el escaneo nocturno construye la misma imagen que producción, con sus fuentes oficiales");
    }

    /// <summary>
    /// El job de imagen ya ha sondeado las fuentes http de la base cuando llama a la lista:
    /// sin <c>solo-reserva</c> le volvería archive.ubuntu.com, que el Dockerfile rechaza. El
    /// guion tiene que quitar de verdad ese candidato y no ignorar un argumento mal escrito.
    /// </summary>
    [Fact]
    public void El_job_de_imagen_pide_solo_reservas_y_el_guion_las_filtra()
    {
        var raiz = RaizDelRepositorio();
        var ci = LineasSinComentario(Path.Combine(raiz, ".github", "workflows", "ci.yml"));
        var guion = LineasSinComentario(Path.Combine(raiz, ".github", "actions", "apt-acotado", "elegir-espejo.sh"));

        ci.Should().Contain(
            l => l.Contains("elegir-espejo.sh", StringComparison.Ordinal) && l.Contains("solo-reserva", StringComparison.Ordinal),
            "el job de imagen solo puede recibir un espejo fuera de ubuntu.com");
        guion.Should().Contain("candidatos=(\"${candidatos[@]:1}\") ;;",
            "solo-reserva tiene que quitar archive.ubuntu.com de la lista");
        guion.Should().Contain(l => l.StartsWith("exit 2", StringComparison.Ordinal),
            "un segundo argumento desconocido tiene que fallar, no tratarse como la lista completa");
    }

    /// <summary>
    /// ESPEJO_UBUNTU se interpola en un programa de sed: solo puede llegar a él un valor
    /// que sea, entero, uno de los espejos de reserva de elegir-espejo.sh. Un <c>case</c>
    /// con patrones literales compara el valor completo, así que tampoco pasa uno
    /// multilínea; una expresión por líneas (grep -x) no bastaba. Tras el sed, cada
    /// URIs: tiene que ser exactamente el espejo y no puede perderse ningún Signed-By.
    /// </summary>
    [Fact]
    public void La_lista_blanca_del_Dockerfile_es_exactamente_la_de_reservas_y_va_antes_del_sed()
    {
        var raiz = RaizDelRepositorio();
        var guion = File.ReadAllText(Path.Combine(raiz, ".github", "actions", "apt-acotado", "elegir-espejo.sh"));
        var reservas = Regex.Matches(guion, @"^\s*(https://[a-z0-9.-]+/ubuntu/)\s*$", RegexOptions.Multiline)
            .Select(m => m.Groups[1].Value)
            .Skip(1)
            .ToList();
        var dockerfile = LineasSinComentario(Path.Combine(raiz, "Dockerfile"));

        var lineaDeAdmitidos = dockerfile.Single(l => l.StartsWith("https://", StringComparison.Ordinal) && l.Contains(") ;;", StringComparison.Ordinal));
        var admitidos = lineaDeAdmitidos[..lineaDeAdmitidos.IndexOf(')', StringComparison.Ordinal)].Split('|');

        reservas.Should().NotBeEmpty("el test tiene que estar leyendo la lista de candidatos de verdad");
        admitidos.Should().Equal(reservas, "la lista blanca del Dockerfile y la de elegir-espejo.sh no pueden divergir");
        admitidos.Should().OnlyContain(a => Regex.IsMatch(a, @"^https://[a-z0-9.-]+/ubuntu/$"),
            "sin comodines: cada patrón del case es un espejo literal");

        var indiceCase = dockerfile.FindIndex(l => l.StartsWith("case \"$ESPEJO_UBUNTU\" in", StringComparison.Ordinal));
        var indiceSed = dockerfile.FindIndex(l => l.Contains("${ESPEJO_UBUNTU}", StringComparison.Ordinal));
        indiceCase.Should().BeGreaterThan(-1, "el valor se filtra con un case de patrones literales");
        indiceCase.Should().BeLessThan(indiceSed, "el filtro va antes de interpolar el valor en sed");
        dockerfile.Should().Contain(l => l.Contains("grep -vqxF \"URIs: $ESPEJO_UBUNTU\"", StringComparison.Ordinal),
            "cada URIs: tiene que quedar exactamente en el espejo");
        dockerfile.Should().Contain(l => l.Contains("= \"$firmas\" ]", StringComparison.Ordinal),
            "la reescritura no puede perder ningún Signed-By");
    }

    /// <summary>
    /// La acción corre sin errexit para conservar los reintentos: cada preparación de apt
    /// (configuración y fuentes) se comprueba a mano y, si falla, apt no llega a correr
    /// con reintentos activos ante un espejo de terceros ni con fuentes a medias.
    /// </summary>
    [Fact]
    public void La_preparacion_de_apt_falla_cerrada()
    {
        var accion = LineasSinComentario(Path.Combine(
            RaizDelRepositorio(), ".github", "actions", "apt-acotado", "action.yml"));

        accion.Should().Contain(l => l.StartsWith("escribir_conf_apt 1 || {", StringComparison.Ordinal),
            "sin la configuración inicial, apt esperaría 120 s por conexión");
        accion.Should().Contain("escribir_conf_apt 0 || return 1",
            "con un espejo de terceros, apt no puede correr con reintentos activos");
        accion.Should().Contain("apuntar_fuentes_a \"$base\" || return 1",
            "un fallo al reescribir las fuentes no puede quedar oculto");
        accion.Should().Contain(l => l.Contains("grep -vqxF \"URIs: $1\"", StringComparison.Ordinal),
            "cada URIs: tiene que quedar exactamente en la base elegida");
        accion.Should().Contain(l => l.Contains("= \"$firmas\" ] || return 1", StringComparison.Ordinal),
            "la reescritura no puede perder ningún Signed-By");
        accion.Should().NotContain("usar_archivo_directo",
            "llamar a la preparación sin comprobarla deja correr apt con la preparación fallida");
        accion.Count(l => l.Contains("preparar_o_fallar", StringComparison.Ordinal)).Should().BeGreaterThanOrEqualTo(3,
            "la definición, la salida de Azure y cada reintento pasan por la preparación comprobada");
    }

    private static List<string> LineasSinComentario(string ruta) =>
        File.ReadAllLines(ruta)
            .Select(l => l.Trim())
            .Where(l => !l.StartsWith('#'))
            .ToList();

    private static IEnumerable<string> PasoDe(string[] lineas, int indice)
    {
        var inicio = indice;
        while (inicio > 0 && !InicioDePaso.IsMatch(lineas[inicio]))
            inicio--;

        var fin = indice + 1;
        while (fin < lineas.Length && !InicioDePaso.IsMatch(lineas[fin]))
            fin++;

        return lineas[inicio..fin];
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
