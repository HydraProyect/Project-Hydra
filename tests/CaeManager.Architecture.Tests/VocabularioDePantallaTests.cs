using FluentAssertions;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// <b>Vocabulario de pantalla ejecutable</b> — S1 del análisis de causas raíz de 2026-10-02
/// (<c>Project-Hydra-Negocio/tecnico/evaluacion/causas-raiz-y-salvaguardas-2026-10-02.md</c>, § 4).
///
/// <para>
/// <b>Por qué existe.</b> El contrato de terminología era prosa y el código no lo comprobaba: sacar
/// «Cliente» a secas de la interfaz costó tres PR y 239 toques de fichero (D-07 y D-27), y la decisión
/// de rótulo del 2026-10-09 lo devolvió a la pantalla como forma corta del Cliente empresarial: lo que hoy se caza
/// es el rótulo largo («Cliente empresarial»), que queda para documentación, análisis y código. La fuente
/// única es <c>Vocabulario/Vocabulario.json</c> (términos canónicos, términos prohibidos con su
/// sustitución y excepciones por contexto con su motivo); este test la aplica a los valores de todos los
/// <c>.resx</c> (neutral y satélites es/ca) y al texto visible de los <c>.razor</c>, y
/// <c>scripts/vocabulario-tabla.py</c> genera desde ella la tabla del contrato.
/// </para>
///
/// <para>
/// <b>Cómo falla.</b> Cada aparición de un término prohibido que no esté cubierta por una excepción ni
/// listada en <c>Congelados/Vocabulario-pantalla.txt</c> pone el test en rojo con fichero, clave y
/// término (<see cref="ListaCongelada"/>: nueva, crece, baja y obsoleta). La deuda actual queda congelada
/// por ubicación y solo baja.
/// </para>
///
/// <para>
/// <b>Lo que ve:</b> los <c>.resx</c> (es y ca), el texto de marcado de los <c>.razor</c> y los mensajes de
/// <c>Error.Crear</c> y <c>.WithMessage</c> de Application. <b>Lo que NO ve, declarado:</b> texto que llega de
/// datos, otros mensajes de Application o Domain (<c>Result.Fallo</c> con literal suelto, excepciones), literales
/// <c>.cs</c> de Web e Infrastructure, texto montado en JavaScript o un atributo con expresión Razor. Los
/// trinquetes de identificadores (<c>Bandeja</c>, <c>ClienteId</c>…) siguen vigilando el código; este mide lo que
/// el usuario lee. Detalle en <see cref="VocabularioDePantalla"/>.
/// </para>
/// </summary>
public class VocabularioDePantallaTests
{
    private static readonly Lazy<VocabularioJson> Vocabulario = new(VocabularioDePantalla.Cargar);

    private static readonly Lazy<List<(string Fichero, string? Clave, string Texto)>> Resx =
        new(() => VocabularioDePantalla.TextosDeResx().ToList());

    private static readonly Lazy<List<(string Fichero, string? Clave, string Texto)>> Razor =
        new(() => VocabularioDePantalla.TextosDeRazor().ToList());

    private static readonly Lazy<List<(string Fichero, string? Clave, string Texto)>> Mensajes =
        new(() => VocabularioDePantalla.TextosDeMensajes().ToList());

    private static List<HallazgoDeVocabulario> HallazgosReales() =>
        VocabularioDePantalla.Escanear(Vocabulario.Value, Resx.Value.Concat(Razor.Value).Concat(Mensajes.Value));

    // ───────────────────────── el trinquete real ─────────────────────────

    [Fact]
    public void El_texto_visible_solo_dice_los_terminos_prohibidos_donde_la_lista_lo_congela()
    {
        var fallo = ListaCongelada.Verificar("Vocabulario-pantalla",
            VocabularioDePantalla.DeudaMedida(HallazgosReales()), GuiaDeCorreccion(Vocabulario.Value));

        fallo.Should().BeNull();
    }

