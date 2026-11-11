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
    [InlineData("curl -sS --max-time 10 --max-redirs 0", "el sondeo del espejo evita el cuelgue conocido")]
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
        guion.Should().Contain("curl -sS --max-time 10", "una sonda sin límite reintroduce el cuelgue");
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
        dockerfile.Should().Contain(l => l.StartsWith("*)", StringComparison.Ordinal) && l.Contains("exit 1", StringComparison.Ordinal),
            "un valor fuera de la lista blanca tiene que abortar el RUN, no seguir hasta el sed");
        dockerfile.Should().Contain(l => l.Contains("&& ! grep '^URIs:' /etc/apt/sources.list.d/ubuntu.sources | grep -vqxF \"URIs: $ESPEJO_UBUNTU\"", StringComparison.Ordinal),
            "cada URIs: tiene que quedar exactamente en el espejo");
        dockerfile.Should().Contain(l => l.Contains("= \"$firmas\" ]", StringComparison.Ordinal),
            "la reescritura no puede perder ningún Signed-By");
    }

    /// <summary>
    /// La acción corre sin errexit para conservar los reintentos: cada preparación de apt
    /// (configuración y fuentes) se comprueba a mano y, si falla, apt no llega a correr
    /// sin su configuración ni con fuentes a medias.
    /// </summary>
    [Fact]
    public void La_preparacion_de_apt_falla_cerrada()
    {
        var accion = LineasSinComentario(Path.Combine(
            RaizDelRepositorio(), ".github", "actions", "apt-acotado", "action.yml"));

        var indiceConfiguracion = accion.FindIndex(l => l.StartsWith("escribir_conf_apt || {", StringComparison.Ordinal));
        var indiceSondaAzure = accion.FindIndex(l => l.StartsWith("if ! responde_200 \"$sonda\"", StringComparison.Ordinal));
        indiceConfiguracion.Should().BeGreaterThan(-1,
            "sin la configuración inicial, apt esperaría 120 s por conexión y reintentaría descargas");
        indiceConfiguracion.Should().BeLessThan(indiceSondaAzure,
            "la configuración se escribe antes de cualquier camino que llegue a ejecutar apt");
        accion[indiceConfiguracion].Should().Contain("exit 1", "un fallo al escribir la configuración corta el paso");
        accion.Should().Contain("apuntar_fuentes_a \"$base\" || return 1",
            "un fallo al reescribir las fuentes no puede quedar oculto");
        accion.Should().Contain("! grep '^URIs:' \"$fuentes\" | grep -vqxF \"URIs: $1\" || return 1",
            "cada URIs: tiene que quedar exactamente en la base elegida; sin el ! la reserva fallaría siempre");
        accion.Should().Contain(l => l.Contains("= \"$firmas\" ] || return 1", StringComparison.Ordinal),
            "la reescritura no puede perder ningún Signed-By");
        accion.Where(l => l.Contains("usar_archivo_directo", StringComparison.Ordinal)).Should().BeEquivalentTo(
            ["usar_archivo_directo() {", "usar_archivo_directo && return 0"],
            "la preparación solo se llama desde preparar_o_fallar, que corta antes de ejecutar apt");
        accion.Count(l => l.Contains("preparar_o_fallar", StringComparison.Ordinal)).Should().BeGreaterThanOrEqualTo(3,
            "la definición, la salida de Azure y cada reintento pasan por la preparación comprobada");
    }

    /// <summary>
    /// Codex r2 (#2168): <c>curl -f</c> sin <c>-L</c> da por buena una respuesta 3xx, así
    /// que se podía elegir un espejo cuyos InRelease no se habían alcanzado. Cada sonda
    /// (la de Azure en la acción, la lista de espejos y la del job de imagen) exige un
    /// 200 final y no sigue redirecciones.
    ///
    /// <para>
    /// Este test lee el texto de los guiones; no ejecuta bash ni simula respuestas HTTP.
    /// </para>
    /// </summary>
    [Fact]
    public void Las_sondas_de_apt_exigen_un_200_final_sin_seguir_redirecciones()
    {
        var raiz = RaizDelRepositorio();
        var lineasCi = File.ReadAllLines(Path.Combine(raiz, ".github", "workflows", "ci.yml"));
        var indicePasoImagen = Array.FindIndex(lineasCi, l => l.Trim() == "id: espejo-ubuntu");
        indicePasoImagen.Should().BeGreaterThan(-1, "el test tiene que encontrar el paso de la sonda de la imagen");

        var fuentes = new Dictionary<string, List<string>>
        {
            ["action.yml"] = LineasSinComentario(Path.Combine(raiz, ".github", "actions", "apt-acotado", "action.yml")),
            ["elegir-espejo.sh"] = LineasSinComentario(Path.Combine(raiz, ".github", "actions", "apt-acotado", "elegir-espejo.sh")),
            ["ci.yml (espejo-ubuntu)"] = PasoDe(lineasCi, indicePasoImagen)
                .Select(l => l.Trim())
                .Where(l => !l.StartsWith('#'))
                .ToList(),
        };
        var siguePorRedireccion = new Regex(@"(^|\s)(--location(-trusted)?|-[A-Za-z]*L[A-Za-z]*)(\s|$)", RegexOptions.Compiled);

        // El cuerpo entero de la sonda, línea a línea: el 200 exacto (no «!= 200»), el
        // código de salida de curl (un 200 cortado o colgado a mitad no vale) y sin -L.
        string[] sonda =
        [
            "responde_200() {",
            "local codigo",
            "codigo=\"$(curl -sS --max-time 10 --max-redirs 0 -o /dev/null -w '%{http_code}' \"$1\")\" || return 1",
            "[ \"$codigo\" = \"200\" ]",
            "}",
        ];
        var llamadasMinimas = new Dictionary<string, int>
        {
            ["action.yml"] = 1,
            ["elegir-espejo.sh"] = 2,
            ["ci.yml (espejo-ubuntu)"] = 2,
        };

        foreach (var (nombre, lineas) in fuentes)
        {
            var curls = lineas.Where(l => Regex.IsMatch(l, @"\bcurl\s")).ToList();

            curls.Should().NotBeEmpty($"{nombre}: el test tiene que estar mirando la sonda de verdad");
            IndiceDeSecuencia(lineas, sonda).Should().BeGreaterThan(-1,
                $"{nombre}: la sonda exige un 200 final, sin redirección y con la descarga completa");
            curls.Should().OnlyContain(l => l == sonda[2],
                $"{nombre}: ninguna otra llamada a curl puede decidir la sonda por su cuenta");
            curls.Should().NotContain(l => siguePorRedireccion.IsMatch(l),
                $"{nombre}: la sonda no sigue redirecciones: el 200 tiene que darlo el propio espejo");
            lineas.Count(l => Regex.IsMatch(l, @"\bresponde_200\s+""")).Should().BeGreaterThanOrEqualTo(llamadasMinimas[nombre],
                $"{nombre}: cada InRelease sondeado pasa por responde_200");
        }
    }

    /// <summary>
    /// Codex r3 (#2168): el CVE-2026-109518 (ver el Dockerfile) deja colar un paquete no
    /// verificado en un reintento de descarga, sea desde un espejo de terceros o desde un
    /// atacante en la ruta de una fuente HTTP. Decidir los reintentos leyendo las fuentes
    /// admitía HTTP y no veía todas las fuentes de apt, así que ningún apt del CI ni de la
    /// imagen reintenta: toda asignación de <c>Acquire::Retries</c> (o
    /// <c>APT::Acquire::Retries</c>) que no sea comentario vale 0, no hay ningún
    /// <c>Retries</c> suelto (la sintaxis de bloque de apt, que este test no sabría leer) y
    /// apt-acotado comprueba con apt-config el valor que apt lee de verdad.
    ///
    /// <para>
    /// Lee el texto de todo <c>.github</c> y de los Dockerfile de la raíz; no ejecuta apt ni
    /// mira la configuración que traiga la imagen del runner (eso lo comprueba la acción
    /// con apt-config en cada job).
    /// </para>
    /// </summary>
    [Fact]
    public void Ningun_apt_del_ci_ni_de_la_imagen_reintenta_descargas()
    {
        var raiz = RaizDelRepositorio();
        var rutas = Directory.EnumerateFiles(Path.Combine(raiz, ".github"), "*", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(raiz, "Dockerfile*", SearchOption.TopDirectoryOnly))
            .ToList();
        var lineas = rutas
            .SelectMany(r => LineasSinComentario(r).Select(l => (fichero: Path.GetRelativePath(raiz, r), linea: l)))
            .ToList();
        // Toda aparición de Acquire::Retries tiene que ser una asignación que este test sepa
        // leer (con = o con espacio, con o sin comillas y con o sin :: final); la única
        // excepción es la línea exacta de la consulta con apt-config, fijada más abajo.
        const string consultaAptConfig =
            "&& [ \"$(apt-config shell R Acquire::Retries A APT::Acquire::Retries)\" = \"R='0'\"$'\\n'\"A='0'\" ]";
        var aparicion = new Regex(@"Acquire::Retries", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        var asignacion = new Regex(@"\GAcquire::Retries(::)?(\s*=\s*|\s+)\\?[""']?(?<valor>[^""'\\\s;]*)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        var suelto = new Regex(@"(?<![-\w:])Retries\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        var asignaciones = lineas
            .Where(x => x.linea != consultaAptConfig)
            .SelectMany(x => aparicion.Matches(x.linea).Select(m => asignacion.Match(x.linea, m.Index)).Select(a =>
                (x.fichero, x.linea, valor: a.Success ? a.Groups["valor"].Value : "<no es una asignación legible>")))
            .ToList();

        asignaciones.Count(x => x.fichero.EndsWith("action.yml", StringComparison.Ordinal)).Should().BeGreaterThanOrEqualTo(2,
            "el test tiene que estar mirando Acquire::Retries y APT::Acquire::Retries de apt-acotado");
        asignaciones.Count(x => x.fichero == "Dockerfile").Should().BeGreaterThanOrEqualTo(2,
            "el test tiene que estar mirando el update y el install de la imagen");
        asignaciones.Should().OnlyContain(x => x.valor == "0",
            "ningún apt del CI ni de la imagen puede reintentar descargas (CVE-2026-109518)");
        lineas.Should().NotContain(x => suelto.IsMatch(x.linea),
            "un Retries fuera de Acquire::Retries (sintaxis de bloque de apt) escaparía a este test");

        var accion = LineasSinComentario(Path.Combine(raiz, ".github", "actions", "apt-acotado", "action.yml"));
        IndiceDeSecuencia(accion,
        [
            "printf '%s\\n' \\",
            "'Acquire::Retries \"0\";' \\",
            "'APT::Acquire::Retries \"0\";' \\",
        ]).Should().BeGreaterThan(-1, "apt-acotado escribe 0 literal en las dos claves, sin variable que otro camino pueda subir");
        accion.Should().Contain(consultaAptConfig,
            "la acción comprueba, después de escribir el fichero, que el valor que apt lee de verdad es 0 en las dos claves");
        accion.Should().NotContain(l => l.Contains("reintentos_segun_fuentes", StringComparison.Ordinal)
                || l.Contains("fijar_reintentos", StringComparison.Ordinal),
            "ya no hay decisión de reintentos por fuentes que mantener");
    }

    /// <summary>
    /// Codex r3 (#2168): contar las llamadas a <c>responde_200</c> no basta; con <c>||</c>
    /// en vez de <c>&amp;&amp;</c> se elegiría un espejo (o se daría por buena la base) sin
    /// que su <c>-security</c> responda. En elegir-espejo.sh y en la sonda del job de la
    /// imagen, la del pocket principal y la de security van unidas con <c>&amp;&amp;</c>
    /// en la misma condición y ninguna llamada usa <c>||</c>.
    ///
    /// <para>
    /// Lee el texto de los guiones; no ejecuta bash.
    /// </para>
    /// </summary>
    [Fact]
    public void Las_dos_sondas_de_cada_archivo_se_exigen_a_la_vez()
    {
        var raiz = RaizDelRepositorio();
        var lineasCi = File.ReadAllLines(Path.Combine(raiz, ".github", "workflows", "ci.yml"));
        var indicePasoImagen = Array.FindIndex(lineasCi, l => l.Trim() == "id: espejo-ubuntu");
        indicePasoImagen.Should().BeGreaterThan(-1, "el test tiene que encontrar el paso de la sonda de la imagen");

        var fuentes = new Dictionary<string, (List<string> lineas, string principal, string seguridad)>
        {
            ["elegir-espejo.sh"] = (
                LineasSinComentario(Path.Combine(raiz, ".github", "actions", "apt-acotado", "elegir-espejo.sh")),
                "if responde_200 \"${base}dists/${codename}/InRelease\" \\",
                "&& responde_200 \"${base}dists/${codename}-security/InRelease\"; then"),
            ["ci.yml (espejo-ubuntu)"] = (
                PasoDe(lineasCi, indicePasoImagen).Select(l => l.Trim()).Where(l => !l.StartsWith('#')).ToList(),
                "if responde_200 \"http://archive.ubuntu.com/ubuntu/dists/${codename}/InRelease\" \\",
                "&& responde_200 \"http://security.ubuntu.com/ubuntu/dists/${codename}-security/InRelease\"; then"),
        };

        foreach (var (nombre, (lineas, principal, seguridad)) in fuentes)
        {
            var llamadas = lineas.Where(l => Regex.IsMatch(l, @"\bresponde_200\s+""")).ToList();

            IndiceDeSecuencia(lineas, [principal, seguridad]).Should().BeGreaterThan(-1,
                $"{nombre}: la sonda del pocket principal y la de security van unidas con && en la misma condición");
            llamadas.Should().BeEquivalentTo([principal, seguridad],
                $"{nombre}: no hay otra sonda que decida por su cuenta");
            llamadas.Should().NotContain(l => l.Contains("||", StringComparison.Ordinal),
                $"{nombre}: con || bastaría una de las dos sondas");
        }
    }


    private static int IndiceDeSecuencia(List<string> lineas, string[] secuencia)
    {
        for (var i = 0; i + secuencia.Length <= lineas.Count; i++)
        {
            if (lineas.Skip(i).Take(secuencia.Length).SequenceEqual(secuencia, StringComparer.Ordinal))
                return i;
        }

        return -1;
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
