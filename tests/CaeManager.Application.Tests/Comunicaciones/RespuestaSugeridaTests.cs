using CaeManager.Application.Common;
using CaeManager.Application.Comunicaciones;
using CaeManager.Application.Comunicaciones.Commands.GenerarRespuestaSugerida;
using CaeManager.Application.Comunicaciones.Commands.ResolverRespuestaSugerida;
using CaeManager.Application.Comunicaciones.Queries.ObtenerRespuestaSugerida;
using CaeManager.Application.Cumplimiento;
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
using CaeManager.Domain.Visitas;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

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

    private ObtenerRespuestaSugeridaQueryHandler Obtener(bool puedeLeer = true, AlcanceDatosServiceFalso? alcance = null) => new(
        _comunicaciones, alcance ?? new AlcanceDatosServiceFalso(), new PoliticaNotaInternaFalsa(puedeLeer));

    private Conversacion Hilo(Guid? clienteId = null, bool anclada = true, CanalConversacion canal = CanalConversacion.Correo)
    {
        var conversacion = canal == CanalConversacion.WhatsApp
            ? Conversacion.CrearWhatsApp("+34600111222", Guid.NewGuid(), anclada ? clienteId ?? Guid.NewGuid() : null, Guid.NewGuid())
            : new Conversacion("Documentación", anclada ? clienteId ?? Guid.NewGuid() : null);
        _comunicaciones.ListaConversaciones.Add(conversacion);
        Entrante(conversacion);
        return conversacion;
    }

    private Mensaje Entrante(Conversacion conversacion, DateTime? fecha = null)
    {
        var mensaje = conversacion.AgregarMensaje(
            DireccionMensaje.Entrante, conversacion.Canal, "ana@contrata.test", "<p>Adjunto la documentación</p>", fecha ?? DateTime.UtcNow.AddHours(-1));
        _comunicaciones.ListaMensajes.Add(mensaje);
        return mensaje;
    }

    private void Saliente(Conversacion conversacion)
    {
        var mensaje = conversacion.AgregarMensaje(
            DireccionMensaje.Saliente, conversacion.Canal, "cae@empresa.test", "<p>Recibido</p>", DateTime.UtcNow);
        _comunicaciones.ListaMensajes.Add(mensaje);
    }

    private MacroRespuesta Macro(string cuerpo = "<p>Recibida, gracias.</p>", Guid? clienteId = null)
    {
        var macro = new MacroRespuesta("Acuse", cuerpo, clienteId);
        _comunicaciones.ListaMacrosRespuesta.Add(macro);
        return macro;
    }

    private RespuestaSugerida Sugerencia(Conversacion conversacion, EstadoRespuestaSugerida estado = EstadoRespuestaSugerida.Propuesta)
    {
        var respuesta = RespuestaSugerida.DeVega(
            conversacion.Id, conversacion.Mensajes.First().Id, "<p>Borrador</p>", Guid.NewGuid(), DateTime.UtcNow);
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
        _redaccion.Respuesta = Result.Fallo<string>(Error.Crear("Otro.Error", "fallo"));

        var resultado = await Generar().Handle(new(conversacion.Id), CancellationToken.None);

        resultado.Error.Should().Be(GenerarRespuestaSugeridaCommandHandler.NoDisponible);
        anterior.Estado.Should().Be(EstadoRespuestaSugerida.Propuesta);
        _comunicaciones.ListaRespuestasSugeridas.Should().ContainSingle();
        _unidad.VecesGuardado.Should().Be(0);
        _transaccion.Ejecutadas.Should().Be(0);
    }

    [Theory]
    [InlineData(EstadoRespuestaSugerida.Propuesta)]
    [InlineData(EstadoRespuestaSugerida.SinRevisar)]
    public async Task Generar_otra_descarta_la_activa_en_la_misma_transaccion(EstadoRespuestaSugerida estado)
    {
        var conversacion = Hilo();
        var anterior = Sugerencia(conversacion, estado);

        var resultado = await Generar().Handle(new(conversacion.Id), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        anterior.Estado.Should().Be(EstadoRespuestaSugerida.Descartada);
        _comunicaciones.ListaRespuestasSugeridas.Where(r => r.EstaActiva).Should().ContainSingle().Which.Id.Should().Be(resultado.Valor);
        _transaccion.Ejecutadas.Should().Be(1);
        _unidad.VecesGuardado.Should().Be(2);
    }

    [Fact]
    public async Task Si_gana_otra_generacion_concurrente_se_descartan_los_cambios_y_se_informa()
    {
        var conversacion = Hilo();
        _unidad.ExcepcionAlGuardar = new DbUpdateException("índice único parcial");

        var resultado = await Generar().Handle(new(conversacion.Id), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("Sugerencia.Conflicto");
        _descarte.VecesDescartado.Should().Be(1);
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
    public async Task La_consulta_respeta_el_alcance_de_la_conversacion()
    {
        var conversacion = Hilo();
        Sugerencia(conversacion);

        var dto = await Obtener(alcance: new AlcanceDatosServiceFalso(tieneAccesoTotal: false))
            .Handle(new(conversacion.Id), CancellationToken.None);

        dto.Should().BeNull();
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

        public Task<Result<string>> RedactarAsync(string asunto, string cuerpoUltimoEntrante, CancellationToken cancellationToken = default)
        {
            Llamadas.Add((asunto, cuerpoUltimoEntrante));
            return Task.FromResult(Respuesta);
        }
    }

    private sealed class InstruccionTratamientoFalsa : IInstruccionTratamientoIaService
    {
        public bool Habilitada { get; set; } = true;

        public Task<bool> EstaHabilitadaAsync(Guid tenantId, CancellationToken cancellationToken = default) => Task.FromResult(Habilitada);
    }
}