    [Fact]
    public void Toda_excepcion_cubre_al_menos_una_aparicion_real_de_cada_termino_que_nombra()
    {
        var cubiertas = HallazgosReales().Where(h => h.IdExcepcion is not null)
            .Select(h => (Excepcion: h.IdExcepcion!, Termino: h.IdProhibido)).ToHashSet();

        var sobrantes = Vocabulario.Value.Excepciones
            .SelectMany(e => e.Prohibidos.Select(t => (Excepcion: e.Id, Termino: t)))
            .Where(par => !cubiertas.Contains(par))
            .Select(par => $"{par.Excepcion} -> {par.Termino}")
            .ToList();

        sobrantes.Should().BeEmpty(
            "una excepción que nombra un término sin ninguna aparición real que lo justifique es una puerta abierta: " +
            "el motivo de la excepción solo vale para lo que de verdad contiene (quita el término de su lista)");
    }

    // ───────────────────────── controles positivos del instrumento ─────────────────────────

    [Fact]
    public void Los_escaneres_ven_miles_de_valores_y_cientos_de_textos_de_marcado()
    {
        // «Vacío ≠ negativo»: un escáner que se queda ciego daría verde con cero hallazgos.
        Resx.Value.Count.Should().BeGreaterThan(3000, "el escáner de .resx debe ver los valores de es-ES y ca-ES");
        Resx.Value.Select(t => t.Fichero).Distinct().Should().Contain(f => f.EndsWith(".ca-ES.resx", StringComparison.Ordinal),
            "los satélites ca-ES cuentan: son lo que lee un usuario con la interfaz en catalán");
        Razor.Value.Count.Should().BeGreaterThan(500, "el escáner de .razor debe ver el texto de marcado escrito a mano");
        Mensajes.Value.Count.Should().BeGreaterThan(500, "el escáner de mensajes debe ver los de Error.Crear y WithMessage de src");
        Mensajes.Value.Select(t => t.Clave).Should().Contain(c => c != null && c.Contains('.'), "la clave de un mensaje de Application es el código del error");
    }

    [Fact]
    public void Cada_termino_prohibido_pasa_sus_propios_ejemplos()
    {
        var descartes = VocabularioDePantalla.Descartes(Vocabulario.Value);
        foreach (var p in Vocabulario.Value.Prohibidos)
        {
            var regex = VocabularioDePantalla.Compilar(p);
            p.Casa.Should().NotBeNullOrEmpty($"«{p.Id}» necesita al menos un ejemplo que deba cazar (control positivo)");
            p.NoCasa.Should().NotBeNullOrEmpty($"«{p.Id}» necesita al menos un ejemplo legítimo que no deba cazar");

            foreach (var ejemplo in p.Casa!)
                regex.IsMatch(VocabularioDePantalla.Descartar(descartes, ejemplo)).Should().BeTrue($"«{p.Id}» debe cazar «{ejemplo}»");
            foreach (var ejemplo in p.NoCasa!)
                regex.IsMatch(VocabularioDePantalla.Descartar(descartes, ejemplo)).Should().BeFalse($"«{p.Id}» no debe cazar «{ejemplo}»");
        }
    }

    [Fact]
    public void Ninguna_forma_canonica_cae_en_un_termino_prohibido()
    {
        // Dos secciones del mismo fichero no pueden contradecirse: lo que el vocabulario manda decir
        // no puede ser, a la vez, lo que prohíbe.
        var reglas = Vocabulario.Value.Prohibidos.Select(p => (p.Id, Regex: VocabularioDePantalla.Compilar(p))).ToList();
        var formas = Vocabulario.Value.Canonicos.SelectMany(c =>
            new[] { c.Forma, c.Plural }.Concat(c.FormasCortasPermitidas ?? []).Where(f => !string.IsNullOrWhiteSpace(f))
                .Select(f => (c.Id, Forma: f!)));

        foreach (var (id, forma) in formas)
        {
            reglas.Where(r => r.Regex.IsMatch(forma)).Select(r => r.Id)
                .Should().BeEmpty($"la forma canónica «{forma}» ({id}) no puede estar prohibida");
        }
    }

