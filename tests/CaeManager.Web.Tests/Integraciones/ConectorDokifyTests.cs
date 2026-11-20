using System.Net;
using System.Text;
using CaeManager.Application.Integraciones.ConectoresPlataformaCae;
using CaeManager.Domain.Integraciones;
using CaeManager.Infrastructure.Integraciones;
using CaeManager.Infrastructure.Integraciones.ConectoresPlataformaCae.Dokify;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace CaeManager.Web.Tests.Integraciones;

/// <summary>
/// El conector de la API pública de dokify contra respuestas HTTP simuladas con JSON escrito a mano: ninguna prueba sale a
/// la red ni usa credenciales reales. Cubre el mapeo de Empresas, Trabajadores, máquinas, estados, documentos,
/// asignaciones y descargas, el estado Desconocido, la paginación, la cabecera de autenticación, la traducción de cada
/// respuesta a <see cref="MotivoUsoConector"/> y que ni el token, ni el CIF o NIF de la ruta, ni el texto de dokify acaban
/// en un log. Viven en Web.Tests y no en IntegrationTests porque no necesitan base de datos y así corren en local.
/// </summary>
public class ConectorDokifyTests
{
    private const string Token = "token-de-prueba-0f1e2d3c4b5a69788796a5b4c3d2e1f0";
    private const string CifPrueba = "B00000001";
    private const string NifPrueba = "TEST0001A";
    private const string TextoDelTercero = "Employee TEST0001A not found for company B00000001 (nombre de prueba)";
    private const string Base = "https://app.dokify.net/app/";

    private readonly HandlerFalso _handler = new();
    private readonly LoggerQueCaptura _logger = new();
    private readonly CredencialApiConector _credencial = new(Token);

    private DokifyClient CrearCliente(TimeSpan? timeout = null, DokifyOptions? opciones = null) =>
        new(new HttpClient(_handler) { Timeout = timeout ?? TimeSpan.FromSeconds(10) }, Options.Create(opciones ?? new DokifyOptions()), _logger);

    private ConectorDokify Crear(TimeSpan? timeout = null) => new(CrearCliente(timeout));

    private static string Pagina(int numero, int totalPaginas, string datos) =>
        "{\"links\":[{\"rel\":\"self\",\"href\":\"x\"}],\"data\":[" + datos + "],\"page\":{\"size\":50,\"totalElements\":3,\"totalPages\":"
        + totalPaginas + ",\"number\":" + numero + "}}";

    [Fact]
    public void El_codigo_es_el_del_proveedor_sembrado_para_dokify_net()
    {
        var sembrado = CaeManager.Infrastructure.Persistence.Seed.ProveedorPlataformaCaeSeedData.Datos
            .Should().ContainSingle(p => p.Dominios.Contains("dokify.net"), "un solo proveedor sembrado sirve dokify.net")
            .Subject;

        Crear().CodigoProveedor.Should().Be(sembrado.Codigo);
    }

    [Fact]
    public async Task Una_pagina_de_empresas_se_pide_con_el_token_en_la_cabecera_y_se_mapea()
    {
        _handler.Responder(HttpStatusCode.OK, Pagina(0, 2, """{"vat":"B00000001","name":"Empresa Uno"},{"vat":"B00000002","name":"Empresa Dos"},{"name":"Sin identificador"}"""));

        var resultado = await CrearCliente().ListarEmpresasAsync(_credencial, 0, 50, CancellationToken.None);

        resultado.Fallo.Should().BeNull();
        resultado.Valor.Datos.Should().Equal(new EmpresaDokify("B00000001", "Empresa Uno"), new EmpresaDokify("B00000002", "Empresa Dos"));
        resultado.Valor.Numero.Should().Be(0);
        resultado.Valor.HayMasPaginas.Should().BeTrue();

        var peticion = _handler.Peticiones.Should().ContainSingle().Subject;
        peticion.Metodo.Should().Be(HttpMethod.Get);
        peticion.Uri.Should().Be(new Uri(Base + "clients/self/companies?page=0&size=50"));
        peticion.Uri.ToString().Should().NotContain(Token, "el token nunca va en la URL");
        peticion.Cabeceras["Authorization"].Should().ContainSingle().Which.Should().Be("Bearer " + Token);
    }

    [Fact]
    public async Task Una_pagina_de_trabajadores_cuelga_de_la_empresa_y_no_escribe_sus_datos_personales()
    {
        _handler.Responder(HttpStatusCode.OK, Pagina(0, 1, """{"firstname":"Nombre","surname":"Apellido Prueba","vat":"TEST0001A"}"""));

        var resultado = await CrearCliente().ListarTrabajadoresDeEmpresaAsync(_credencial, CifPrueba, 0, 50, CancellationToken.None);

        var trabajador = resultado.Valor.Datos.Should().ContainSingle().Subject;
        trabajador.Should().Be(new TrabajadorDokify("TEST0001A", "Nombre", "Apellido Prueba"));
        trabajador.ToString().Should().NotContain("TEST0001A").And.NotContain("Apellido");
        resultado.Valor.HayMasPaginas.Should().BeFalse();
        _handler.Peticiones.Single().Uri.Should().Be(new Uri(Base + "clients/self/companies/B00000001/employees?page=0&size=50"));
    }

