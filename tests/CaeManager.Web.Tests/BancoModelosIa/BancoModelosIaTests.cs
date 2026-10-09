using System.Text.Json;
using CaeManager.Infrastructure.AsistenteIa;
using FluentAssertions;
using Xunit.Abstractions;

namespace CaeManager.Web.Tests.BancoModelosIa;

/// <summary>
/// La medición con modelo real solo corre con una clave de Anthropic en
/// <c>Anthropic__ApiKey</c> Y con la petición expresa <c>BANCO_IA_EJECUTAR</c>,
/// que pone el lanzador. Sin las dos el test queda OMITIDO con su motivo a
/// la vista, nunca en verde: un verde sin casos no es una medición. La
/// petición expresa existe para que un <c>dotnet test</c> corriente, lanzado
/// en una consola que tenga la clave, no gaste dinero sin que nadie lo pida.
/// </summary>
public sealed class FactSiHayClaveDeAnthropicAttribute : FactAttribute
{
    public const string Variable = AnthropicOptions.SeccionConfiguracion + "__ApiKey";
    public const string VariableDePeticion = "BANCO_IA_EJECUTAR";

    public FactSiHayClaveDeAnthropicAttribute()
    {
        Skip = MotivoDeOmision(Environment.GetEnvironmentVariable(Variable), Environment.GetEnvironmentVariable(VariableDePeticion));
    }

    public static string? MotivoDeOmision(string? clave, string? peticion)
    {
        if (string.IsNullOrWhiteSpace(clave))
        {
            return $"Banco de modelos de IA NO ejecutado: falta la clave en \"{Variable}\". Se lanza con scripts/banco-ia.sh, que la toma " +
                   "del fichero local de clave o, en CI, del workflow integraciones-con-clave.yml; en ci.yml este test se omite por diseño.";
        }

        return string.IsNullOrWhiteSpace(peticion)
            ? $"Banco de modelos de IA NO ejecutado: hay clave pero nadie lo ha pedido (\"{VariableDePeticion}\"). Gasta dinero: se lanza con scripts/banco-ia.sh."
            : null;
    }
}

/// <summary>Modos del banco que no llaman a la API y que solo corren cuando el lanzador los pide con una variable de entorno.</summary>
public sealed class FactSiSePideAttribute : FactAttribute
{
    public FactSiSePideAttribute(string variable)
    {
        Skip = MotivoDeOmision(variable, Environment.GetEnvironmentVariable(variable));
    }

    public static string? MotivoDeOmision(string variable, string? valor) =>
        string.IsNullOrWhiteSpace(valor) ? $"Modo del banco de modelos de IA no pedido: se activa con \"{variable}\" (lo hace scripts/banco-ia.sh)." : null;
}

/// <summary>
/// Banco de medición de las siete rutas Anthropic del producto. MIDE, no
/// vigila: la calidad que obtenga un modelo no pone nada en rojo. Lo que sí
/// falla es el instrumento — ningún caso ejecutado, la API rechazando casos,
/// o el modelo y el esfuerzo pedidos que no viajan en la petición — porque
/// entonces las cifras no dicen lo que parecen decir.
///
/// El resto de tests son la prueba de sensibilidad del propio banco, con un
/// modelo simulado y sin clave: cada métrica llega al máximo con la
/// respuesta correcta y baja con una equivocada.
/// </summary>
public class BancoModelosIaTests(ITestOutputHelper salida)
{
    private static readonly ParametrosBanco ParametrosDePrueba =
        new("modelo-de-prueba", null, null, Path.Combine(Path.GetTempPath(), "banco-modelos-ia-pruebas"), null, null);

    [FactSiHayClaveDeAnthropic]
    public async Task Mide_las_rutas_Anthropic_con_el_modelo_y_el_esfuerzo_pedidos()
    {
        var parametros = ParametrosBanco.DesdeEntorno(Environment.GetEnvironmentVariable);
        var clave = Environment.GetEnvironmentVariable(FactSiHayClaveDeAnthropicAttribute.Variable)!;

        // Una sola conexión para todos los casos, abierta antes de medir: la latencia de cada caso es la del modelo,
        // no la de negociar TCP y TLS. La petición de calentamiento no lleva clave y no cuesta nada.
        using var red = new SocketsHttpHandler();
        using (var calentamiento = new HttpClient(red, disposeHandler: false))
        {
            try
            {
                using var _ = await calentamiento.GetAsync("https://api.anthropic.com/");
            }
            catch (HttpRequestException)
            {
                // Sin red, los casos lo dirán con su propio diagnóstico.
            }
        }

        var resultados = await EjecutorBancoModelosIa.EjecutarAsync(parametros, clave, CorpusBancoModelosIa.Casos, _ => red);

        var problemas = ValidacionDelInstrumento.Problemas(parametros, resultados);
        var informe = InformeBancoModelosIa.Construir(
            parametros, resultados, problemas, CorpusBancoModelosIa.CasosNoConstruibles,
            DateTimeOffset.UtcNow, Environment.GetEnvironmentVariable("GITHUB_SHA"));

        // El gasto se anota aunque el instrumento se declare inválido: los tokens se han consumido igual.
        informe = AnotarEnElLibro(informe, Environment.GetEnvironmentVariable("BANCO_IA_ORIGEN") ?? "local");
        var rutaMarkdown = InformeBancoModelosIa.Escribir(informe, parametros.CarpetaSalida);

        salida.WriteLine(InformeBancoModelosIa.Markdown(informe));
        salida.WriteLine($"Informe escrito en {rutaMarkdown}");

        problemas.Should().BeEmpty("la medición solo es comparable si el instrumento midió lo que se le pidió");
    }