    [Fact]
    public void El_vocabulario_esta_completo_y_sin_ids_repetidos()
    {
        var v = Vocabulario.Value;
        v.Version.Should().Be(1);

        foreach (var ids in new[] { v.Canonicos.Select(c => c.Id), v.Prohibidos.Select(p => p.Id), v.Excepciones.Select(e => e.Id) })
            ids.GroupBy(i => i).Where(g => g.Count() > 1).Select(g => g.Key).Should().BeEmpty("los ids son únicos");

        v.Canonicos.Should().OnlyContain(c => !string.IsNullOrWhiteSpace(c.Forma) && !string.IsNullOrWhiteSpace(c.Significado)
            && !string.IsNullOrWhiteSpace(c.Contrato));
        v.Prohibidos.Should().OnlyContain(p => !string.IsNullOrWhiteSpace(p.Sustitucion) && !string.IsNullOrWhiteSpace(p.Motivo)
            && !string.IsNullOrWhiteSpace(p.Patron) && !string.IsNullOrWhiteSpace(p.Contrato));

        var idsProhibidos = v.Prohibidos.Select(p => p.Id).ToHashSet();
        foreach (var e in v.Excepciones)
        {
            e.Motivo.Should().NotBeNullOrWhiteSpace($"la excepción «{e.Id}» necesita su motivo: sin motivo no hay excepción");
            e.Contexto.Should().NotBeNullOrWhiteSpace($"la excepción «{e.Id}» necesita su contexto");
            e.Ficheros.Should().NotBeEmpty($"la excepción «{e.Id}» necesita al menos un fichero");
            e.Prohibidos.Should().NotBeEmpty().And.OnlyContain(id => idsProhibidos.Contains(id),
                $"la excepción «{e.Id}» solo puede nombrar términos prohibidos que existan");
        }
    }

    // ───────────────────────── mutaciones, con fuentes sintéticas ─────────────────────────

    [Fact]
    public void Un_resx_con_Cliente_empresarial_da_rojo_con_fichero_clave_y_termino()
    {
        // Decisión de rótulo del 2026-10-09: la pantalla dice «Cliente»; el rótulo largo es lo que se caza.
        const string ruta = "src/CaeManager.Web/Features/Falsa/Recursos/TextosFalsa.resx";
        var textos = VocabularioDePantalla.TextosDeResx(ruta, Resx_(
            ("EtiquetaCliente", "Cliente empresarial"), ("EtiquetaBuena", "Cliente"), ("EtiquetaRol", "Usuario de Cliente")));

        var deuda = VocabularioDePantalla.DeudaMedida(VocabularioDePantalla.Escanear(Vocabulario.Value, textos));

        deuda.Should().ContainSingle().Which.Key.Should().Be(new Ubicacion(ruta, "EtiquetaCliente [cliente-empresarial-en-pantalla]"));

        // Y el veredicto de verdad: contra la lista congelada real, la línea nueva es un desvío con ese texto.
        var desvios = ListaCongelada.Desvios(deuda, new Dictionary<Ubicacion, int>());
        desvios.Should().ContainSingle().Which.Should().Contain("TextosFalsa.resx").And.Contain("EtiquetaCliente").And.Contain("cliente-empresarial-en-pantalla");
    }

    [Fact]
    public void Un_razor_con_Bandeja_del_gestor_da_rojo_pero_la_bandeja_de_entrada_del_correo_no()
    {
        const string ruta = "src/CaeManager.Web/Features/Falsa/Pages/Falsa.razor";

        var mala = VocabularioDePantalla.TextosDeRazor(ruta, "<h1>Bandeja del gestor</h1>");
        var hallazgos = VocabularioDePantalla.Escanear(Vocabulario.Value, mala);

        hallazgos.Select(h => h.IdProhibido).Should().BeEquivalentTo(["bandeja-como-mi-trabajo", "gestor-a-secas"]);
        hallazgos.Should().OnlyContain(h => h.Fichero == ruta && h.Clave == null);

        var buena = VocabularioDePantalla.TextosDeRazor(ruta, "<h1>Bandeja de entrada</h1><p>Mi trabajo</p>");
        VocabularioDePantalla.Escanear(Vocabulario.Value, buena).Should().BeEmpty(
            "«bandeja de entrada» es el sentido legítimo (el buzón de correo) y «Mi trabajo» es el canónico");
    }