    [Fact]
    public async Task Todas_las_empresas_se_piden_pagina_a_pagina_hasta_la_ultima()
    {
        _handler.Responder(p => (HttpStatusCode.OK, p switch
        {
            0 => Pagina(0, 3, """{"vat":"B00000001","name":"Uno"}"""),
            1 => Pagina(1, 3, """{"vat":"B00000002","name":"Dos"}"""),
            _ => Pagina(2, 3, """{"vat":"B00000003","name":"Tres"}"""),
        }));

        var resultado = await Crear().ListarEmpresasAsync(_credencial, CancellationToken.None);

        resultado.Valor.Select(e => e.Vat).Should().Equal("B00000001", "B00000002", "B00000003");
        _handler.Peticiones.Select(p => p.Uri.Query).Should().Equal("?page=0&size=50", "?page=1&size=50", "?page=2&size=50");
    }

    [Fact]
    public async Task Todos_los_trabajadores_de_una_empresa_vacia_son_una_lista_vacia()
    {
        _handler.Responder(HttpStatusCode.OK, Pagina(0, 0, string.Empty));

        var resultado = await Crear().ListarTrabajadoresAsync(_credencial, CifPrueba, CancellationToken.None);

        resultado.Fallo.Should().BeNull();
        resultado.Valor.Should().BeEmpty();
        _handler.Peticiones.Should().ContainSingle();
    }

    [Fact]
    public async Task Un_servidor_que_ignora_page_y_devuelve_siempre_la_misma_pagina_es_inesperado_y_no_se_pide_sin_fin()
    {
        _handler.Responder(HttpStatusCode.OK, Pagina(0, 5, """{"vat":"B00000001","name":"Uno"}"""));

        var resultado = await Crear().ListarEmpresasAsync(_credencial, CancellationToken.None);

        resultado.Fallo.Should().Be(MotivoUsoConector.Inesperado);
        _handler.Peticiones.Should().HaveCount(2, "la segunda página no es la pedida y se corta ahí");
    }

    [Fact]
    public async Task Un_listado_con_mas_paginas_que_el_tope_se_corta_como_inesperado()
    {
        _handler.Responder(p => (HttpStatusCode.OK, Pagina(p, int.MaxValue, string.Empty)));

        var resultado = await Crear().ListarEmpresasAsync(_credencial, CancellationToken.None);

        resultado.Fallo.Should().Be(MotivoUsoConector.Inesperado);
        _handler.Peticiones.Should().HaveCount(ConectorDokify.MaximoDePaginas);
    }

    [Theory]
    [InlineData("""{"data":[],"page":{"totalPages":1}}""")]
    [InlineData("""{"data":{},"page":{"number":0,"totalPages":1}}""")]
    [InlineData("""{"page":{"number":0,"totalPages":1}}""")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("<html>no es json</html>")]
    public async Task Una_pagina_sin_la_forma_esperada_es_inesperada(string cuerpo)
    {
        _handler.Responder(HttpStatusCode.OK, cuerpo);

        var resultado = await CrearCliente().ListarEmpresasAsync(_credencial, 0, 50, CancellationToken.None);

        resultado.Fallo.Should().Be(MotivoUsoConector.Inesperado);
    }

    [Theory]
    [InlineData(-1, 50)]
    [InlineData(0, 0)]
    [InlineData(0, DokifyClient.TamanoMaximoPagina + 1)]
    public async Task Una_pagina_o_un_tamano_fuera_de_rango_no_llama(int pagina, int tamano)
    {
        var resultado = await CrearCliente().ListarEmpresasAsync(_credencial, pagina, tamano, CancellationToken.None);

        resultado.Fallo.Should().Be(MotivoUsoConector.DatosInsuficientes);
        _handler.Peticiones.Should().BeEmpty();
    }

    [Fact]
    public async Task Una_maquina_se_mapea_desde_su_ficha()
    {
        _handler.Responder(HttpStatusCode.OK, """
            {"manufacture":"Fabricante Prueba","model":"M-1","name":"Maquina de prueba","plate":"MAQ-0001","serial":"SN-0001",
             "uid":12,"companies":[{"name":"Empresa Uno","cif":"B00000001"}],"extra_data":[]}
            """);

        var resultado = await Crear().ObtenerMaquinaAsync(_credencial, "MAQ-0001", CancellationToken.None);

        resultado.Valor.Should().Be(new MaquinaDokify("MAQ-0001", "Maquina de prueba", "M-1", "Fabricante Prueba", "SN-0001"));
        _handler.Peticiones.Single().Uri.Should().Be(new Uri(Base + "machine/MAQ-0001/info"));
    }

    [Fact]
    public async Task Una_ficha_de_maquina_sin_matricula_es_inesperada()
    {
        _handler.Responder(HttpStatusCode.OK, """{"name":"Maquina de prueba"}""");

        (await Crear().ObtenerMaquinaAsync(_credencial, "MAQ-0001", CancellationToken.None)).Fallo.Should().Be(MotivoUsoConector.Inesperado);
    }

