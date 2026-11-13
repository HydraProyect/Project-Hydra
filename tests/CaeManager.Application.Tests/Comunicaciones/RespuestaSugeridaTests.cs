using CaeManager.Application.Common;
using CaeManager.Application.Comunicaciones;
using CaeManager.Application.Comunicaciones.Commands.GenerarRespuestaSugerida;
using CaeManager.Application.Comunicaciones.Commands.ResolverRespuestaSugerida;
using CaeManager.Application.Comunicaciones.Queries.ObtenerRespuestaSugerida;
using CaeManager.Application.Cumplimiento;
using CaeManager.Application.Plataforma;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Common;
using CaeManager.Application.Tests.Configuracion;
using CaeManager.Application.Tests.ContratosAcceso;
using CaeManager.Application.Tests.Documentos;
using CaeManager.Application.Tests.Integraciones;
using CaeManager.Application.Tests.Plantillas;
using CaeManager.Application.Tests.Reclamaciones;
using CaeManager.Application.Tests.Reportes;
using CaeManager.Application.Tests.TiposDocumento;
using CaeManager.Application.Visitas;
using CaeManager.Domain.Common;
using CaeManager.Domain.Comunicaciones;
using CaeManager.Domain.Plataforma;
using CaeManager.Domain.Visitas;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using System.Data.Common;

namespace CaeManager.Application.Tests.Comunicaciones;

public class RespuestaSugeridaTests
{
    private readonly Guid _usuarioId = Guid.NewGuid();
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly ComunicacionesQueryContextFalso _comunicaciones = new();
    private readonly RespuestasFalsas _respuestas;
    private readonly RedaccionFalsa _redaccion = new();
    private readonly InstruccionTratamientoFalsa _instruccion = new();
    private readonly UnitOfWorkFalso _unidad = new();
    private readonly TransaccionDeComandoFalsa _transaccion = new();
    private readonly DescarteCambiosPendientesFalso _descarte = new();

    public RespuestaSugeridaTests() => _respuestas = new RespuestasFalsas(_comunicaciones);

    private GenerarRespuestaSugeridaCommandHandler Generar(
        AlcanceDatosServiceFalso? alcance = null, Guid? usuarioId = null, bool sinUsuario = false, Guid? tenantId = null, bool sinTenant = false)
    {
        alcance ??= new AlcanceDatosServiceFalso();
        var empresas = new EmpresasQueryContextFalso();
        var centros = new CentrosQueryContextFalso();
        var trabajadores = new TrabajadoresQueryContextFalso();
        var relleno = new RellenoMacroRespuesta(
            _comunicaciones, empresas, centros, trabajadores, new VisitasVacias(),
            PendientesDeReclamacionPruebas.Crear(
                new DocumentosQueryContextFalso(), new TiposDocumentoQueryContextFalso(), trabajadores,
                new AsignacionesQueryContextFalso(), centros, empresas, alcance),
            alcance);
        return new GenerarRespuestaSugeridaCommandHandler(
            _comunicaciones, _respuestas, relleno, _redaccion, _instruccion,
            new TenantActualFijo(sinTenant ? null : tenantId ?? _tenantId), alcance,
            new CurrentUserServiceFalso(sinUsuario ? null : usuarioId ?? _usuarioId),
            _transaccion, _unidad, _descarte);
    }

    private ResolverRespuestaSugeridaCommandHandler Resolver(AlcanceDatosServiceFalso? alcance = null, bool sinUsuario = false) => new(
        _comunicaciones, _respuestas, alcance ?? new AlcanceDatosServiceFalso(),
        new CurrentUserServiceFalso(sinUsuario ? null : _usuarioId), _unidad);

    private ObtenerRespuestaSugeridaQueryHandler Obtener(bool puedeLeer = true, AlcanceDatosServiceFalso? alcance = null, bool sesionPrivilegiada = false) => new(
        _comunicaciones, alcance ?? new AlcanceDatosServiceFalso(), new PoliticaNotaInternaFalsa(puedeLeer), new SesionFalsa(sesionPrivilegiada));

    private Conversacion Hilo(Guid? clienteId = null, bool anclada = true, CanalConversacion canal = CanalConversacion.Correo)
    {
        var conversacion = canal == CanalConversacion.WhatsApp
            ? Conversacion.CrearWhatsApp("+34600111222", Guid.NewGuid(), anclada ? clienteId ?? Guid.NewGuid() : null, Guid.NewGuid())
            : new Conversacion("Documentación", anclada ? clienteId ?? Guid.NewGuid() : null);
        _comunicaciones.ListaConversaciones.Add(conversacion);
        Entrante(conversacion);
        return conversacion;
    }

