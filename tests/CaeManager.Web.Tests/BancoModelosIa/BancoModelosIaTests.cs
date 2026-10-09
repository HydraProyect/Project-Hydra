using System.Text.Json;
using CaeManager.Infrastructure.AsistenteIa;
using FluentAssertions;
using Xunit.Abstractions;

namespace CaeManager.Web.Tests.BancoModelosIa;

/// <summary>
/// La medición con modelo real solo corre con una clave de Anthropic en
/// <c>Anthropic__ApiKey</c>. Sin ella el test queda OMITIDO con este motivo
/// a la vista, nunca en verde: un verde sin casos no es una medición.
/// </summary>
public sealed class FactSiHayClaveDeAnthropicAttribute : FactAttribute
{
    public const string Variable = AnthropicOptions.SeccionConfiguracion + "__ApiKey";

    public FactSiHayClaveDeAnthropicAttribute()
    {
        Skip = MotivoDeOmision(Environment.GetEnvironmentVariable(Variable));
    }

    public static string? MotivoDeOmision(string? clave) =>
        string.IsNullOrWhiteSpace(clave)
            ? $"Banco de modelos de IA NO ejecutado: falta la clave en \"{Variable}\". En CI solo la aporta el workflow " +
              "integraciones-con-clave.yml, lanzado a mano con la entrada banco_modelos; en ci.yml este test se omite por diseño."
            : null;
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

        var resultados = await EjecutorBancoModelosIa.EjecutarAsync(
            parametros, clave, CorpusBancoModelosIa.Casos, _ => new SocketsHttpHandler());

        var problemas = ValidacionDelInstrumento.Problemas(parametros, resultados);
        var informe = InformeBancoModelosIa.Construir(
            parametros, resultados, problemas, CorpusBancoModelosIa.CasosNoConstruibles,
            DateTimeOffset.UtcNow, Environment.GetEnvironmentVariable("GITHUB_SHA"));
        var rutaMarkdown = InformeBancoModelosIa.Escribir(informe, parametros.CarpetaSalida);

        salida.WriteLine(InformeBancoModelosIa.Markdown(informe));
        salida.WriteLine($"Informe escrito en {rutaMarkdown}");

        problemas.Should().BeEmpty("la medición solo es comparable si el instrumento midió lo que se le pidió");
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

        var conInventado = await EjecutarUnoAsync(caso, Json([.. caso.Esperados, new TrabajadorEsperado("Pedro", "Inventado", "00000000T")]));
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

    [Fact]
    public void El_coste_se_estima_con_la_tarifa_del_modelo_o_con_la_que_de_la_ejecucion()
    {
        TarifasAnthropic.Para(ParametrosDePrueba with { Modelo = "claude-sonnet-5" })!.Coste(1_000_000, 100_000).Should().Be(3m);
        TarifasAnthropic.Para(ParametrosDePrueba with { Modelo = "modelo-futuro", PrecioEntrada = 1m, PrecioSalida = 4m })!
            .Coste(500_000, 250_000).Should().Be(1.5m);
        TarifasAnthropic.Para(ParametrosDePrueba with { Modelo = "modelo-futuro" }).Should().BeNull();
    }

    [Fact]
    public void Sin_clave_el_banco_se_declara_omitido_con_su_motivo_y_con_clave_no()
    {
        FactSiHayClaveDeAnthropicAttribute.MotivoDeOmision(null).Should().Contain("NO ejecutado");
        FactSiHayClaveDeAnthropicAttribute.MotivoDeOmision("  ").Should().Contain("NO ejecutado");
        FactSiHayClaveDeAnthropicAttribute.MotivoDeOmision("una-clave").Should().BeNull();
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