    [Theory]
    [InlineData(ElementoDokify.Empresa, "company/B00000001/status")]
    [InlineData(ElementoDokify.Trabajador, "employee/B00000001/status")]
    [InlineData(ElementoDokify.Maquina, "machine/B00000001/status")]
    public async Task El_estado_global_se_lee_de_la_ruta_de_cada_clase_de_elemento(ElementoDokify elemento, string ruta)
    {
        _handler.Responder(HttpStatusCode.OK, """{"status":true}""");

        var resultado = await Crear().ObtenerEstadoAsync(_credencial, elemento, CifPrueba, CancellationToken.None);

        resultado.Valor.Status.Should().BeTrue();
        _handler.Peticiones.Single().Uri.Should().Be(new Uri(Base + ruta));
    }

    [Theory]
    [InlineData("""{"status":"true"}""")]
    [InlineData("""{"status":null}""")]
    [InlineData("""{}""")]
    [InlineData("[]")]
    public async Task Un_estado_global_que_no_es_un_booleano_es_inesperado(string cuerpo)
    {
        _handler.Responder(HttpStatusCode.OK, cuerpo);

        (await Crear().ObtenerEstadoAsync(_credencial, ElementoDokify.Empresa, CifPrueba, CancellationToken.None))
            .Fallo.Should().Be(MotivoUsoConector.Inesperado);
    }

    private const string DocumentosDeEjemplo = """
        {
          "11": {"uid":11,"name":"Seguro","requests":{
            "501":{"uid":501,"name":"Seguro 2026","status":"Validado","status_code":2},
            "502":{"uid":502,"name":"Seguro 2025","status":"Caducado","status_code":3}}},
          "12": {"uid":12,"name":"Alta","requests":{
            "503":{"uid":503,"status":"Sin anexar","status_code":0},
            "504":{"uid":504,"status":"Anulado","status_code":4},
            "505":{"uid":505,"status":"Algo nuevo","status_code":7},
            "506":{"uid":506,"status":"Sin codigo"},
            "507":{"uid":507,"status_code":"2"}}},
          "13": {"uid":13,"name":"Vacio","requests":[]}
        }
        """;

    [Fact]
    public async Task Los_documentos_se_mapean_uno_por_solicitud_y_un_codigo_desconocido_nunca_es_validado()
    {
        _handler.Responder(HttpStatusCode.OK, DocumentosDeEjemplo);

        var resultado = await Crear().ObtenerDocumentosAsync(_credencial, ElementoDokify.Trabajador, NifPrueba, CancellationToken.None);

        resultado.Valor.Select(d => (d.TipoDocumentoId, d.SolicitudId, d.CodigoEstado, d.Estado)).Should().Equal(
            ("11", "501", 2, EstadoDocumentoDokify.Validado),
            ("11", "502", 3, EstadoDocumentoDokify.Caducado),
            ("12", "503", 0, EstadoDocumentoDokify.SinAnexar),
            ("12", "504", 4, EstadoDocumentoDokify.Anulado),
            ("12", "505", 7, EstadoDocumentoDokify.Desconocido),
            ("12", "506", null, EstadoDocumentoDokify.Desconocido),
            ("12", "507", 2, EstadoDocumentoDokify.Validado));
        resultado.Valor.First().TipoDocumentoNombre.Should().Be("Seguro");
        _handler.Peticiones.Single().Uri.Should().Be(new Uri(Base + "employee/TEST0001A/docs"));
    }