    private Mensaje Entrante(Conversacion conversacion, DateTime? fecha = null, bool esAutomatico = false)
    {
        var mensaje = conversacion.AgregarMensaje(
            DireccionMensaje.Entrante, conversacion.Canal, "ana@contrata.test", "<p>Adjunto la documentación</p>",
            fecha ?? DateTime.UtcNow.AddHours(-1), esAutomatico: esAutomatico);
        _comunicaciones.ListaMensajes.Add(mensaje);
        return mensaje;
    }

    private void Saliente(Conversacion conversacion, bool esAutomatico = false)
    {
        var mensaje = conversacion.AgregarMensaje(
            DireccionMensaje.Saliente, conversacion.Canal, "cae@empresa.test", "<p>Recibido</p>", DateTime.UtcNow, esAutomatico: esAutomatico);
        _comunicaciones.ListaMensajes.Add(mensaje);
    }

    private MacroRespuesta Macro(string cuerpo = "<p>Recibida, gracias.</p>", Guid? clienteId = null)
    {
        var macro = new MacroRespuesta("Acuse", cuerpo, clienteId);
        _comunicaciones.ListaMacrosRespuesta.Add(macro);
        return macro;
    }

    private RespuestaSugerida Sugerencia(
        Conversacion conversacion, EstadoRespuestaSugerida estado = EstadoRespuestaSugerida.Propuesta, Mensaje? origen = null, Guid? macroId = null)
    {
        var origenId = (origen ?? conversacion.Mensajes.First()).Id;
        var respuesta = macroId is { } macro
            ? RespuestaSugerida.DesdeMacro(conversacion.Id, origenId, macro, "<p>Borrador</p>", Guid.NewGuid(), DateTime.UtcNow)
            : RespuestaSugerida.DeVega(conversacion.Id, origenId, "<p>Borrador</p>", Guid.NewGuid(), DateTime.UtcNow);
        if (estado != EstadoRespuestaSugerida.Propuesta)
            respuesta.Insertar();
        _respuestas.Agregar(respuesta);
        return respuesta;
    }