    [Fact]
    public void Una_excepcion_legitima_no_da_rojo_y_la_misma_frase_fuera_de_su_contexto_si()
    {
        // Una excepción real acotada por clave, con un fichero y una clave concretos tomados de su propia definición
        // (el comodín «TextosImportacion*.resx» casa con la ausencia de «*»): el test no depende de qué frase
        // concreta contenga hoy el recurso.
        var excepcion = Vocabulario.Value.Excepciones.First(e => e.Claves is { Count: > 0 });
        var termino = excepcion.Prohibidos[0];
        var frase = Vocabulario.Value.Prohibidos.Single(p => p.Id == termino).Casa![0];
        var ficheroLiteral = excepcion.Ficheros[0].Replace("*", string.Empty);
        var clave = excepcion.Claves![0];
        VocabularioDePantalla.ComodinARegex(excepcion.Ficheros[0]).IsMatch(ficheroLiteral).Should().BeTrue();

        // Dentro del contexto: la excepción la cubre y no hay deuda.
        var dentro = VocabularioDePantalla.Escanear(Vocabulario.Value, [(ficheroLiteral, (string?)clave, frase)])
            .Where(h => h.IdProhibido == termino).ToList();
        dentro.Should().ContainSingle().Which.IdExcepcion.Should().Be(excepcion.Id);
        VocabularioDePantalla.DeudaMedida(dentro).Should().BeEmpty();

        // Otra clave del mismo fichero: no es el contexto de la excepción.
        var otraClave = VocabularioDePantalla.Escanear(Vocabulario.Value, [(ficheroLiteral, (string?)"OtraClave", frase)])
            .Where(h => h.IdProhibido == termino);
        VocabularioDePantalla.DeudaMedida(otraClave).Should().ContainSingle();

        // Otro fichero con la misma clave: tampoco.
        var otroFichero = VocabularioDePantalla.Escanear(Vocabulario.Value,
            [("src/CaeManager.Web/Features/Falsa/Recursos/TextosFalsa.resx", (string?)clave, frase)])
            .Where(h => h.IdProhibido == termino);
        VocabularioDePantalla.DeudaMedida(otroFichero).Should().ContainSingle();
    }

    [Fact]
    public void Un_mensaje_de_Application_con_Cliente_empresarial_da_rojo_con_fichero_y_codigo_de_error()
    {
        const string ruta = "src/CaeManager.Application/Falsa/Commands/FalsoCommand.cs";
        const string codigo = @"
            class C {
                void M() {
                    var a = Error.Crear(""Falso.NoEncontrado"", ""No encontramos ese Cliente empresarial."");
                    var b = Error.Crear(""Falso.Bien"", ""No encontramos ese Cliente."");
                    var c = Error.Crear(""Falso.Interpolado"", $""El tenant {x} no existe"");
                    RuleFor(x => x.A).WithMessage(""Falta el gestor."");
                    var d = Otra.Crear(""Falso.Ajeno"", ""Cliente empresarial en otra fábrica no cuenta"");
                    var e = Error.Crear(""Falso.Concat"", ""No encontramos ese Cliente "" + ""empresarial."");
                    var f = Domain.Common.Error.Crear(""Falso.Cualificado"", ""El operador cualificado no existe"");
                }
            }";

        var hallazgos = VocabularioDePantalla.Escanear(Vocabulario.Value, VocabularioDePantalla.TextosDeMensajes(ruta, codigo));

        hallazgos.Select(h => (h.Clave, h.IdProhibido)).Should().BeEquivalentTo(new[]
        {
            ("Falso.NoEncontrado", "cliente-empresarial-en-pantalla"),
            ("Falso.Interpolado", "tenant-a-secas"),
            ("WithMessage", "gestor-a-secas"),
            // Los trozos de una concatenación se unen antes de casar: el rótulo largo partido en dos literales también se ve.
            ("Falso.Concat", "cliente-empresarial-en-pantalla"),
            ("Falso.Cualificado", "operador-a-secas"),
        });
        hallazgos.Should().OnlyContain(h => h.Fichero == ruta);
    }