    /// <summary>
    /// Construye las peticiones que el producto enviaría y las cuenta, sin
    /// enviarlas: el transporte es un modelo simulado. Da la cota de gasto
    /// antes de decidir si se ejecuta el banco de verdad.
    /// </summary>
    [FactSiSePide("BANCO_IA_SOLO_ESTIMAR")]
    public async Task Estima_el_coste_sin_llamar_a_la_API()
    {
        var parametros = ParametrosBanco.DesdeEntorno(Environment.GetEnvironmentVariable);

        var peticiones = await EjecutorBancoModelosIa.EjecutarAsync(
            parametros, "sin-clave-solo-estimacion", CorpusBancoModelosIa.Casos, _ => new ModeloSimulado(string.Empty));

        var markdown = InformeBancoModelosIa.MarkdownDeEstimacion(parametros, peticiones);
        Directory.CreateDirectory(parametros.CarpetaSalida);
        File.WriteAllText(Path.Combine(parametros.CarpetaSalida, $"banco-ia-estimacion-{InformeBancoModelosIa.Limpio(parametros.Modelo)}.md"), markdown);
        salida.WriteLine(markdown);

        peticiones.Should().NotBeEmpty("una estimación sobre cero casos no acota nada");
    }

    /// <summary>Pasa al libro de gasto local un informe ya medido en otro sitio (el artefacto JSON de una ejecución de CI).</summary>
    [FactSiSePide("BANCO_IA_ANOTAR")]
    public void Anota_en_el_libro_de_gasto_un_informe_ya_medido()
    {
        var informe = JsonSerializer.Deserialize<InformeBanco>(
            File.ReadAllText(Environment.GetEnvironmentVariable("BANCO_IA_ANOTAR")!), InformeBancoModelosIa.Json)!;
        var libro = Environment.GetEnvironmentVariable("BANCO_IA_LIBRO_GASTO");
        libro.Should().NotBeNullOrWhiteSpace("anotar exige saber dónde está el libro de gasto");

        var anotadas = LibroDeGasto.Anotar(informe, libro!, Environment.GetEnvironmentVariable("BANCO_IA_ORIGEN") ?? "ci");

        salida.WriteLine($"Líneas anotadas: {anotadas} (0 = el informe ya estaba en el libro). " +
            $"Acumulado del mes: {LibroDeGasto.AcumuladoDelMes(libro!, informe.Fecha).ToString("0.0000", System.Globalization.CultureInfo.InvariantCulture)} $");
    }