    [Theory]
    [InlineData(0, EstadoDocumentoDokify.SinAnexar)]
    [InlineData(2, EstadoDocumentoDokify.Validado)]
    [InlineData(3, EstadoDocumentoDokify.Caducado)]
    [InlineData(4, EstadoDocumentoDokify.Anulado)]
    [InlineData(1, EstadoDocumentoDokify.Desconocido)]
    [InlineData(5, EstadoDocumentoDokify.Desconocido)]
    [InlineData(-1, EstadoDocumentoDokify.Desconocido)]
    [InlineData(99, EstadoDocumentoDokify.Desconocido)]
    [InlineData(null, EstadoDocumentoDokify.Desconocido)]
    public void Solo_los_codigos_conocidos_se_traducen_y_el_resto_es_desconocido(int? codigo, EstadoDocumentoDokify esperado)
    {
        DokifyMapeo.EstadoDesdeCodigo(codigo).Should().Be(esperado);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{}")]
    public async Task Un_elemento_sin_documentos_es_una_lista_vacia_aunque_php_serialice_un_mapa_vacio_como_lista(string cuerpo)
    {
        _handler.Responder(HttpStatusCode.OK, cuerpo);

        var resultado = await Crear().ObtenerDocumentosAsync(_credencial, ElementoDokify.Maquina, "MAQ-0001", CancellationToken.None);

        resultado.Fallo.Should().BeNull();
        resultado.Valor.Should().BeEmpty();
        _handler.Peticiones.Single().Uri.Should().Be(new Uri(Base + "machine/MAQ-0001/docs"));
    }

    [Fact]
    public async Task Las_asignaciones_se_mapean_con_el_recuento_de_sus_solicitudes()
    {
        _handler.Responder(HttpStatusCode.OK, """
            [{"uid":77,"description":"x","has_all_documents_valid":2,"group":{"name":"Grupo de prueba","requests_attached":1,
              "requests_validated":4,"requests_expired":"2","requests_rejected":0,"requests_pending":3}},
             {"description":"sin uid"},
             {"uid":78}]
            """);

        var resultado = await Crear().ObtenerAsignacionesAsync(_credencial, ElementoDokify.Empresa, CifPrueba, CancellationToken.None);

        resultado.Valor.Should().Equal(
            new AsignacionDokify("77", "Grupo de prueba", 1, 4, 2, 0, 3),
            new AsignacionDokify("78", string.Empty, null, null, null, null, null));
        _handler.Peticiones.Single().Uri.Should().Be(new Uri(Base + "company/B00000001/assignments"));
    }

    [Fact]
    public async Task La_descarga_decodifica_el_fichero_y_sanea_el_nombre_y_el_tipo()
    {
        var fichero = new byte[] { 0x25, 0x50, 0x44, 0x46, 0x00, 0xFF };
        _handler.Responder(HttpStatusCode.OK,
            $$"""{"type":"Seguro","uploadedAt":"2026-11-01T10:00:00+00:00","expirationAt":"No Caduca","status":"Validado","document":"{{Convert.ToBase64String(fichero)}}","contentType":"application/pdf","fileName":"..\\..\\carpeta/seguro.pdf"}""");

        var resultado = await Crear().DescargarDocumentoAsync(_credencial, ElementoDokify.Trabajador, NifPrueba, "11", CancellationToken.None);

        resultado.Valor.Contenido.Should().Equal(fichero);
        resultado.Valor.TipoContenido.Should().Be("application/pdf");
        resultado.Valor.NombreFichero.Should().Be("seguro.pdf", "del nombre del tercero solo se conserva el último tramo");
        resultado.Valor.ToString().Should().NotContain("seguro");
        _handler.Peticiones.Single().Uri.Should().Be(new Uri(Base + "document/11/employee/TEST0001A"));
    }

    [Theory]
    [InlineData("text/html\r\nX: 1")]
    [InlineData("no-es-un-tipo")]
    [InlineData("")]
    public async Task Un_tipo_de_contenido_que_no_lo_parece_se_sustituye_por_el_generico(string tipo)
    {
        _handler.Responder(HttpStatusCode.OK, $$"""{"document":"AQID","contentType":{{System.Text.Json.JsonSerializer.Serialize(tipo)}},"fileName":""}""");

        var resultado = await Crear().DescargarDocumentoAsync(_credencial, ElementoDokify.Empresa, CifPrueba, "11", CancellationToken.None);

        resultado.Valor.TipoContenido.Should().Be("application/octet-stream");
        resultado.Valor.NombreFichero.Should().Be("documento");
    }

    [Theory]
    [InlineData("""{"document":"esto no es base64!!"}""")]
    [InlineData("""{"document":""}""")]
    [InlineData("""{"fileName":"a.pdf"}""")]
    [InlineData("[]")]
    public async Task Una_descarga_sin_fichero_o_con_un_fichero_que_no_es_base64_es_inesperada(string cuerpo)
    {
        _handler.Responder(HttpStatusCode.OK, cuerpo);

        (await Crear().DescargarDocumentoAsync(_credencial, ElementoDokify.Empresa, CifPrueba, "11", CancellationToken.None))
            .Fallo.Should().Be(MotivoUsoConector.Inesperado);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("a/b")]
    [InlineData("..")]
    [InlineData("a?x=1")]
    [InlineData("a#b")]
    [InlineData("a b")]
    [InlineData("-a")]
    [InlineData("%2e%2e")]
    [InlineData("B00000001\n")]
    public async Task Un_cif_nif_o_matricula_que_no_cabe_en_un_tramo_de_ruta_no_llama(string vat)
    {
        var conector = Crear();

        (await conector.ObtenerDocumentosAsync(_credencial, ElementoDokify.Trabajador, vat, CancellationToken.None))
            .Fallo.Should().Be(MotivoUsoConector.DatosInsuficientes);
        (await conector.ListarTrabajadoresAsync(_credencial, vat, CancellationToken.None))
            .Fallo.Should().Be(MotivoUsoConector.DatosInsuficientes);
        (await conector.DescargarDocumentoAsync(_credencial, ElementoDokify.Empresa, vat, "11", CancellationToken.None))
            .Fallo.Should().Be(MotivoUsoConector.DatosInsuficientes);
        _handler.Peticiones.Should().BeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("11/../12")]
    [InlineData("abc")]
    [InlineData("-1")]
    [InlineData("11\n")]
    public async Task Un_tipo_de_documento_no_numerico_no_llama(string tipo)
    {
        (await Crear().DescargarDocumentoAsync(_credencial, ElementoDokify.Empresa, CifPrueba, tipo, CancellationToken.None))
            .Fallo.Should().Be(MotivoUsoConector.DatosInsuficientes);
        _handler.Peticiones.Should().BeEmpty();
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, MotivoUsoConector.RechazadoPorPlataforma)]
    [InlineData(HttpStatusCode.UnprocessableEntity, MotivoUsoConector.RechazadoPorPlataforma)]
    [InlineData(HttpStatusCode.NotFound, MotivoUsoConector.NoEncontradoEnPlataforma)]
    [InlineData(HttpStatusCode.Unauthorized, MotivoUsoConector.CredencialRechazada)]
    [InlineData(HttpStatusCode.Forbidden, MotivoUsoConector.CredencialRechazada)]
    [InlineData(HttpStatusCode.TooManyRequests, MotivoUsoConector.LimiteSuperado)]
    [InlineData(HttpStatusCode.InternalServerError, MotivoUsoConector.PlataformaNoDisponible)]
    [InlineData(HttpStatusCode.ServiceUnavailable, MotivoUsoConector.PlataformaNoDisponible)]
    [InlineData(HttpStatusCode.Found, MotivoUsoConector.Inesperado)]
    [InlineData(HttpStatusCode.Created, MotivoUsoConector.Inesperado)]
    [InlineData(HttpStatusCode.NoContent, MotivoUsoConector.Inesperado)]
    public async Task Una_respuesta_que_no_es_200_se_traduce_a_un_motivo_sin_el_texto_del_tercero(HttpStatusCode codigo, MotivoUsoConector esperado)
    {
        _handler.Responder(codigo, TextoDelTercero);

        var resultado = await Crear().ObtenerDocumentosAsync(_credencial, ElementoDokify.Trabajador, NifPrueba, CancellationToken.None);

        resultado.Fallo.Should().Be(esperado);
        _logger.Todo.Should().Contain(((int)codigo).ToString(), "el log dice qué código HTTP se recibió");
        _logger.Todo.Should().NotContain(NifPrueba).And.NotContain(CifPrueba).And.NotContain("Employee").And.NotContain(Token);
    }