    [Fact]
    public void Un_atributo_con_texto_de_nombre_no_listado_se_ve_y_una_URL_de_ejemplo_se_descarta()
    {
        const string ruta = "src/CaeManager.Web/Features/Falsa/Pages/Falsa.razor";
        const string marcado =
            "<Buscador PlaceholderBuscador=\"Buscar por Cliente empresarial\" EtiquetaCampoNuevo=\"Operador\" />" +
            "<input placeholder=\"https://portal-del-operador.com/login\" />" +
            "<section aria-labelledby=\"columnas-del-operador-titulo\"><p>datos del cliente empresarial</p></section>";

        var hallazgos = VocabularioDePantalla.Escanear(Vocabulario.Value, VocabularioDePantalla.TextosDeRazor(ruta, marcado));

        hallazgos.Select(h => h.IdProhibido).Should().BeEquivalentTo(new[] { "cliente-empresarial-en-pantalla", "operador-a-secas", "cliente-empresarial-en-pantalla" },
            "el atributo PlaceholderBuscador no está en la lista del detector y se ve igualmente; la URL no cuenta (un solo «operador», el del atributo); el texto entre etiquetas sí; aria-labelledby es una referencia a un id y no cuenta");
    }

    [Fact]
    public void Quitar_la_excepcion_pone_la_aparicion_en_la_deuda()
    {
        var sinExcepciones = Vocabulario.Value with { Excepciones = [] };

        var hallazgosConExcepcion = HallazgosReales().Where(h => h.IdExcepcion is not null).ToList();
        hallazgosConExcepcion.Should().NotBeEmpty("este test solo mide algo si hay excepciones vivas");

        var textos = hallazgosConExcepcion.Select(h => (h.Fichero, h.Clave, Texto: h.Coincidencia)).Distinct().ToList();
        var sin = VocabularioDePantalla.Escanear(sinExcepciones, textos);

        sin.Should().NotBeEmpty().And.OnlyContain(h => h.IdExcepcion == null);
        VocabularioDePantalla.DeudaMedida(sin).Should().NotBeEmpty("sin la excepción, esas apariciones son deuda nueva y el trinquete se pone en rojo");
    }

    [Fact]
    public void Los_comodines_de_fichero_no_cruzan_carpetas_salvo_con_doble_asterisco()
    {
        VocabularioDePantalla.ComodinARegex("src/A/*.resx").IsMatch("src/A/X.resx").Should().BeTrue();
        VocabularioDePantalla.ComodinARegex("src/A/*.resx").IsMatch("src/A/B/X.resx").Should().BeFalse();
        VocabularioDePantalla.ComodinARegex("src/**/X.resx").IsMatch("src/A/B/X.resx").Should().BeTrue();
        VocabularioDePantalla.ComodinARegex("src/A/X.resx").IsMatch("src/A/XAresx").Should().BeFalse("el punto es literal");
    }

    // ───────────────────────── apoyo ─────────────────────────

    private static string Resx_(params (string Clave, string Valor)[] datos) =>
        "<root>" + string.Concat(datos.Select(d => $"<data name=\"{d.Clave}\"><value>{d.Valor}</value></data>")) + "</root>";

    private static string GuiaDeCorreccion(VocabularioJson v) =>
        "Este texto de pantalla dice un término que el vocabulario prohíbe a secas (Vocabulario/Vocabulario.json). " +
        "Cámbialo por su sustitución, o, si es un contexto legítimo (plantilla de importación, texto legal, acceso de plataforma, " +
        "nombre propio), añade una EXCEPCIÓN con su motivo en Vocabulario.json; añadir una línea a la lista solo es para deuda ya existente. " +
        "Sustituciones:\n  " + string.Join("\n  ", v.Prohibidos.Select(p => $"[{p.Id}] «{p.Termino}» → {p.Sustitucion}"));
}