    [Fact]
    public async Task Sin_macro_redacta_Vega_escapada_y_queda_propuesta_sobre_el_ultimo_entrante()
    {
        var conversacion = Hilo();
        var ultimo = Entrante(conversacion, DateTime.UtcNow);
        _redaccion.Respuesta = Result.Exito("Hola Ana:\n\nRecibido <script>\ncon gracias.");

        var resultado = await Generar().Handle(new(conversacion.Id), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        var respuesta = _comunicaciones.ListaRespuestasSugeridas.Should().ContainSingle().Which;
        respuesta.Id.Should().Be(resultado.Valor);
        respuesta.Origen.Should().Be(OrigenRespuestaSugerida.Vega);
        respuesta.Estado.Should().Be(EstadoRespuestaSugerida.Propuesta);
        respuesta.MensajeOrigenId.Should().Be(ultimo.Id);
        respuesta.GeneradaPorUsuarioId.Should().Be(_usuarioId);
        respuesta.Texto.Should().Be("<p>Hola Ana:</p><p>Recibido &lt;script&gt;<br>con gracias.</p>");
        _redaccion.Llamadas.Should().ContainSingle().Which.Should().Be(("Documentación", ultimo.CuerpoHtml));
    }

    [Fact]
    public async Task En_WhatsApp_el_texto_de_Vega_queda_plano()
    {
        var conversacion = Hilo(canal: CanalConversacion.WhatsApp);
        _redaccion.Respuesta = Result.Exito("Recibido, gracias.");

        await Generar().Handle(new(conversacion.Id), CancellationToken.None);

        _comunicaciones.ListaRespuestasSugeridas.Should().ContainSingle().Which.Texto.Should().Be("Recibido, gracias.");
    }

    [Fact]
    public async Task Con_macro_rellena_sin_llamar_a_la_IA_ni_consultar_la_instruccion()
    {
        var conversacion = Hilo();
        var macro = Macro();
        _instruccion.Habilitada = false;

        var resultado = await Generar().Handle(new(conversacion.Id, macro.Id), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        var respuesta = _comunicaciones.ListaRespuestasSugeridas.Should().ContainSingle().Which;
        respuesta.Origen.Should().Be(OrigenRespuestaSugerida.Macro);
        respuesta.MacroId.Should().Be(macro.Id);
        respuesta.Texto.Should().Be(macro.CuerpoHtml);
        _redaccion.Llamadas.Should().BeEmpty();
    }

    [Fact]
    public async Task Una_variable_sin_resolver_se_queda_en_el_texto()
    {
        var conversacion = Hilo();
        var macro = Macro("<p>Hola {{Trabajador}}</p>");

        await Generar().Handle(new(conversacion.Id, macro.Id), CancellationToken.None);

        _comunicaciones.ListaRespuestasSugeridas.Should().ContainSingle().Which.Texto.Should().Contain("{{Trabajador}}");
    }

    [Fact]
    public async Task Una_macro_de_otro_Cliente_empresarial_no_se_rellena()
    {
        var conversacion = Hilo();
        var macro = Macro(clienteId: Guid.NewGuid());

        var resultado = await Generar().Handle(new(conversacion.Id, macro.Id), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("Macro.NoEncontrada");
        _comunicaciones.ListaRespuestasSugeridas.Should().BeEmpty();
    }

    [Fact]
    public async Task Fuera_de_alcance_no_se_genera_ni_se_llama_a_la_IA()
    {
        var conversacion = Hilo();

        var resultado = await Generar(new AlcanceDatosServiceFalso(tieneAccesoTotal: false)).Handle(new(conversacion.Id), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("Conversacion.NoEncontrada");
        _redaccion.Llamadas.Should().BeEmpty();
        _comunicaciones.ListaRespuestasSugeridas.Should().BeEmpty();
    }

    [Fact]
    public async Task Sin_autor_no_se_genera()
    {
        var conversacion = Hilo();

        var resultado = await Generar(sinUsuario: true).Handle(new(conversacion.Id), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("Sugerencia.SinAutor");
        _redaccion.Llamadas.Should().BeEmpty();
    }

    [Theory]
    [InlineData(EstadoConversacion.Resuelta)]
    [InlineData(EstadoConversacion.Cerrada)]
    public async Task Una_conversacion_resuelta_o_cerrada_no_admite_sugerencia(EstadoConversacion estado)
    {
        var conversacion = Hilo();
        conversacion.CambiarEstado(estado);

        var resultado = await Generar().Handle(new(conversacion.Id), CancellationToken.None);

        resultado.Error.Should().Be(GenerarRespuestaSugeridaCommandHandler.NoProcede);
        _redaccion.Llamadas.Should().BeEmpty();
    }

    [Fact]
    public async Task Si_el_ultimo_mensaje_es_saliente_no_procede()
    {
        var conversacion = Hilo();
        Saliente(conversacion);

        var resultado = await Generar().Handle(new(conversacion.Id), CancellationToken.None);

        resultado.Error.Should().Be(GenerarRespuestaSugeridaCommandHandler.NoProcede);
        _comunicaciones.ListaRespuestasSugeridas.Should().BeEmpty();
    }

    [Fact]
    public async Task Sin_instruccion_de_tratamiento_no_se_llama_a_la_IA()
    {
        var conversacion = Hilo();
        _instruccion.Habilitada = false;

        var resultado = await Generar().Handle(new(conversacion.Id), CancellationToken.None);

        resultado.Error.Should().Be(GenerarRespuestaSugeridaCommandHandler.NoDisponible);
        _redaccion.Llamadas.Should().BeEmpty();
        _comunicaciones.ListaRespuestasSugeridas.Should().BeEmpty();
    }

    [Fact]
    public async Task Sin_Tenant_activo_no_se_llama_a_la_IA()
    {
        var conversacion = Hilo();

        var resultado = await Generar(sinTenant: true).Handle(new(conversacion.Id), CancellationToken.None);

        resultado.Error.Should().Be(GenerarRespuestaSugeridaCommandHandler.NoDisponible);
        _redaccion.Llamadas.Should().BeEmpty();
    }

    [Fact]
    public async Task Un_hilo_sin_ancla_no_sale_al_proveedor()
    {
        var conversacion = Hilo(anclada: false);

        var resultado = await Generar().Handle(new(conversacion.Id), CancellationToken.None);

        resultado.Error.Should().Be(GenerarRespuestaSugeridaCommandHandler.NoDisponible);
        _redaccion.Llamadas.Should().BeEmpty();
    }

    [Fact]
    public async Task Un_fallo_de_la_IA_no_persiste_y_conserva_la_sugerencia_anterior()
    {
        var conversacion = Hilo();
        var anterior = Sugerencia(conversacion);
        Entrante(conversacion, DateTime.UtcNow);
        _redaccion.Respuesta = Result.Fallo<string>(Error.Crear("Otro.Error", "fallo"));

        var resultado = await Generar().Handle(new(conversacion.Id), CancellationToken.None);

        resultado.Error.Should().Be(GenerarRespuestaSugeridaCommandHandler.NoDisponible);
        anterior.Estado.Should().Be(EstadoRespuestaSugerida.Propuesta);
        _comunicaciones.ListaRespuestasSugeridas.Should().ContainSingle();
        _unidad.VecesGuardado.Should().Be(0);
        _transaccion.Ejecutadas.Should().Be(0);
    }

    [Fact]
    public async Task Generar_otra_sustituye_la_propuesta_de_un_mensaje_anterior_en_la_misma_transaccion()
    {
        var conversacion = Hilo();
        var anterior = Sugerencia(conversacion);
        var nuevoEntrante = Entrante(conversacion, DateTime.UtcNow);

        var resultado = await Generar().Handle(new(conversacion.Id), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        anterior.Estado.Should().Be(EstadoRespuestaSugerida.Descartada);
        var activa = _comunicaciones.ListaRespuestasSugeridas.Where(r => r.EstaActiva).Should().ContainSingle().Subject;
        activa.Id.Should().Be(resultado.Valor);
        activa.MensajeOrigenId.Should().Be(nuevoEntrante.Id);
        _transaccion.Ejecutadas.Should().Be(1);
        _unidad.VecesGuardado.Should().Be(2);
    }

    [Fact]
    public async Task Una_sugerencia_sin_revisar_no_se_sustituye_ni_se_llama_a_la_IA()
    {
        var conversacion = Hilo();
        var anterior = Sugerencia(conversacion, EstadoRespuestaSugerida.SinRevisar);
        Entrante(conversacion, DateTime.UtcNow);

        var resultado = await Generar().Handle(new(conversacion.Id), CancellationToken.None);

        resultado.Error.Should().Be(GenerarRespuestaSugeridaCommandHandler.PendienteDeRevisar);
        anterior.Estado.Should().Be(EstadoRespuestaSugerida.SinRevisar);
        _redaccion.Llamadas.Should().BeEmpty();
        _transaccion.Ejecutadas.Should().Be(0);
    }

    [Fact]
    public async Task Con_una_propuesta_del_mismo_mensaje_la_devuelve_sin_llamar_a_la_IA()
    {
        var conversacion = Hilo();
        var existente = Sugerencia(conversacion);

        var resultado = await Generar().Handle(new(conversacion.Id), CancellationToken.None);

        resultado.Valor.Should().Be(existente.Id);
        existente.Estado.Should().Be(EstadoRespuestaSugerida.Propuesta);
        _redaccion.Llamadas.Should().BeEmpty();
        _unidad.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Vega_no_redacta_si_hay_una_propuesta_de_macro_del_mismo_mensaje()
    {
        var conversacion = Hilo();
        var existente = Sugerencia(conversacion, macroId: Macro().Id);

        var resultado = await Generar().Handle(new(conversacion.Id), CancellationToken.None);

        resultado.Valor.Should().Be(existente.Id);
        _redaccion.Llamadas.Should().BeEmpty();
    }

    [Fact]
    public async Task La_misma_macro_devuelve_su_propuesta_y_otra_macro_la_sustituye()
    {
        var conversacion = Hilo();
        var primera = Macro();
        var existente = Sugerencia(conversacion, macroId: primera.Id);

        (await Generar().Handle(new(conversacion.Id, primera.Id), CancellationToken.None)).Valor.Should().Be(existente.Id);

        var otra = await Generar().Handle(new(conversacion.Id, Macro().Id), CancellationToken.None);

        otra.Valor.Should().NotBe(existente.Id);
        existente.Estado.Should().Be(EstadoRespuestaSugerida.Descartada);
    }

    [Fact]
    public async Task Si_mientras_Vega_redacta_alguien_inserta_la_propuesta_no_se_descarta()
    {
        var conversacion = Hilo();
        var existente = Sugerencia(conversacion);
        Entrante(conversacion, DateTime.UtcNow);
        _redaccion.AlRedactar = () => existente.Insertar();

        var resultado = await Generar().Handle(new(conversacion.Id), CancellationToken.None);

        resultado.Error.Should().Be(GenerarRespuestaSugeridaCommandHandler.PendienteDeRevisar);
        existente.Estado.Should().Be(EstadoRespuestaSugerida.SinRevisar);
        _comunicaciones.ListaRespuestasSugeridas.Should().ContainSingle();
    }

    [Theory]
    [InlineData(EstadoConversacion.Resuelta)]
    [InlineData(EstadoConversacion.Cerrada)]
    public async Task Si_mientras_Vega_redacta_la_conversacion_se_cierra_no_se_guarda_nada(EstadoConversacion estado)
    {
        var conversacion = Hilo();
        var existente = Sugerencia(conversacion);
        Entrante(conversacion, DateTime.UtcNow);
        _redaccion.AlRedactar = () => conversacion.CambiarEstado(estado);

        var resultado = await Generar().Handle(new(conversacion.Id), CancellationToken.None);

        resultado.Error.Should().Be(GenerarRespuestaSugeridaCommandHandler.NoProcede);
        _comunicaciones.ListaRespuestasSugeridas.Should().ContainSingle().Which.Should().BeSameAs(existente);
        existente.Estado.Should().Be(EstadoRespuestaSugerida.Propuesta);
    }

    [Fact]
    public async Task Si_mientras_Vega_redacta_alguien_responde_no_se_guarda_nada()
    {
        var conversacion = Hilo();
        _redaccion.AlRedactar = () => Saliente(conversacion);

        var resultado = await Generar().Handle(new(conversacion.Id), CancellationToken.None);

        resultado.Error.Should().Be(GenerarRespuestaSugeridaCommandHandler.NoProcede);
        _comunicaciones.ListaRespuestasSugeridas.Should().BeEmpty();
    }

    [Fact]
    public async Task Si_mientras_Vega_redacta_llega_otro_entrante_no_se_guarda_nada()
    {
        var conversacion = Hilo();
        _redaccion.AlRedactar = () => Entrante(conversacion, DateTime.UtcNow);

        var resultado = await Generar().Handle(new(conversacion.Id), CancellationToken.None);

        resultado.Error.Should().Be(GenerarRespuestaSugeridaCommandHandler.NoProcede);
        _comunicaciones.ListaRespuestasSugeridas.Should().BeEmpty();
    }

    [Fact]
    public async Task Si_mientras_Vega_redacta_llega_otro_entrante_con_la_misma_fecha_no_se_guarda_nada()
    {
        var conversacion = Hilo();
        var fecha = conversacion.Mensajes.Single().FechaUtc;
        _redaccion.AlRedactar = () => Entrante(conversacion, fecha);

        var resultado = await Generar().Handle(new(conversacion.Id), CancellationToken.None);

        resultado.Error.Should().Be(GenerarRespuestaSugeridaCommandHandler.NoProcede);
        _comunicaciones.ListaRespuestasSugeridas.Should().BeEmpty();
    }

    [Fact]
    public async Task Con_la_misma_fecha_una_salida_cuenta_como_ultimo_mensaje()
    {
        var conversacion = Hilo();
        var fecha = conversacion.Mensajes.Single().FechaUtc;
        var salida = conversacion.AgregarMensaje(
            DireccionMensaje.Saliente, conversacion.Canal, "cae@empresa.test", "<p>Recibido</p>", fecha);
        _comunicaciones.ListaMensajes.Add(salida);

        var resultado = await Generar().Handle(new(conversacion.Id), CancellationToken.None);

        resultado.Error.Should().Be(GenerarRespuestaSugeridaCommandHandler.NoProcede);
        _redaccion.Llamadas.Should().BeEmpty();
    }

    [Fact]
    public async Task Dos_entrantes_con_la_misma_fecha_dan_siempre_el_mismo_origen()
    {
        var conversacion = Hilo();
        var primero = conversacion.Mensajes.Single();
        var segundo = Entrante(conversacion, primero.FechaUtc);
        var esperado = primero.Id.CompareTo(segundo.Id) > 0 ? primero : segundo;
        _redaccion.Respuesta = Result.Exito("Recibido.");

        await Generar().Handle(new(conversacion.Id), CancellationToken.None);

        _comunicaciones.ListaRespuestasSugeridas.Should().ContainSingle().Which.MensajeOrigenId.Should().Be(esperado.Id);
    }

    [Fact]
    public async Task Si_mientras_Vega_redacta_entra_un_automatico_la_sugerencia_se_guarda()
    {
        var conversacion = Hilo();
        _redaccion.AlRedactar = () => Entrante(conversacion, DateTime.UtcNow, esAutomatico: true);

        var resultado = await Generar().Handle(new(conversacion.Id), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        _comunicaciones.ListaRespuestasSugeridas.Should().ContainSingle();
    }

    [Fact]
    public async Task Si_mientras_Vega_redacta_otra_generacion_termina_es_un_conflicto()
    {
        var conversacion = Hilo();
        _redaccion.AlRedactar = () => Sugerencia(conversacion);

        var resultado = await Generar().Handle(new(conversacion.Id), CancellationToken.None);

        resultado.Error.Should().Be(GenerarRespuestaSugeridaCommandHandler.Conflicto);
        _comunicaciones.ListaRespuestasSugeridas.Should().ContainSingle().Which.Estado.Should().Be(EstadoRespuestaSugerida.Propuesta);
    }

    [Fact]
    public async Task La_violacion_del_indice_unico_es_un_conflicto_y_descarta_los_cambios()
    {
        var conversacion = Hilo();
        _unidad.ExcepcionAlGuardar = new DbUpdateException("índice único parcial", new ExcepcionDeBaseDeDatos("23505"));

        var resultado = await Generar().Handle(new(conversacion.Id), CancellationToken.None);

        resultado.Error.Should().Be(GenerarRespuestaSugeridaCommandHandler.Conflicto);
        _descarte.VecesDescartado.Should().Be(1);
    }

    [Fact]
    public async Task Un_cambio_de_version_a_medias_es_un_conflicto()
    {
        var conversacion = Hilo();
        _unidad.ExcepcionAlGuardar = new DbUpdateConcurrencyException("token");

        var resultado = await Generar().Handle(new(conversacion.Id), CancellationToken.None);

        resultado.Error.Should().Be(GenerarRespuestaSugeridaCommandHandler.Conflicto);
    }

    [Fact]
    public async Task Otro_fallo_de_la_base_no_se_disfraza_de_conflicto()
    {
        var conversacion = Hilo();
        _unidad.ExcepcionAlGuardar = new DbUpdateException("clave ajena", new ExcepcionDeBaseDeDatos("23503"));

        var generar = () => Generar().Handle(new(conversacion.Id), CancellationToken.None);

        await generar.Should().ThrowAsync<DbUpdateException>();
        _descarte.VecesDescartado.Should().Be(0);
    }

    [Fact]
    public async Task Un_entrante_automatico_no_es_el_mensaje_de_origen()
    {
        var conversacion = Hilo();
        var dePersona = conversacion.Mensajes.Single();
        Entrante(conversacion, DateTime.UtcNow, esAutomatico: true);

        var resultado = await Generar().Handle(new(conversacion.Id), CancellationToken.None);

        _comunicaciones.ListaRespuestasSugeridas.Single(r => r.Id == resultado.Valor).MensajeOrigenId.Should().Be(dePersona.Id);
    }

    [Fact]
    public async Task Sin_entrantes_de_una_persona_no_procede()
    {
        var conversacion = new Conversacion("Fuera de oficina", Guid.NewGuid());
        _comunicaciones.ListaConversaciones.Add(conversacion);
        Entrante(conversacion, esAutomatico: true);

        var resultado = await Generar().Handle(new(conversacion.Id), CancellationToken.None);

        resultado.Error.Should().Be(GenerarRespuestaSugeridaCommandHandler.NoProcede);
    }

    [Fact]
    public async Task Un_automatico_tras_contestar_una_persona_no_reabre_la_sugerencia()
    {
        var conversacion = Hilo();
        Saliente(conversacion);
        Entrante(conversacion, DateTime.UtcNow.AddMinutes(1), esAutomatico: true);

        var resultado = await Generar().Handle(new(conversacion.Id), CancellationToken.None);

        resultado.Error.Should().Be(GenerarRespuestaSugeridaCommandHandler.NoProcede);
    }

    [Fact]
    public async Task Un_saliente_automatico_no_tapa_el_entrante_que_espera_respuesta()
    {
        var conversacion = Hilo();
        Saliente(conversacion, esAutomatico: true);

        (await Generar().Handle(new(conversacion.Id), CancellationToken.None)).EsExitoso.Should().BeTrue();
    }

    [Fact]
    public async Task En_WhatsApp_la_macro_queda_en_texto_plano()
    {
        var conversacion = Hilo(canal: CanalConversacion.WhatsApp);
        var macro = Macro("<p>Recibida &amp; revisada.</p><ul><li>Uno</li></ul>");

        var resultado = await Generar().Handle(new(conversacion.Id, macro.Id), CancellationToken.None);

        _comunicaciones.ListaRespuestasSugeridas.Single(r => r.Id == resultado.Valor).Texto.Should().Be("Recibida & revisada.\n\n- Uno");
    }

    [Fact]
    public async Task En_WhatsApp_una_macro_sin_texto_no_se_guarda()
    {
        var conversacion = Hilo(canal: CanalConversacion.WhatsApp);

        var resultado = await Generar().Handle(new(conversacion.Id, Macro("<p> </p>").Id), CancellationToken.None);

        resultado.Error.Should().Be(GenerarRespuestaSugeridaCommandHandler.SinTexto);
        _comunicaciones.ListaRespuestasSugeridas.Should().BeEmpty();
    }

    [Fact]
    public async Task Insertar_y_confirmar_por_comando_guardan_autor_y_fecha()
    {
        var conversacion = Hilo();
        var respuesta = Sugerencia(conversacion);

        (await Resolver().Handle(new(respuesta.Id, AccionRespuestaSugerida.Insertar), CancellationToken.None)).EsExitoso.Should().BeTrue();
        respuesta.Estado.Should().Be(EstadoRespuestaSugerida.SinRevisar);

        (await Resolver().Handle(new(respuesta.Id, AccionRespuestaSugerida.Confirmar), CancellationToken.None)).EsExitoso.Should().BeTrue();

        respuesta.Estado.Should().Be(EstadoRespuestaSugerida.Revisada);
        respuesta.RevisadaPorUsuarioId.Should().Be(_usuarioId);
        respuesta.RevisadaEnUtc.Should().NotBeNull();
        _unidad.VecesGuardado.Should().Be(2);
    }

    [Fact]
    public async Task Descartar_por_comando_la_deja_fuera()
    {
        var conversacion = Hilo();
        var respuesta = Sugerencia(conversacion, EstadoRespuestaSugerida.SinRevisar);

        var resultado = await Resolver().Handle(new(respuesta.Id, AccionRespuestaSugerida.Descartar), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        respuesta.Estado.Should().Be(EstadoRespuestaSugerida.Descartada);
    }

    [Fact]
    public async Task Una_transicion_prohibida_no_guarda()
    {
        var conversacion = Hilo();
        var respuesta = Sugerencia(conversacion);

        var resultado = await Resolver().Handle(new(respuesta.Id, AccionRespuestaSugerida.Confirmar), CancellationToken.None);

        resultado.Error.Should().Be(RespuestaSugerida.ErrorTransicionNoPermitida);
        _unidad.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Resolver_una_inexistente_y_una_fuera_de_alcance_responde_igual()
    {
        var conversacion = Hilo();
        var respuesta = Sugerencia(conversacion);

        var inexistente = await Resolver().Handle(new(Guid.NewGuid(), AccionRespuestaSugerida.Descartar), CancellationToken.None);
        var ajena = await Resolver(new AlcanceDatosServiceFalso(tieneAccesoTotal: false))
            .Handle(new(respuesta.Id, AccionRespuestaSugerida.Descartar), CancellationToken.None);

        inexistente.Error.Codigo.Should().Be("Sugerencia.NoEncontrada");
        ajena.Error.Should().BeEquivalentTo(inexistente.Error);
        respuesta.Estado.Should().Be(EstadoRespuestaSugerida.Propuesta);
        _unidad.VecesGuardado.Should().Be(0);
    }

    /// <summary>
    /// Revisión Codex r3 de la #2163: confirmar es la acción que levanta el bloqueo del envío, así que la de un Gestor CAE sin
    /// la conversación en su cartera no puede dejarla revisada, igual que no puede insertarla ni descartarla.
    /// </summary>
    [Theory]
    [InlineData(AccionRespuestaSugerida.Insertar, EstadoRespuestaSugerida.Propuesta)]
    [InlineData(AccionRespuestaSugerida.Confirmar, EstadoRespuestaSugerida.SinRevisar)]
    [InlineData(AccionRespuestaSugerida.Descartar, EstadoRespuestaSugerida.SinRevisar)]
    public async Task Fuera_de_cartera_ninguna_accion_cambia_la_sugerencia(AccionRespuestaSugerida accion, EstadoRespuestaSugerida estado)
    {
        var conversacion = Hilo();
        var respuesta = Sugerencia(conversacion, estado);

        var resultado = await Resolver(new AlcanceDatosServiceFalso(tieneAccesoTotal: false))
            .Handle(new(respuesta.Id, accion), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("Sugerencia.NoEncontrada");
        respuesta.Estado.Should().Be(estado);
        respuesta.RevisadaPorUsuarioId.Should().BeNull();
        _unidad.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Resolver_sin_autor_no_cambia_nada()
    {
        var conversacion = Hilo();
        var respuesta = Sugerencia(conversacion);

        var resultado = await Resolver(sinUsuario: true).Handle(new(respuesta.Id, AccionRespuestaSugerida.Insertar), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("Sugerencia.SinAutor");
        respuesta.Estado.Should().Be(EstadoRespuestaSugerida.Propuesta);
    }

    [Fact]
    public async Task La_consulta_devuelve_la_activa()
    {
        var conversacion = Hilo();
        var respuesta = Sugerencia(conversacion, EstadoRespuestaSugerida.SinRevisar);

        var dto = await Obtener().Handle(new(conversacion.Id), CancellationToken.None);

        dto.Should().NotBeNull();
        dto!.Id.Should().Be(respuesta.Id);
        dto.Estado.Should().Be(EstadoRespuestaSugerida.SinRevisar);
        dto.Texto.Should().Be(respuesta.Texto);
    }

    [Fact]
    public async Task La_consulta_no_devuelve_una_ya_resuelta()
    {
        var conversacion = Hilo();
        var respuesta = Sugerencia(conversacion);
        respuesta.Descartar();

        (await Obtener().Handle(new(conversacion.Id), CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task La_consulta_esta_cerrada_al_portal()
    {
        var conversacion = Hilo();
        Sugerencia(conversacion);

        (await Obtener(puedeLeer: false).Handle(new(conversacion.Id), CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task La_consulta_no_llega_a_la_tabla_con_una_sesion_privilegiada()
    {
        var conversacion = Hilo();
        Sugerencia(conversacion);

        var dto = await Obtener(sesionPrivilegiada: true).Handle(new(conversacion.Id), CancellationToken.None);

        dto.Should().BeNull();
        _comunicaciones.LecturasDeRespuestasSugeridas.Should().Be(0, "cae_app_soporte no tiene concesión sobre la tabla");
    }

    [Fact]
    public async Task La_consulta_respeta_el_alcance_de_la_conversacion()
    {
        var conversacion = Hilo();
        Sugerencia(conversacion);

        var dto = await Obtener(alcance: new AlcanceDatosServiceFalso(tieneAccesoTotal: false))
            .Handle(new(conversacion.Id), CancellationToken.None);

        dto.Should().BeNull();
    }

    private sealed class SesionFalsa(bool activa) : ISesionPrivilegiadaActual
    {
        public Task<SesionPrivilegiadaActiva?> ObtenerAsync(CancellationToken cancellationToken = default) => RevalidarAsync(cancellationToken);

        public Task<SesionPrivilegiadaActiva?> RevalidarAsync(CancellationToken cancellationToken = default) => Task.FromResult<SesionPrivilegiadaActiva?>(
            activa ? new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), CapacidadPrivilegio.SoporteLectura, null) : null);
    }

    private sealed class ExcepcionDeBaseDeDatos(string estado) : DbException("fallo de la base")
    {
        public override string SqlState => estado;
    }

    private sealed class VisitasVacias : IVisitasQueryContext
    {
        public IQueryable<Visita> Visitas => new TestAsyncQueryable<Visita>(new List<Visita>().AsQueryable());
        public IQueryable<VisitaTrabajador> VisitasTrabajadores => throw new NotSupportedException();
    }

    private sealed class RespuestasFalsas(ComunicacionesQueryContextFalso comunicaciones) : IRespuestaSugeridaRepository
    {
        public Task<RespuestaSugerida?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(comunicaciones.ListaRespuestasSugeridas.FirstOrDefault(r => r.Id == id));

        public Task<RespuestaSugerida?> ObtenerActivaPorConversacionAsync(Guid conversacionId, CancellationToken cancellationToken = default) =>
            Task.FromResult(comunicaciones.ListaRespuestasSugeridas.FirstOrDefault(r => r.ConversacionId == conversacionId && r.EstaActiva));

        public void Agregar(RespuestaSugerida respuesta) => comunicaciones.ListaRespuestasSugeridas.Add(respuesta);
    }

    private sealed class RedaccionFalsa : IRedaccionRespuestaIa
    {
        public Result<string> Respuesta { get; set; } = Result.Exito("Recibido, gracias.");
        public List<(string Asunto, string Cuerpo)> Llamadas { get; } = [];
        public Action? AlRedactar { get; set; }

        public Task<Result<string>> RedactarAsync(string asunto, string cuerpoUltimoEntrante, CancellationToken cancellationToken = default)
        {
            Llamadas.Add((asunto, cuerpoUltimoEntrante));
            AlRedactar?.Invoke();
            return Task.FromResult(Respuesta);
        }
    }

    private sealed class InstruccionTratamientoFalsa : IInstruccionTratamientoIaService
    {
        public bool Habilitada { get; set; } = true;

        public Task<bool> EstaHabilitadaAsync(Guid tenantId, CancellationToken cancellationToken = default) => Task.FromResult(Habilitada);
    }
}