    [Fact]
    public async Task Un_timeout_es_plataforma_no_disponible_y_no_lanza()
    {
        _handler.Colgar();

        var resultado = await CrearCliente(TimeSpan.FromMilliseconds(200))
            .ObtenerEstadoAsync(_credencial, ElementoDokify.Trabajador, NifPrueba, CancellationToken.None);

        resultado.Fallo.Should().Be(MotivoUsoConector.PlataformaNoDisponible);
        _logger.Todo.Should().NotContain(Token).And.NotContain(NifPrueba);
    }

    [Fact]
    public async Task La_cancelacion_de_quien_llama_se_propaga_para_que_la_puerta_la_registre_como_cancelada()
    {
        _handler.Colgar();
        using var cancelacion = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        var llamada = () => Crear().ListarEmpresasAsync(_credencial, cancelacion.Token);

        await llamada.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Un_fallo_de_red_es_plataforma_no_disponible_sin_copiar_su_mensaje()
    {
        _handler.Fallar(new HttpRequestException(TextoDelTercero));

        var resultado = await CrearCliente().ObtenerDocumentosAsync(_credencial, ElementoDokify.Trabajador, NifPrueba, CancellationToken.None);

        resultado.Fallo.Should().Be(MotivoUsoConector.PlataformaNoDisponible);
        _logger.Todo.Should().NotContain("Employee").And.NotContain(Token).And.NotContain(NifPrueba);
    }

    [Fact]
    public async Task Un_cuerpo_ilegible_es_inesperado_y_el_log_no_lleva_ni_el_cuerpo_ni_la_ruta()
    {
        _handler.Responder(HttpStatusCode.OK, "{" + TextoDelTercero);

        var resultado = await CrearCliente().ObtenerDocumentosAsync(_credencial, ElementoDokify.Trabajador, NifPrueba, CancellationToken.None);

        resultado.Fallo.Should().Be(MotivoUsoConector.Inesperado);
        _logger.Todo.Should().Contain("Json").And.NotContain("Employee").And.NotContain(NifPrueba).And.NotContain(Token);
    }

    [Theory]
    [InlineData("token\r\nX-Inyectada: 1")]
    [InlineData("token con espacios")]
    [InlineData("")]
    public async Task Un_token_que_no_cabe_en_una_cabecera_se_rechaza_sin_llamar_y_sin_excepcion_que_lo_copie(string token)
    {
        var resultado = await CrearCliente().ObtenerDocumentosAsync(new CredencialApiConector(token), ElementoDokify.Empresa, CifPrueba, CancellationToken.None);

        resultado.Fallo.Should().Be(MotivoUsoConector.SinCredencial, "la plataforma no llegó a ver la llamada");
        _handler.Peticiones.Should().BeEmpty();
    }

    // El tope de longitud de cabecera es 4096: 4096 sale, 4097 no, y en ese caso el token no llega al handler ni al log.
    [Theory]
    [InlineData(4096, true)]
    [InlineData(4097, false)]
    public async Task Un_token_mas_largo_que_el_tope_de_cabecera_se_rechaza_sin_llamar(int longitud, bool sale)
    {
        var token = new string('k', longitud);
        _handler.Responder(HttpStatusCode.OK, "[]");

        var resultado = await CrearCliente().ObtenerAsignacionesAsync(new CredencialApiConector(token), ElementoDokify.Empresa, CifPrueba, CancellationToken.None);

        if (sale)
        {
            resultado.Fallo.Should().BeNull();
            _handler.Peticiones.Should().ContainSingle();
            return;
        }

        resultado.Fallo.Should().Be(MotivoUsoConector.SinCredencial);
        _handler.Peticiones.Should().BeEmpty();
        _logger.Todo.Should().NotContain(token);
    }

    [Theory]
    [InlineData("Bearer", "Bearer " + Token)]
    [InlineData("", Token)]
    public async Task El_esquema_de_autorizacion_es_configurable_hasta_que_dokify_confirme_el_suyo(string esquema, string esperado)
    {
        _handler.Responder(HttpStatusCode.OK, "[]");

        await CrearCliente(opciones: new DokifyOptions { EsquemaAutorizacion = esquema })
            .ObtenerAsignacionesAsync(_credencial, ElementoDokify.Empresa, CifPrueba, CancellationToken.None);

        _handler.Peticiones.Single().Cabeceras["Authorization"].Should().ContainSingle().Which.Should().Be(esperado);
    }

    [Fact]
    public void El_manejador_real_no_sigue_redirecciones_ni_guarda_cookies_entre_conexiones()
    {
        using var manejador = DokifyClient.CrearManejadorSinRedirecciones();

        manejador.AllowAutoRedirect.Should().BeFalse("la ruta lleva el CIF o NIF y no hay redirecciones legítimas");
        manejador.UseCookies.Should().BeFalse("el manejador del pool se comparte entre las conexiones de todos los Tenants");
    }

    [Fact]
    public async Task Por_la_puerta_hoy_ninguna_operacion_llama_y_la_escritura_no_existe()
    {
        var conector = Crear();
        using var contenido = new MemoryStream([1, 2, 3]);

        var lectura = await conector.LeerEstadoDocumentoAsync(_credencial, new SolicitudLecturaEstado(Guid.NewGuid(), Guid.NewGuid()), CancellationToken.None);
        var alta = await conector.DarDeAltaTrabajadorAsync(
            _credencial, new SolicitudAltaTrabajador(Guid.NewGuid(), "Nombre", "Apellido", NifPrueba, Guid.NewGuid().ToString("D")), CancellationToken.None);
        var subida = await conector.SubirDocumentoAsync(
            _credencial, new SolicitudSubidaDocumento(Guid.NewGuid(), Guid.NewGuid(), contenido, Guid.NewGuid().ToString("D")), CancellationToken.None);

        lectura.Fallo.Should().Be(MotivoUsoConector.DatosInsuficientes);
        alta.Fallo.Should().Be(MotivoUsoConector.SinConector);
        subida.Fallo.Should().Be(MotivoUsoConector.SinConector);
        _handler.Peticiones.Should().BeEmpty();

        IConectorPlataformaCae puerto = conector;
        puerto.AdmiteOperacion(OperacionConectorPlataformaCae.LeerEstadoDocumento).Should().BeTrue();
        puerto.AdmiteOperacion(OperacionConectorPlataformaCae.DarDeAltaTrabajador).Should().BeFalse(
            "la puerta deniega el alta como SinConector antes de abrir un uso");
        puerto.AdmiteOperacion(OperacionConectorPlataformaCae.SubirDocumento).Should().BeFalse();
    }

    [Fact]
    public async Task Cada_peticion_lleva_la_marca_de_ruta_con_identificadores()
    {
        var marcadas = new List<bool>();
        var enAmbito = new List<bool>();
        var cliente = new DokifyClient(
            new HttpClient(new HandlerQueMira(p =>
            {
                marcadas.Add(PeticionConIdentificadoresEnLaRuta.Lleva(p));
                enAmbito.Add(PeticionConIdentificadoresEnLaRuta.EnviandoAhora);
            })),
            Options.Create(new DokifyOptions()), _logger);

        await cliente.ObtenerDocumentosAsync(_credencial, ElementoDokify.Trabajador, NifPrueba, CancellationToken.None);
        await cliente.ListarEmpresasAsync(_credencial, 0, 50, CancellationToken.None);

        marcadas.Should().Equal([true, true], "OpenTelemetry y Sentry dejan de exportar la ruta por esta marca");
        enAmbito.Should().Equal([true, true], "el manejador de Sentry, por fuera del pipeline, ve el ámbito del envío");
        PeticionConIdentificadoresEnLaRuta.EnviandoAhora.Should().BeFalse("el ámbito no sale del envío");
        PeticionConIdentificadoresEnLaRuta.Lleva(new HttpRequestMessage(HttpMethod.Get, Base)).Should().BeFalse(
            "control: una petición sin marcar no la lleva");
    }

    [Fact]
    public async Task Una_respuesta_mas_grande_que_el_tope_real_es_inesperada_y_no_plataforma_no_disponible()
    {
        _handler.Responder(HttpStatusCode.OK, new string('a', (int)DokifyClient.TamanoMaximoRespuesta + 1));
        var cliente = new DokifyClient(
            new HttpClient(_handler) { MaxResponseContentBufferSize = DokifyClient.TamanoMaximoRespuesta },
            Options.Create(new DokifyOptions()), _logger);

        var resultado = await cliente.ObtenerDocumentosAsync(_credencial, ElementoDokify.Empresa, CifPrueba, CancellationToken.None);

        resultado.Fallo.Should().Be(MotivoUsoConector.Inesperado, "dokify contestó: no es una plataforma caída");
        _logger.Todo.Should().Contain("más grande que el tope").And.NotContain(CifPrueba);
    }

    [Fact]
    public async Task Una_pagina_con_mas_filas_de_las_pedidas_es_inesperada()
    {
        var filas = string.Join(',', Enumerable.Range(0, DokifyClient.TamanoMaximoPagina + 1).Select(i => $$"""{"vat":"B{{i:D8}}","name":"E"}"""));
        _handler.Responder(HttpStatusCode.OK, Pagina(0, 1, filas));

        var resultado = await Crear().ListarEmpresasAsync(_credencial, CancellationToken.None);

        resultado.Fallo.Should().Be(MotivoUsoConector.Inesperado);
    }

    [Fact]
    public async Task Una_pagina_con_las_filas_justas_se_acepta()
    {
        var filas = string.Join(',', Enumerable.Range(0, DokifyClient.TamanoMaximoPagina).Select(i => $$"""{"vat":"B{{i:D8}}","name":"E"}"""));
        _handler.Responder(HttpStatusCode.OK, Pagina(0, 1, filas));

        var resultado = await Crear().ListarEmpresasAsync(_credencial, CancellationToken.None);

        resultado.Valor.Should().HaveCount(DokifyClient.TamanoMaximoPagina);
    }

    [Fact]
    public async Task Un_listado_que_pasa_de_su_plazo_total_es_plataforma_no_disponible()
    {
        _handler.Colgar();
        // Sin Timeout por página: solo el plazo total puede cortar, así que sin él la prueba no acaba (y cae por el tope
        // de abajo, no por el Timeout del HttpClient, que también daría PlataformaNoDisponible).
        var conector = new ConectorDokify(CrearCliente(Timeout.InfiniteTimeSpan)) { PlazoTotalDeListado = TimeSpan.FromMilliseconds(200) };

        var listado = conector.ListarTrabajadoresAsync(_credencial, CifPrueba, CancellationToken.None);
        var primero = await Task.WhenAny(listado, Task.Delay(TimeSpan.FromSeconds(20)));

        primero.Should().BeSameAs(listado, "el plazo total corta el listado colgado");
        (await listado).Fallo.Should().Be(MotivoUsoConector.PlataformaNoDisponible);
        _logger.Todo.Should().NotContain("no respondió a tiempo", "no lo cortó el Timeout por página");
        ConectorDokify.PlazoTotalDeListadoPorDefecto.Should().BeLessThan(TimeSpan.FromMinutes(10));
    }

    [Fact]
    public async Task La_cancelacion_de_quien_llama_durante_un_listado_se_propaga()
    {
        _handler.Colgar();
        using var cancelacion = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        var llamada = () => Crear(TimeSpan.FromSeconds(30)).ListarEmpresasAsync(_credencial, cancelacion.Token);

        await llamada.Should().ThrowAsync<OperationCanceledException>();
    }

    [Theory]
    [InlineData("""{"11":{"name":"Seguro","requests":{"501":{"uid":501,"status_code":2}}}}""")]
    [InlineData("""[{"name":"Seguro","requests":[{"uid":501,"status_code":2}]}]""")]
    [InlineData("""{"11":{"uid":11,"name":"Seguro","requests":{"501":{"status_code":2}}}}""")]
    [InlineData("""[{"uid":11,"name":"Seguro","requests":[{"status_code":2}]}]""")]
    public async Task Un_tipo_o_una_solicitud_sin_uid_hace_inesperados_los_documentos_y_no_usa_la_clave_ni_el_indice(string cuerpo)
    {
        _handler.Responder(HttpStatusCode.OK, cuerpo);

        var resultado = await Crear().ObtenerDocumentosAsync(_credencial, ElementoDokify.Trabajador, NifPrueba, CancellationToken.None);

        resultado.Fallo.Should().Be(MotivoUsoConector.Inesperado);
    }

    [Theory]
    [InlineData("a\r\nb.pdf", "a__b.pdf")]
    [InlineData("fac\u202Efdp.exe", "fac_fdp.exe")]
    [InlineData("c:\\x\\a:b*c\"d.pdf", "a_b_c_d.pdf")]
    [InlineData("tab\tnul\u0000.pdf", "tab_nul_.pdf")]
    [InlineData("..", "documento")]
    [InlineData(" .oculto. ", "oculto")]
    [InlineData("Señalización (copia)-1_v2.pdf", "Señalización (copia)-1_v2.pdf")]
    public async Task El_nombre_del_fichero_se_sanea_con_una_lista_explicita_en_cualquier_sistema(string nombre, string esperado)
    {
        _handler.Responder(HttpStatusCode.OK, $$"""{"document":"AQID","contentType":"application/pdf","fileName":{{System.Text.Json.JsonSerializer.Serialize(nombre)}}}""");

        var resultado = await Crear().DescargarDocumentoAsync(_credencial, ElementoDokify.Empresa, CifPrueba, "11", CancellationToken.None);

        resultado.Valor.NombreFichero.Should().Be(esperado);
    }

    [Theory]
    [InlineData("text/html", "application/octet-stream")]
    [InlineData("image/svg+xml", "application/octet-stream")]
    [InlineData("application/xhtml+xml", "application/octet-stream")]
    [InlineData("application/pdf; charset=binary", "application/octet-stream")]
    [InlineData("Application/PDF", "application/pdf")]
    [InlineData("image/png", "image/png")]
    public async Task Solo_pasan_los_tipos_de_contenido_de_la_lista(string tipo, string esperado)
    {
        _handler.Responder(HttpStatusCode.OK, $$"""{"document":"AQID","contentType":{{System.Text.Json.JsonSerializer.Serialize(tipo)}},"fileName":"a.pdf"}""");

        var resultado = await Crear().DescargarDocumentoAsync(_credencial, ElementoDokify.Empresa, CifPrueba, "11", CancellationToken.None);

        resultado.Valor.TipoContenido.Should().Be(esperado);
    }

    [Fact]
    public void La_maquina_no_escribe_su_matricula_ni_su_numero_de_serie()
    {
        new MaquinaDokify("MAQ-0001", "Maquina", "M-1", "Fabricante", "SN-0001").ToString()
            .Should().NotContain("MAQ-0001").And.NotContain("SN-0001");
    }

    private sealed class HandlerQueMira(Action<HttpRequestMessage> mirar) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            mirar(request);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
        }
    }

    [Fact]
    public async Task Todas_las_lecturas_son_get_y_ninguna_lleva_cuerpo()
    {
        _handler.Responder(HttpStatusCode.OK, "[]");
        var conector = Crear();

        await conector.ObtenerDocumentosAsync(_credencial, ElementoDokify.Empresa, CifPrueba, CancellationToken.None);
        await conector.ObtenerAsignacionesAsync(_credencial, ElementoDokify.Maquina, "MAQ-0001", CancellationToken.None);
        await conector.ObtenerEstadoAsync(_credencial, ElementoDokify.Trabajador, NifPrueba, CancellationToken.None);
        await conector.DescargarDocumentoAsync(_credencial, ElementoDokify.Empresa, CifPrueba, "11", CancellationToken.None);

        _handler.Peticiones.Should().HaveCount(4).And.OnlyContain(p => p.Metodo == HttpMethod.Get && p.Cuerpo.Length == 0);
    }

    [Theory]
    [InlineData("https://app.dokify.net/app/", 30, "Bearer", true)]
    [InlineData("https://app.dokify.net/app/", 30, "", true)]
    [InlineData("http://app.dokify.net/app/", 30, "Bearer", false)]
    [InlineData("https://app.dokify.net/app", 30, "Bearer", false)]
    [InlineData("https://app.dokify.net/app/?token=x", 30, "Bearer", false)]
    [InlineData("https://usuario:clave@app.dokify.net/app/", 30, "Bearer", false)]
    [InlineData("https://app.dokify.net/app/", 0, "Bearer", false)]
    [InlineData("https://app.dokify.net/app/", 121, "Bearer", false)]
    [InlineData("https://app.dokify.net/app/", 30, "Bearer\r\nX: 1", false)]
    public void Las_opciones_solo_aceptan_https_absoluto_acabado_en_barra(string url, int timeout, string esquema, bool valida)
    {
        new DokifyOptions { UrlBase = url, TimeoutSegundos = timeout, EsquemaAutorizacion = esquema }.EsValida().Should().Be(valida);
    }

    [Fact]
    public void Las_opciones_por_defecto_son_validas_y_apuntan_a_la_api_publica()
    {
        var opciones = new DokifyOptions();

        opciones.EsValida().Should().BeTrue();
        opciones.UrlBase.Should().Be(Base);
    }

    private sealed record PeticionVista(HttpMethod Metodo, Uri Uri, Dictionary<string, string[]> Cabeceras, string Cuerpo);

    private sealed class HandlerFalso : HttpMessageHandler
    {
        private Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _respuesta =
            (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));

        public List<PeticionVista> Peticiones { get; } = [];

        public void Responder(HttpStatusCode codigo, string cuerpo) => Responder(_ => (codigo, cuerpo));

        /// <summary>Responde según el número de página que lleve la petición (0 si no lleva).</summary>
        public void Responder(Func<int, (HttpStatusCode Codigo, string Cuerpo)> segunPagina) =>
            _respuesta = (peticion, _) =>
            {
                var consulta = System.Web.HttpUtility.ParseQueryString(peticion.RequestUri!.Query);
                var (codigo, cuerpo) = segunPagina(int.TryParse(consulta["page"], out var p) ? p : 0);
                return Task.FromResult(new HttpResponseMessage(codigo)
                {
                    Content = new StringContent(cuerpo, Encoding.UTF8, "application/json"),
                });
            };

        public void Colgar() =>
            _respuesta = async (_, ct) =>
            {
                await Task.Delay(Timeout.Infinite, ct);
                return new HttpResponseMessage(HttpStatusCode.OK);
            };

        public void Fallar(Exception excepcion) => _respuesta = (_, _) => throw excepcion;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var cuerpo = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            Peticiones.Add(new PeticionVista(
                request.Method,
                request.RequestUri!,
                request.Headers.ToDictionary(h => h.Key, h => h.Value.ToArray(), StringComparer.OrdinalIgnoreCase),
                cuerpo));
            return await _respuesta(request, cancellationToken);
        }
    }

    private sealed class LoggerQueCaptura : ILogger<DokifyClient>
    {
        private readonly List<string> _entradas = [];

        public string Todo => string.Join('\n', _entradas);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            _entradas.Add(formatter(state, exception));
            if (exception is not null)
                _entradas.Add(exception.ToString());

            if (state is IEnumerable<KeyValuePair<string, object?>> valores)
                _entradas.AddRange(valores.Select(v => $"{v.Key}={v.Value}"));
        }
    }
}