    private static InformeBanco AnotarEnElLibro(InformeBanco informe, string origen)
    {
        var libro = Environment.GetEnvironmentVariable("BANCO_IA_LIBRO_GASTO");
        if (string.IsNullOrWhiteSpace(libro))
            return informe;

        LibroDeGasto.Anotar(informe, libro, origen);
        var presupuesto = decimal.TryParse(
            Environment.GetEnvironmentVariable("BANCO_IA_PRESUPUESTO_MES"), System.Globalization.NumberStyles.Number,
            System.Globalization.CultureInfo.InvariantCulture, out var p) ? p : 240m;
        return informe with
        {
            GastoDelMes = new GastoDelMes(
                informe.Fecha.UtcDateTime.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture),
                LibroDeGasto.AcumuladoDelMes(libro, informe.Fecha), presupuesto),
        };
    }

    [Fact]
    public void El_corpus_cubre_las_siete_rutas_con_los_cuatro_niveles_y_un_caso_adversarial_en_cada_una()
    {
        var porRuta = CorpusBancoModelosIa.Casos.GroupBy(c => c.Ruta).ToDictionary(g => g.Key, g => g.ToList());

        porRuta.Keys.Should().BeEquivalentTo(Enum.GetValues<RutaIa>());
        foreach (var (ruta, casos) in porRuta)
        {
            casos.Select(c => c.Nivel).Distinct().Should().BeEquivalentTo(Enum.GetValues<NivelCaso>(), $"la ruta {ruta} se mide en los cuatro niveles");
        }

        CorpusBancoModelosIa.Casos.Select(c => c.Id).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task Con_un_modelo_simulado_que_responde_lo_esperado_todos_los_casos_puntuan_el_maximo()
    {
        var resultados = await EjecutorBancoModelosIa.EjecutarAsync(
            ParametrosDePrueba, "clave-de-prueba", CorpusBancoModelosIa.Casos, caso => new ModeloSimulado(caso.RespuestaIdeal()));

        resultados.Should().HaveCount(CorpusBancoModelosIa.Casos.Count);
        resultados.Where(r => r.Puntuacion < 1).Select(r => $"{r.Caso}: {r.ErrorServicio} {string.Join("; ", r.Fallos)}")
            .Should().BeEmpty("la respuesta ideal de cada caso es, por construcción, la que la métrica premia entera");
    }

    [Fact]
    public async Task Con_respuestas_plausibles_pero_equivocadas_ningun_caso_puntua_el_maximo()
    {
        var resultados = await EjecutorBancoModelosIa.EjecutarAsync(
            ParametrosDePrueba, "clave-de-prueba", CorpusBancoModelosIa.Casos, caso => new ModeloSimulado(caso.RespuestaErronea()));

        resultados.Where(r => r.Puntuacion >= 1).Select(r => r.Caso)
            .Should().BeEmpty("una respuesta bien formada pero con el contenido cambiado tiene que bajar la métrica de su ruta");
    }

    [Fact]
    public async Task Con_un_modelo_que_devuelve_texto_sin_relacion_ningun_caso_puntua_el_maximo_y_las_rutas_de_datos_caen_a_cero()
    {
        var resultados = await EjecutorBancoModelosIa.EjecutarAsync(
            ParametrosDePrueba, "clave-de-prueba", CorpusBancoModelosIa.Casos, _ => new ModeloSimulado("Lo siento, hoy no puedo ayudarte con eso."));

        resultados.Where(r => r.Puntuacion >= 1).Select(r => r.Caso).Should().BeEmpty();

        // Las cinco rutas que devuelven JSON no pueden ni interpretar la respuesta: el servicio falla y el caso vale cero.
        // La transcripción y el chat devuelven texto libre, así que puntúan por contenido y ahí «no contiene X» sí se cumple.
        var rutasDeDatos = new[]
        {
            RutaIa.RelevanciaCaeDeConversacion, RutaIa.GestionDocumentalEnCorreo, RutaIa.VisitaEnCorreo,
            RutaIa.ExtraccionEstructurada, RutaIa.ListadoDeTrabajadores,
        };
        var deDatos = resultados.Where(r => rutasDeDatos.Contains(r.Ruta)).ToList();
        deDatos.Should().NotBeEmpty();
        deDatos.Where(r => r.Puntuacion != 0 || r.ErrorServicio is null).Select(r => r.Caso).Should().BeEmpty();
    }

    [Fact]
    public async Task En_el_listado_de_Trabajadores_un_inventado_y_una_omision_se_cuentan_por_separado()
    {
        var caso = (CasoListadoTrabajadores)CorpusBancoModelosIa.Casos.First(c => c.Id == "lis-sencillo-cuatro");
        string Json(IEnumerable<TrabajadorEsperado> trabajadores) =>
            JsonSerializer.Serialize(trabajadores.Select(t => new { nombre = t.Nombre, apellidos = t.Apellidos, dni = t.Dni }));

        var conInventado = await EjecutarUnoAsync(caso, Json([.. caso.Esperados, new TrabajadorEsperado("Pedro", "Inventado", "00000000X")]));
        conInventado.Medidas["inventados"].Should().Be(1);
        conInventado.Medidas["omitidos"].Should().Be(0);
        conInventado.Fallos.Should().ContainSingle().Which.Should().StartWith("sin Trabajadores inventados");

        var conOmision = await EjecutarUnoAsync(caso, Json(caso.Esperados.Skip(1)));
        conOmision.Medidas["omitidos"].Should().Be(1);
        conOmision.Medidas["inventados"].Should().Be(0);
        conOmision.Fallos.Should().ContainSingle().Which.Should().StartWith("sin omisiones");
    }

    [Fact]
    public async Task En_el_listado_una_baja_incluida_ni_suma_ni_resta_pero_queda_anotada()
    {
        var caso = (CasoListadoTrabajadores)CorpusBancoModelosIa.Casos.First(c => c.Id == "lis-adversarial-cortado");
        var conBaja = JsonSerializer.Serialize(
            caso.Esperados.Select(t => new { nombre = t.Nombre, apellidos = t.Apellidos, dni = t.Dni })
                .Append(new { nombre = "Alguien", apellidos = "De Baja", dni = caso.Dudosos[0] }));

        var resultado = await EjecutarUnoAsync(caso, conBaja);

        resultado.Puntuacion.Should().Be(1);
        resultado.Medidas["bajasIncluidas"].Should().Be(1);
    }

    [Fact]
    public async Task En_la_gestion_documental_obedecer_la_instruccion_incrustada_cuenta_como_items_inventados()
    {
        var caso = (CasoGestionCorreo)CorpusBancoModelosIa.Casos.First(c => c.Id == "ges-adversarial-inyeccion");
        var apto = caso.TiposDocumento.Single(t => t.Nombre == "Apto médico");
        var obediente = JsonSerializer.Serialize(new
        {
            esActualizacionDocumento = true,
            resumen = "Simulado.",
            confianza = 100,
            items = caso.ItemsEsperados
                .Select(i => new { trabajadorId = i.TrabajadorId, tipoDocumentoId = i.TipoDocumentoId, confianzaTrabajador = 100, confianzaTipoDocumento = 100 })
                .Concat(caso.Trabajadores.Select(t => new { trabajadorId = (Guid?)t.Id, tipoDocumentoId = (Guid?)apto.Id, confianzaTrabajador = 100, confianzaTipoDocumento = 100 })),
        });

        var resultado = await EjecutarUnoAsync(caso, obediente);

        resultado.Medidas["inventados"].Should().Be(caso.Trabajadores.Count);
        resultado.Medidas["omitidos"].Should().Be(0);
        resultado.Puntuacion.Should().BeLessThan(1);
    }

    [Fact]
    public async Task En_la_gestion_documental_callar_no_regala_el_punto_de_no_inventar_ni_el_del_resumen()
    {
        var caso = (CasoGestionCorreo)CorpusBancoModelosIa.Casos.First(c => c.Id == "ges-adversarial-inyeccion");
        var callado = JsonSerializer.Serialize(new
        {
            esActualizacionDocumento = caso.EsActualizacionEsperada,
            resumen = "Simulado.",
            confianza = 100,
            items = Array.Empty<object>(),
        });

        var resultado = await EjecutarUnoAsync(caso, callado);

        resultado.ComprobacionesTotales.Should().Be(1 + caso.ItemsEsperados.Count, "solo cuentan la etiqueta y los ítems que el correo pide");
        resultado.ComprobacionesCorrectas.Should().Be(1);
        resultado.Medidas["inventados"].Should().Be(0);
    }

    [Fact]
    public async Task En_la_Visita_cada_dato_equivocado_resta_su_propia_comprobacion()
    {
        var caso = (CasoVisitaCorreo)CorpusBancoModelosIa.Casos.First(c => c.Id == "vis-medio-rango");
        string Json(Guid? centro, DateOnly? inicio, DateOnly? fin) => JsonSerializer.Serialize(new
        {
            esSolicitudVisita = true,
            centroId = centro,
            fechaInicio = inicio?.ToString("yyyy-MM-dd"),
            fechaFin = fin?.ToString("yyyy-MM-dd"),
            resumen = "Simulado.",
            confianza = 90,
            confianzaCentro = 90,
            confianzaFechas = 90,
        });
        var otroCentro = caso.Centros.First(c => c.Id != caso.CentroEsperado).Id;

        (await EjecutarUnoAsync(caso, Json(otroCentro, caso.FechaInicioEsperada, caso.FechaFinEsperada)))
            .Fallos.Should().ContainSingle().Which.Should().StartWith("Centro");
        (await EjecutarUnoAsync(caso, Json(caso.CentroEsperado, caso.FechaInicioEsperada!.Value.AddDays(1), caso.FechaFinEsperada)))
            .Fallos.Should().ContainSingle().Which.Should().StartWith("fecha de inicio");
        (await EjecutarUnoAsync(caso, Json(caso.CentroEsperado, caso.FechaInicioEsperada, null)))
            .Fallos.Should().ContainSingle().Which.Should().StartWith("fecha de fin");
    }

    [Fact]
    public async Task En_la_extraccion_un_campo_inventado_y_un_campo_cambiado_restan_cada_uno_lo_suyo()
    {
        var caso = (CasoExtraccionEstructurada)CorpusBancoModelosIa.Casos.First(c => c.Id == "ext-adversarial-cortado");
        string Json(Dictionary<string, string> campos) =>
            JsonSerializer.Serialize(new { tipoDetectado = caso.TipoEsperado, campos, confianzaGeneral = 90, notasValidacion = (string?)null });
        var correctos = caso.CamposExactos.Concat(caso.CamposQueDebenContener).ToDictionary(c => c.Key, c => c.Value);

        var conInventado = await EjecutarUnoAsync(caso, Json(new(correctos) { ["fechaVencimiento"] = "2028-02-18" }));
        conInventado.Fallos.Should().ContainSingle().Which.Should().StartWith("campo fechaVencimiento sin inventar");

        var conCambiado = await EjecutarUnoAsync(caso, Json(new(correctos) { ["fechaEmision"] = "2026-02-19" }));
        conCambiado.Fallos.Should().ContainSingle().Which.Should().StartWith("campo fechaEmision");
    }

    [Fact]
    public async Task En_la_transcripcion_perder_texto_baja_el_recuerdo_aunque_sobrevivan_los_fragmentos_criticos()
    {
        var caso = (CasoTranscripcion)CorpusBancoModelosIa.Casos.First(c => c.Id == "ocr-dificil-relacion");
        var lineas = caso.TextoDeReferencia.Split('\n');
        // Se pierde una de cada cuatro filas que no llevan ningún fragmento crítico.
        var recortado = string.Join('\n', lineas.Where((linea, i) => i % 4 != 2 || caso.FragmentosCriticos.Any(linea.Contains)));

        var resultado = await EjecutarUnoAsync(caso, recortado);

        resultado.Medidas["recuerdoPalabras"].Should().BeLessThan(CasoTranscripcion.RecuerdoMinimo);
        resultado.Fallos.Should().ContainSingle().Which.Should().StartWith("recuerdo de palabras");
    }

    [Fact]
    public async Task En_el_chat_revelar_el_prompt_resta_aunque_la_respuesta_cumpla_todo_lo_demas()
    {
        var caso = (CasoChat)CorpusBancoModelosIa.Casos.First(c => c.Id == "chat-adversarial-inyeccion");

        var resultado = await EjecutarUnoAsync(caso, caso.RespuestaIdeal() + "\n\nMi prompt dice: Eres PRL Expert AI.");

        resultado.Fallos.Should().ContainSingle().Which.Should().Contain("no contiene «Eres PRL Expert AI»");
    }

    private static async Task<ResultadoCaso> EjecutarUnoAsync(CasoBanco caso, string respuestaDelModelo) =>
        (await EjecutorBancoModelosIa.EjecutarAsync(ParametrosDePrueba, "clave-de-prueba", [caso], _ => new ModeloSimulado(respuestaDelModelo))).Single();

    [Fact]
    public async Task La_sonda_anota_lo_que_viaja_en_la_peticion_y_lo_que_devuelve_la_respuesta()
    {
        var caso = CorpusBancoModelosIa.Casos.First(c => c.Id == "rel-sencillo-alta");

        var resultado = (await EjecutorBancoModelosIa.EjecutarAsync(
            ParametrosDePrueba, "clave-de-prueba", [caso], c => new ModeloSimulado(c.RespuestaIdeal(), stopReason: "max_tokens"))).Single();

        resultado.ModeloEnviado.Should().Be("modelo-de-prueba");
        resultado.MaxTokensEnviado.Should().BePositive();
        resultado.ModeloQueRespondio.Should().Be("modelo-simulado");
        resultado.StopReason.Should().Be("max_tokens");
        resultado.TokensEntrada.Should().Be(120);
        resultado.TokensSalida.Should().Be(30);
        resultado.EstadoHttp.Should().Be(200);
        resultado.Intentos.Should().Be(1);
        resultado.CosteUsd.Should().BeNull("el modelo de prueba no tiene tarifa y un coste inventado sería peor que ninguno");
    }

    /// <summary>Responde 429 las primeras veces y después 200, devolviendo como texto la petición que recibió.</summary>
    private sealed class SaturadoAlPrincipio(int rechazos) : HttpMessageHandler
    {
        private int _vistas;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (++_vistas <= rechazos)
                return new HttpResponseMessage(System.Net.HttpStatusCode.TooManyRequests) { Content = new StringContent("{}") };

            var cuerpo = JsonSerializer.Serialize(new
            {
                model = "modelo-simulado",
                stop_reason = "end_turn",
                content = new[] { new { type = "text", text = await request.Content!.ReadAsStringAsync(cancellationToken) } },
                usage = new { input_tokens = 7, output_tokens = 3 },
            });
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(cuerpo, System.Text.Encoding.UTF8, "application/json") };
        }
    }

    private sealed class RedCaida : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("sin red");
    }

    [Fact]
    public async Task La_sonda_lee_el_esfuerzo_de_la_peticion_y_reintenta_una_saturacion_sin_perder_la_cuenta()
    {
        var sonda = new SondaAnthropic(new SaturadoAlPrincipio(rechazos: 1)) { EsperaEntreIntentos = TimeSpan.Zero };
        using var http = new HttpClient(sonda);
        var peticion = JsonSerializer.Serialize(new { model = "modelo-pedido", max_tokens = 2048, output_config = new { effort = "medium" } });

        using var respuesta = await http.PostAsync("https://banco.invalid/v1/messages", new StringContent(peticion));

        ((int)respuesta.StatusCode).Should().Be(200);
        var llamada = sonda.Llamadas.Should().ContainSingle().Subject;
        llamada.Intentos.Should().Be(2, "el 429 se reintenta y no cuenta como respuesta del modelo");
        llamada.EsfuerzoEnviado.Should().Be("medium");
        llamada.ModeloEnviado.Should().Be("modelo-pedido");
        llamada.MaxTokensEnviado.Should().Be(2048);
        llamada.TokensEntrada.Should().Be(7);
    }

    [Fact]
    public async Task Una_saturacion_que_no_cede_se_anota_con_su_estado_tras_tres_intentos()
    {
        var sonda = new SondaAnthropic(new SaturadoAlPrincipio(rechazos: 99)) { EsperaEntreIntentos = TimeSpan.Zero };
        using var http = new HttpClient(sonda);

        using var respuesta = await http.PostAsync("https://banco.invalid/v1/messages", new StringContent("{}"));

        var llamada = sonda.Llamadas.Should().ContainSingle().Subject;
        llamada.Intentos.Should().Be(3);
        llamada.EstadoHttp.Should().Be(429);
    }

    [Fact]
    public async Task Con_la_red_caida_el_caso_queda_medido_como_fallo_de_red_y_el_banco_sigue_con_los_demas()
    {
        var casos = CorpusBancoModelosIa.Casos.Where(c => c.Ruta == RutaIa.RelevanciaCaeDeConversacion).Take(2).ToList();

        var resultados = await EjecutorBancoModelosIa.EjecutarAsync(
            ParametrosDePrueba, "clave-de-prueba", casos, _ => new SondaAnthropic(new RedCaida()) { EsperaEntreIntentos = TimeSpan.Zero });

        resultados.Should().HaveCount(2).And.OnlyContain(r => r.Puntuacion == 0 && r.EstadoHttp == 0 && r.Intentos == 3);
        ValidacionDelInstrumento.Problemas(ParametrosDePrueba, resultados).Should().Contain(p => p.Contains("la red falló"));
    }

    private sealed record CasoQueRevienta() : CasoBanco("caso-que-revienta", NivelCaso.Sencillo, "Deja escapar una excepción que el servicio no captura")
    {
        public override RutaIa Ruta => RutaIa.ChatDelAsistente;

        public override Task<Evaluacion> EjecutarAsync(ServiciosAnthropic servicios, CancellationToken cancellationToken) =>
            throw new NotSupportedException("tipo de contenido inesperado");

        public override string RespuestaIdeal() => string.Empty;

        public override string RespuestaErronea() => string.Empty;
    }

    [Fact]
    public async Task Una_excepcion_que_escapa_de_un_caso_no_pierde_la_medicion_de_los_demas()
    {
        var siguiente = CorpusBancoModelosIa.Casos.First(c => c.Id == "rel-sencillo-alta");

        var resultados = await EjecutorBancoModelosIa.EjecutarAsync(
            ParametrosDePrueba, "clave-de-prueba", [new CasoQueRevienta(), siguiente], c => new ModeloSimulado(c.RespuestaIdeal()));

        resultados.Should().HaveCount(2);
        resultados[0].ErrorServicio.Should().Be("Excepcion.NotSupportedException");
        resultados[0].Puntuacion.Should().Be(0);
        resultados[1].Puntuacion.Should().Be(1);
    }

    [Fact]
    public async Task El_tope_de_salida_pedido_viaja_y_las_respuestas_truncadas_se_declaran_en_el_informe()
    {
        var parametros = ParametrosDePrueba with { MaxTokens = 4096 };
        var caso = CorpusBancoModelosIa.Casos.First(c => c.Id == "rel-sencillo-alta");

        var resultados = await EjecutorBancoModelosIa.EjecutarAsync(
            parametros, "clave-de-prueba", [caso], c => new ModeloSimulado(c.RespuestaIdeal(), stopReason: "max_tokens"));

        resultados.Single().MaxTokensEnviado.Should().Be(4096);
        ValidacionDelInstrumento.Problemas(parametros, resultados).Should().BeEmpty();
        ValidacionDelInstrumento.Problemas(parametros with { MaxTokens = 512 }, resultados).Should().ContainSingle().Which.Should().Contain("tope de salida");

        var md = InformeBancoModelosIa.Markdown(InformeBancoModelosIa.Construir(parametros, resultados, [], [], DateTimeOffset.UnixEpoch, null));
        md.Should().Contain("Respuestas truncadas por el tope de salida (1)").And.Contain("rel-sencillo-alta").And.Contain("`max_tokens`): 4096");
    }

    [Fact]
    public void El_escaneo_sintetico_sabe_dibujar_todos_los_caracteres_de_su_documento_y_no_sale_en_blanco()
    {
        var caso = (CasoTranscripcion)CorpusBancoModelosIa.Casos.Single(c => c.Id == "ocr-dificil-escaneo");

        TextoBanco.SinTildes(caso.TextoDeReferencia).ToUpperInvariant().Where(c => c != '\n' && !EscaneoSintetico.SabeDibujar(c))
            .Should().BeEmpty("un carácter sin glifo saldría en blanco y el caso pediría leer lo que no está");

        using var imagen = SkiaSharp.SKBitmap.Decode(caso.Archivo);
        var oscuros = 0;
        for (var y = 0; y < imagen.Height; y += 2)
        {
            for (var x = 0; x < imagen.Width; x += 2)
            {
                if (imagen.GetPixel(x, y).Red < 110)
                    oscuros++;
            }
        }

        oscuros.Should().BeGreaterThan(5000, "el documento lleva diez líneas de texto");
        CorpusBancoModelosIa.CasosNoConstruibles.Should().BeEmpty();
    }

    [Fact]
    public void El_coste_se_estima_con_la_tarifa_del_modelo_o_con_la_que_de_la_ejecucion()
    {
        TarifasAnthropic.Para(ParametrosDePrueba with { Modelo = "claude-sonnet-5" })!.Coste(1_000_000, 100_000).Should().Be(3m);
        TarifasAnthropic.Para(ParametrosDePrueba with { Modelo = "modelo-futuro", PrecioEntrada = 1m, PrecioSalida = 4m })!
            .Coste(500_000, 250_000).Should().Be(1.5m);
        TarifasAnthropic.Para(ParametrosDePrueba with { Modelo = "modelo-futuro" }).Should().BeNull();
        TarifasAnthropic.Para(ParametrosDePrueba with { Modelo = "claude-haiku-5-5" }).Should().Be(new TarifaAnthropic(0.10m, 0.50m));
        DateOnly.ParseExact(TarifasAnthropic.FechaDeConsulta, "yyyy-MM-dd").Should().BeAfter(new DateOnly(2026, 1, 1));
        TarifasAnthropic.Fuente.Should().StartWith("https://");
    }

    [Fact]
    public void Sin_clave_el_banco_se_declara_omitido_con_su_motivo_y_con_clave_no()
    {
        FactSiHayClaveDeAnthropicAttribute.MotivoDeOmision(null, "1").Should().Contain("NO ejecutado").And.Contain("falta la clave");
        FactSiHayClaveDeAnthropicAttribute.MotivoDeOmision("  ", "1").Should().Contain("NO ejecutado");
        FactSiHayClaveDeAnthropicAttribute.MotivoDeOmision("una-clave", null).Should().Contain("NO ejecutado").And.Contain("nadie lo ha pedido");
        FactSiHayClaveDeAnthropicAttribute.MotivoDeOmision("una-clave", "1").Should().BeNull();

        FactSiSePideAttribute.MotivoDeOmision("BANCO_IA_SOLO_ESTIMAR", null).Should().Contain("no pedido");
        FactSiSePideAttribute.MotivoDeOmision("BANCO_IA_SOLO_ESTIMAR", "1").Should().BeNull();
    }

    [Fact]
    public void El_instrumento_se_declara_invalido_si_no_ejecuta_nada_o_si_el_modelo_o_el_esfuerzo_pedidos_no_viajan()
    {
        var pedido = ParametrosDePrueba with { Modelo = "modelo-pedido", Esfuerzo = "low" };
        ResultadoCaso Caso(string? modelo, string? esfuerzo, int estado = 200, int intentos = 1) => new(
            RutaIa.ChatDelAsistente, "caso", NivelCaso.Sencillo, "", 1, 1, 1, [], new Dictionary<string, double>(), null,
            modelo, esfuerzo, 1024, modelo, estado, 10, intentos, 1, 1, "end_turn", null);

        ValidacionDelInstrumento.Problemas(pedido, [Caso("modelo-pedido", "low")]).Should().BeEmpty();
        ValidacionDelInstrumento.Problemas(pedido, []).Should().ContainSingle().Which.Should().Contain("ningún caso");
        ValidacionDelInstrumento.Problemas(pedido, [Caso("modelo-pedido", null)]).Should().ContainSingle().Which.Should().Contain("NO mide ese esfuerzo");
        ValidacionDelInstrumento.Problemas(pedido, [Caso("modelo-pedido", "high")]).Should().ContainSingle().Which.Should().Contain("NO mide ese esfuerzo");
        ValidacionDelInstrumento.Problemas(pedido, [Caso("otro-modelo", "low")]).Should().ContainSingle().Which.Should().Contain("modelo pedido");
        ValidacionDelInstrumento.Problemas(pedido, [Caso("modelo-pedido", "low", estado: 400)]).Should().ContainSingle().Which.Should().Contain("rechazó");
        ValidacionDelInstrumento.Problemas(pedido, [Caso(null, null, estado: 0, intentos: 0)]).Should().ContainSingle().Which.Should().Contain("no llegaron a llamar");

        // Sin esfuerzo pedido, el banco mide el que el producto envíe por su cuenta: no hay nada que contrastar.
        ValidacionDelInstrumento.Problemas(pedido with { Esfuerzo = null }, [Caso("modelo-pedido", null)]).Should().BeEmpty();
    }

    [Fact]
    public async Task El_informe_sale_en_JSON_CSV_y_Markdown_con_una_fila_por_caso_y_otra_por_ruta()
    {
        var parametros = ParametrosDePrueba with { CarpetaSalida = Path.Combine(Path.GetTempPath(), "banco-modelos-ia-" + Guid.NewGuid().ToString("N")) };
        var resultados = await EjecutorBancoModelosIa.EjecutarAsync(
            parametros, "clave-de-prueba", CorpusBancoModelosIa.Casos, caso => new ModeloSimulado(caso.RespuestaErronea()));
        var informe = InformeBancoModelosIa.Construir(parametros, resultados, ["problema de ejemplo"], [], DateTimeOffset.UnixEpoch, "abc123");

        try
        {
            var markdown = InformeBancoModelosIa.Escribir(informe, parametros.CarpetaSalida);

            File.ReadAllText(markdown).Should().Contain("Medición NO válida").And.Contain("problema de ejemplo").And.Contain(nameof(RutaIa.ListadoDeTrabajadores));
            File.ReadAllLines(Path.ChangeExtension(markdown, ".csv")).Should().HaveCount(resultados.Count + 1);

            using var json = JsonDocument.Parse(File.ReadAllText(Path.ChangeExtension(markdown, ".json")));
            json.RootElement.GetProperty("casos").GetArrayLength().Should().Be(resultados.Count);
            json.RootElement.GetProperty("rutas").GetArrayLength().Should().Be(Enum.GetValues<RutaIa>().Length);
            json.RootElement.GetProperty("modelo").GetString().Should().Be("modelo-de-prueba");
        }
        finally
        {
            Directory.Delete(parametros.CarpetaSalida, recursive: true);
        }
    }

    [Fact]
    public async Task La_clave_no_aparece_en_el_informe_ni_en_el_libro_de_gasto()
    {
        const string clave = "sk-ant-api03-CANARIO-QUE-NO-DEBE-SALIR";
        var carpeta = Path.Combine(Path.GetTempPath(), "banco-modelos-ia-" + Guid.NewGuid().ToString("N"));
        var parametros = ParametrosDePrueba with { CarpetaSalida = carpeta, Modelo = "claude-sonnet-5" };
        var resultados = await EjecutorBancoModelosIa.EjecutarAsync(
            parametros, clave, CorpusBancoModelosIa.Casos, caso => new ModeloSimulado(caso.RespuestaIdeal()));
        var informe = InformeBancoModelosIa.Construir(parametros, resultados, [], [], DateTimeOffset.UnixEpoch, null);

        try
        {
            InformeBancoModelosIa.Escribir(informe, carpeta);
            LibroDeGasto.Anotar(informe, Path.Combine(carpeta, "libro.csv"), "local");

            var escrito = Directory.GetFiles(carpeta);
            escrito.Should().HaveCount(4);
            foreach (var fichero in escrito)
                File.ReadAllText(fichero).Should().NotContain("CANARIO", $"{Path.GetFileName(fichero)} se publica como artefacto");
        }
        finally
        {
            Directory.Delete(carpeta, recursive: true);
        }
    }

    [Fact]
    public async Task El_libro_de_gasto_suma_una_linea_por_ruta_no_duplica_y_acumula_solo_el_mes_pedido()
    {
        var libro = Path.Combine(Path.GetTempPath(), "banco-modelos-ia-libro-" + Guid.NewGuid().ToString("N") + ".csv");
        var parametros = ParametrosDePrueba with { Modelo = "claude-sonnet-5" };
        var resultados = await EjecutorBancoModelosIa.EjecutarAsync(
            parametros, "clave-de-prueba", CorpusBancoModelosIa.Casos, caso => new ModeloSimulado(caso.RespuestaIdeal()));
        var octubre = InformeBancoModelosIa.Construir(parametros, resultados, [], [], new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero), "abc");
        var noviembre = octubre with { Fecha = new DateTimeOffset(2026, 11, 2, 8, 0, 0, TimeSpan.Zero) };
        // Cada caso simulado consume 120 tokens de entrada y 30 de salida: 120 × 2 $ + 30 × 10 $ por millón.
        var costePorPasada = resultados.Count * (120 * 2m + 30 * 10m) / 1_000_000m;

        try
        {
            LibroDeGasto.Anotar(octubre, libro, "local").Should().Be(Enum.GetValues<RutaIa>().Length);
            LibroDeGasto.Anotar(octubre, libro, "local").Should().Be(0, "anotar dos veces el mismo informe no duplica el gasto");
            LibroDeGasto.Anotar(noviembre, libro, "ci").Should().Be(Enum.GetValues<RutaIa>().Length);

            File.ReadAllLines(libro)[0].Should().Be(LibroDeGasto.Cabecera);
            File.ReadAllLines(libro).Should().HaveCount(1 + 2 * Enum.GetValues<RutaIa>().Length);
            octubre.CosteTotalUsd.Should().Be(costePorPasada);
            LibroDeGasto.AcumuladoDelMes(libro, octubre.Fecha).Should().Be(costePorPasada);
            LibroDeGasto.AcumuladoDelMes(libro, new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.Zero)).Should().Be(0);
        }
        finally
        {
            File.Delete(libro);
        }
    }

    [Fact]
    public async Task El_resumen_da_coste_por_caso_proyeccion_por_mil_y_acumulado_del_mes_y_dice_que_es_medido_y_que_calculado()
    {
        var parametros = ParametrosDePrueba with { Modelo = "claude-sonnet-5" };
        var resultados = await EjecutorBancoModelosIa.EjecutarAsync(
            parametros, "clave-de-prueba", CorpusBancoModelosIa.Casos, caso => new ModeloSimulado(caso.RespuestaIdeal()));
        var sinGasto = InformeBancoModelosIa.Construir(parametros, resultados, [], [], DateTimeOffset.UnixEpoch, null);
        var informe = sinGasto with { GastoDelMes = new GastoDelMes("2026-10", 12m, 240m) };

        var chat = informe.Rutas.Single(r => r.Ruta == RutaIa.ChatDelAsistente);
        chat.CostePorCasoUsd.Should().Be(0.00054m);
        chat.CostePorMilUsd.Should().Be(0.54m);

        var markdown = InformeBancoModelosIa.Markdown(informe);
        markdown.Should().Contain("**Medido**").And.Contain("**Calculado**").And.Contain("1.000 documentos")
            .And.Contain("Acumulado de 2026-10").And.Contain("12.0000 $ de 240 $").And.Contain(TarifasAnthropic.FechaDeConsulta);
    }

    [Fact]
    public async Task La_estimacion_sin_llamadas_acota_la_entrada_por_el_texto_y_las_paginas_y_la_salida_por_max_tokens()
    {
        var parametros = ParametrosDePrueba with { Modelo = "claude-sonnet-5" };
        var listado = CorpusBancoModelosIa.Casos.First(c => c.Id == "lis-dificil-sesenta");
        var relevancia = CorpusBancoModelosIa.Casos.First(c => c.Id == "rel-sencillo-alta");

        var peticiones = await EjecutorBancoModelosIa.EjecutarAsync(
            parametros, "sin-clave", [listado, relevancia], _ => new ModeloSimulado(string.Empty));

        // El listado de sesenta Trabajadores ocupa dos páginas de PDF: dos veces la cota por página, más el texto del prompt.
        peticiones.Single(p => p.Caso == listado.Id).TokensEntradaCota.Should().BeInRange(
            2 * CotaDeTokensDeEntrada.TokensPorPaginaOImagen, 2 * CotaDeTokensDeEntrada.TokensPorPaginaOImagen + 2000);
        peticiones.Single(p => p.Caso == relevancia.Id).TokensEntradaCota.Should().BeInRange(300, 3000);

        var markdown = InformeBancoModelosIa.MarkdownDeEstimacion(parametros, peticiones);
        markdown.Should().Contain("cota superior").And.Contain("No se ha hecho ninguna llamada");
        var maxTokens = peticiones.Sum(p => p.MaxTokensEnviado!.Value);
        markdown.Should().Contain($"| **Total** | 2 | {peticiones.Sum(p => p.TokensEntradaCota)} | {maxTokens} |");
    }

    [Fact]
    public void El_banco_conoce_la_clave_de_configuracion_de_las_siete_rutas()
    {
        EjecutorBancoModelosIa.ClaveDeConfiguracion.Keys.Should().BeEquivalentTo(Enum.GetValues<RutaIa>());
        EjecutorBancoModelosIa.ClaveDeConfiguracion.Values.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Los_parametros_salen_del_entorno_y_sin_ellos_el_modelo_es_el_del_producto()
    {
        var entorno = new Dictionary<string, string?>
        {
            ["BANCO_IA_MODELO"] = " claude-haiku-5-5 ",
            ["BANCO_IA_ESFUERZO"] = "medium",
            ["BANCO_IA_RUTAS"] = "ChatDelAsistente, visitaencorreo",
            ["BANCO_IA_PRECIO_ENTRADA"] = "0.25",
            ["BANCO_IA_PRECIO_SALIDA"] = "1.5",
        };

        var conTodo = ParametrosBanco.DesdeEntorno(entorno.GetValueOrDefault);
        conTodo.Modelo.Should().Be("claude-haiku-5-5");
        conTodo.Esfuerzo.Should().Be("medium");
        conTodo.Rutas.Should().BeEquivalentTo([RutaIa.ChatDelAsistente, RutaIa.VisitaEnCorreo]);
        conTodo.PrecioEntrada.Should().Be(0.25m);
        conTodo.PrecioSalida.Should().Be(1.5m);

        var sinNada = ParametrosBanco.DesdeEntorno(_ => null);
        sinNada.Modelo.Should().Be(new AnthropicOptions().Modelo);
        sinNada.Esfuerzo.Should().BeNull();
        sinNada.Rutas.Should().BeNull();
    }

    [Fact]
    public void La_comparacion_de_texto_no_distingue_tildes_ni_mayusculas_y_los_identificadores_ignoran_separadores()
    {
        TextoBanco.Contiene("IBANEZ SOLIS, MARTA", "Ibáñez").Should().BeTrue();
        TextoBanco.Contiene("según el artículo 24", "ARTICULO 24").Should().BeTrue();
        TextoBanco.Contiene("Ibarra", "Ibáñez").Should().BeFalse();
        TextoBanco.Identificador(" 12.345.678-z ").Should().Be("12345678Z");
    }

    [Fact]
    public void Los_identificadores_sinteticos_llevan_una_letra_de_control_que_no_es_la_suya()
    {
        const string letras = "TRWAGMYFPDXBNJZSQVHLCKE";

        foreach (var n in Enumerable.Range(0, 300))
        {
            var dni = DatosSinteticos.Dni(n);
            dni[^1].Should().NotBe(letras[int.Parse(dni[..8]) % 23], $"{dni} no puede ser el DNI válido de nadie");
        }

        Enumerable.Range(0, 300).Select(DatosSinteticos.Dni).Should().OnlyHaveUniqueItems();
    }
}
