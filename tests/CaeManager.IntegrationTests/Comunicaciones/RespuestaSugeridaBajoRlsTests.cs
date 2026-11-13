using CaeManager.Application.Common;
using CaeManager.Application.Comunicaciones;
using CaeManager.Application.Comunicaciones.Commands.GenerarRespuestaSugerida;
using CaeManager.Application.Comunicaciones.Commands.ResolverRespuestaSugerida;
using CaeManager.Application.Comunicaciones.Queries.ObtenerRespuestaSugerida;
using CaeManager.Application.Cumplimiento;
using CaeManager.Application.Plataforma;
using CaeManager.Domain.Common;
using CaeManager.Domain.Comunicaciones;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Integraciones;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.Autorizacion;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Interceptors;
using CaeManager.Infrastructure.Persistence.Repositories;
using CaeManager.IntegrationTests.Reclamaciones;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace CaeManager.IntegrationTests.Comunicaciones;

/// <summary>
/// La respuesta sugerida <b>como <c>cae_app_runtime</c></b>, con los interceptores de sellado y de sesión RLS y el
/// <see cref="AlcanceDatosService"/> real (mismo arnés que <c>NotaInternaDeEmpresaBajoRlsTests</c>). Dos Tenants
/// propietarios con una conversación y una sugerencia cada uno; la sesión es de un Administrador del primero, con acceso
/// total a su Tenant: lo que se mide es la frontera entre Tenants, no la cartera.
/// </summary>
public class RespuestaSugeridaBajoRlsTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _usuarioId = Guid.NewGuid();
    private CaeManagerDbContext _propietario = null!;
    private TenantActualDeLaPeticion _tenantDeLaPeticion = null!;
    private Guid _tenantSesion;
    private Guid _tenantAjeno;
    private Guid _conversacionPropia;
    private Guid _conversacionAjena;
    private Guid _mensajePropio;
    private Guid _mensajeAjeno;
    private Guid _sugerenciaPropia;
    private Guid _sugerenciaAjena;

    public async Task InitializeAsync()
    {
        var tenantPorAmbito = new TenantActualPorAmbito();
        var opcionesPropietario = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantPorAmbito))
            .Options;
        _propietario = new CaeManagerDbContext(opcionesPropietario, new EphemeralDataProtectionProvider(), tenantPorAmbito);
        await _propietario.Database.MigrateAsync();

        var tenantSesion = new Tenant("Tenant propietario de la sesión");
        var tenantAjeno = new Tenant("Tenant propietario ajeno");
        _propietario.Tenants.AddRange(tenantSesion, tenantAjeno);
        await _propietario.SaveChangesAsync();
        _tenantSesion = tenantSesion.Id;
        _tenantAjeno = tenantAjeno.Id;

        (_conversacionPropia, _mensajePropio, _sugerenciaPropia) = await SembrarAsync(tenantSesion.Id, "Montajes Springfield");
        (_conversacionAjena, _mensajeAjeno, _sugerenciaAjena) = await SembrarAsync(tenantAjeno.Id, "Montajes Shelbyville");

        _tenantDeLaPeticion = new TenantActualDeLaPeticion(_tenantSesion);
    }

    public async Task DisposeAsync()
    {
        await _propietario.DisposeAsync();
        await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);
    }

    /// <summary>Control del instrumento: la conexión está de verdad bajo RLS y la sugerencia ajena existe.</summary>
    [Fact]
    public async Task La_conexion_va_bajo_RLS_efectiva_y_la_sugerencia_ajena_existe()
    {
        await using var runtime = CrearRuntime();

        (await runtime.RespuestasSugeridas.IgnoreQueryFilters().CountAsync(r => r.Id == _sugerenciaAjena)).Should().Be(0,
            "sin filtro global de EF, solo RLS puede ocultar la sugerencia del Tenant ajeno");
        (await runtime.RespuestasSugeridas.IgnoreQueryFilters().CountAsync(r => r.Id == _sugerenciaPropia)).Should().Be(1,
            "control positivo: la sugerencia del Tenant de la sesión sí se ve");

        await using var propietario = CrearPropietarioLimpio();
        (await propietario.RespuestasSugeridas.IgnoreQueryFilters().CountAsync(r => r.Id == _sugerenciaAjena)).Should().Be(1);
    }

    [Fact]
    public async Task Una_sugerencia_de_otro_Tenant_no_se_lee_ni_se_escribe_por_Id()
    {
        await using (var runtime = CrearRuntime())
        {
            (await Resolver(runtime).Handle(new ResolverRespuestaSugeridaCommand(_sugerenciaAjena, AccionRespuestaSugerida.Descartar), CancellationToken.None))
                .Error.Codigo.Should().Be("Sugerencia.NoEncontrada");
            (await Obtener(runtime).Handle(new ObtenerRespuestaSugeridaQuery(_conversacionAjena), CancellationToken.None)).Should().BeNull();
            (await Generar(runtime).Handle(new GenerarRespuestaSugeridaCommand(_conversacionAjena), CancellationToken.None))
                .Error.Codigo.Should().Be("Conversacion.NoEncontrada");
        }

        await using var comprobacion = CrearPropietarioLimpio();
        var ajenas = await comprobacion.RespuestasSugeridas.IgnoreQueryFilters().Where(r => r.ConversacionId == _conversacionAjena).ToListAsync();
        ajenas.Should().ContainSingle().Which.Estado.Should().Be(EstadoRespuestaSugerida.SinRevisar);
    }

    [Fact]
    public async Task Una_fila_con_el_Tenant_de_otro_no_entra_por_la_politica()
    {
        await using var runtime = CrearRuntime();

        var excepcion = await Record.ExceptionAsync(() => runtime.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO "RespuestasSugeridas"
                ("Id", "TenantId", "ConversacionId", "MensajeOrigenId", "Origen", "Texto", "TextoHash", "Estado", "GeneradaPorUsuarioId",
                 "GeneradaEnUtc", "Version", "CreadoEnUtc", "EstaEliminado")
            VALUES ({Guid.NewGuid()}, {_tenantAjeno}, {_conversacionAjena}, {_mensajeAjeno}, 1, 'Texto', 'hash', 3, {_usuarioId},
                    now(), {Guid.NewGuid()}, now(), false)
            """));

        excepcion.Should().BeOfType<PostgresException>()
            .Which.SqlState.Should().Be("42501", "el WITH CHECK de aislamiento_tenant debe rechazar la fila antes que la FK");
    }

    [Fact]
    public async Task El_bloqueo_ve_lo_del_Tenant_de_la_sesion_y_no_lo_de_otro()
    {
        await using var runtime = CrearRuntime();

        (await runtime.ComprobarEnvioAsync(_conversacionPropia, "Escrito a mano", CancellationToken.None))
            .Error.Should().Be(BloqueoRespuestaSugerida.SugerenciaSinRevisar);

        // La conversación ajena tiene una SinRevisar que la sesión no ve: el bloqueo vacío aquí mide RLS, no ausencia.
        (await runtime.ComprobarEnvioAsync(_conversacionAjena, "Escrito a mano", CancellationToken.None))
            .EsExitoso.Should().BeTrue();
    }

    /// <summary>El arnés lleva el interceptor de versiones: sin él el token nunca cambiaría y esto pasaría por otra causa.</summary>
    [Fact]
    public async Task Resolver_sobre_una_fila_ya_cambiada_falla_por_el_token()
    {
        await using var primero = CrearRuntime();
        await using var segundo = CrearRuntime();
        var unidad = new UnidadQueAntesDeGuardar(primero, () => Resolver(segundo).Handle(
            new ResolverRespuestaSugeridaCommand(_sugerenciaPropia, AccionRespuestaSugerida.Descartar), CancellationToken.None));
        var handler = new ResolverRespuestaSugeridaCommandHandler(
            primero, new RespuestaSugeridaRepository(primero), Alcance(primero), Usuario(), unidad);

        var confirmar = () => handler.Handle(
            new ResolverRespuestaSugeridaCommand(_sugerenciaPropia, AccionRespuestaSugerida.Confirmar), CancellationToken.None);

        await confirmar.Should().ThrowAsync<DbUpdateConcurrencyException>();

        await using var comprobacion = CrearPropietarioLimpio();
        var fila = await comprobacion.RespuestasSugeridas.IgnoreQueryFilters().SingleAsync(r => r.Id == _sugerenciaPropia);
        fila.Estado.Should().Be(EstadoRespuestaSugerida.Descartada);
        fila.RevisadaPorUsuarioId.Should().BeNull();
    }

    /// <summary>La barrera deja a las dos generaciones pasada la lectura inicial: ninguna ve lo que guarda la otra.</summary>
    [Fact]
    public async Task Dos_generaciones_simultaneas_dejan_una_activa_y_un_conflicto()
    {
        await DescartarLaSembradaAsync();
        var barrera = new RedaccionConBarrera(participantes: 2);
        await using var primero = CrearRuntime();
        await using var segundo = CrearRuntime();

        var resultados = await Task.WhenAll(
            Generar(primero, barrera).Handle(new GenerarRespuestaSugeridaCommand(_conversacionPropia), CancellationToken.None),
            Generar(segundo, barrera).Handle(new GenerarRespuestaSugeridaCommand(_conversacionPropia), CancellationToken.None));

        resultados.Should().ContainSingle(r => r.EsExitoso);
        resultados.Single(r => r.EsFallido).Error.Should().Be(GenerarRespuestaSugeridaCommandHandler.Conflicto);

        await using var comprobacion = CrearPropietarioLimpio();
        (await comprobacion.RespuestasSugeridas.IgnoreQueryFilters()
                .CountAsync(r => r.ConversacionId == _conversacionPropia &&
                                 (r.Estado == EstadoRespuestaSugerida.Propuesta || r.Estado == EstadoRespuestaSugerida.SinRevisar)))
            .Should().Be(1);
    }

    [Fact]
    public async Task Una_sugerencia_sin_revisar_no_se_sustituye()
    {
        await using var runtime = CrearRuntime();

        (await Generar(runtime).Handle(new GenerarRespuestaSugeridaCommand(_conversacionPropia), CancellationToken.None))
            .Error.Should().Be(GenerarRespuestaSugeridaCommandHandler.PendienteDeRevisar);

        await using var comprobacion = CrearPropietarioLimpio();
        (await comprobacion.RespuestasSugeridas.IgnoreQueryFilters().SingleAsync(r => r.Id == _sugerenciaPropia))
            .Estado.Should().Be(EstadoRespuestaSugerida.SinRevisar);
    }

    [Fact]
    public async Task Una_propuesta_se_sustituye_al_llegar_otro_entrante()
    {
        await DescartarLaSembradaAsync();
        Guid primera;
        await using (var runtime = CrearRuntime())
            primera = (await Generar(runtime).Handle(new GenerarRespuestaSugeridaCommand(_conversacionPropia), CancellationToken.None)).Valor;

        var nuevoEntrante = await SembrarEntranteAsync(_tenantSesion, _conversacionPropia);

        Guid segunda;
        await using (var runtime = CrearRuntime())
            segunda = (await Generar(runtime).Handle(new GenerarRespuestaSugeridaCommand(_conversacionPropia), CancellationToken.None)).Valor;

        await using var comprobacion = CrearPropietarioLimpio();
        var filas = await comprobacion.RespuestasSugeridas.IgnoreQueryFilters()
            .Where(r => r.ConversacionId == _conversacionPropia).ToListAsync();
        filas.Single(r => r.Id == primera).Estado.Should().Be(EstadoRespuestaSugerida.Descartada);
        var activa = filas.Single(r => r.Id == segunda);
        activa.Estado.Should().Be(EstadoRespuestaSugerida.Propuesta);
        activa.MensajeOrigenId.Should().Be(nuevoEntrante);
    }

    [Fact]
    public async Task El_bloqueo_compara_tambien_por_buzon_y_con_el_texto_descartado()
    {
        var conexion = await SembrarHiloDeBuzonConPropuestaAsync("<p>Texto del buzón</p>");
        await using (var runtime = CrearRuntime())
        {
            (await PorBuzon(runtime, conexion, "Texto del buzón", CancellationToken.None))
                .Error.Should().Be(BloqueoRespuestaSugerida.SugerenciaSinRevisar);
            (await PorBuzon(runtime, conexion, TextoEscritoAMano, CancellationToken.None))
                .EsExitoso.Should().BeTrue();
        }

        await DescartarLaSembradaAsync();
        await using var despues = CrearRuntime();
        (await despues.ComprobarEnvioAsync(_conversacionPropia, "<p>Borrador</p>", CancellationToken.None))
            .Error.Should().Be(BloqueoRespuestaSugerida.SugerenciaSinRevisar);
        (await despues.ComprobarEnvioAsync(_conversacionPropia, TextoEscritoAMano, CancellationToken.None))
            .EsExitoso.Should().BeTrue();
    }

    /// <summary>Traduce a SQL la subconsulta del último entrante, sin la que el buzón no distingue qué Descartada aplica.</summary>
    [Fact]
    public async Task Por_buzon_bloquea_la_descartada_del_ultimo_entrante_y_no_la_de_uno_anterior()
    {
        var conexion = await SembrarHiloDeBuzonConPropuestaAsync("<p>Texto descartado</p>", descartada: true);
        await using (var runtime = CrearRuntime())
        {
            (await PorBuzon(runtime, conexion, "Texto descartado", CancellationToken.None))
                .Error.Should().Be(BloqueoRespuestaSugerida.SugerenciaSinRevisar);
        }

        await SembrarEntranteAsync(_tenantSesion, await ConversacionDelBuzonAsync(conexion));

        await using var despues = CrearRuntime();
        (await PorBuzon(despues, conexion, "Texto descartado", CancellationToken.None))
            .EsExitoso.Should().BeTrue();
    }

    /// <summary>
    /// Por buzón el texto contenido también bloquea: copiar la propuesta y añadir una firma cambia el hash, y la comparación
    /// por contención se hace en memoria sobre las candidatas que devuelve SQL. Un texto sin relación sigue saliendo.
    /// </summary>
    [Fact]
    public async Task Por_buzon_la_propuesta_con_una_firma_anadida_bloquea_y_un_texto_distinto_no()
    {
        var conexion = await SembrarHiloDeBuzonConPropuestaAsync("<p>Texto del buzón con la documentación.</p>");
        await using var runtime = CrearRuntime();

        (await PorBuzon(runtime, conexion, "<p>Texto del buzón con la documentación.</p><p>Un saludo, Marta</p>", CancellationToken.None))
            .Error.Should().Be(BloqueoRespuestaSugerida.SugerenciaSinRevisar);
        (await PorBuzon(runtime, conexion, TextoEscritoAMano, CancellationToken.None))
            .EsExitoso.Should().BeTrue();
    }

    /// <summary>Control positivo: el ciclo completo persiste bajo RLS.</summary>
    [Fact]
    public async Task Generar_insertar_y_confirmar_persisten()
    {
        await DescartarLaSembradaAsync();
        Guid generada;
        await using (var runtime = CrearRuntime())
        {
            var resultado = await Generar(runtime).Handle(new GenerarRespuestaSugeridaCommand(_conversacionPropia), CancellationToken.None);

            resultado.EsExitoso.Should().BeTrue();
            generada = resultado.Valor;
        }

        await using (var runtime = CrearRuntime())
        {
            var dto = await Obtener(runtime).Handle(new ObtenerRespuestaSugeridaQuery(_conversacionPropia), CancellationToken.None);
            dto!.Id.Should().Be(generada);
            dto.Estado.Should().Be(EstadoRespuestaSugerida.Propuesta);
            dto.Origen.Should().Be(OrigenRespuestaSugerida.Vega);
            dto.MensajeOrigenId.Should().Be(_mensajePropio);
            dto.Texto.Should().Be("<p>Recibido, gracias.</p>");

            (await Resolver(runtime).Handle(new ResolverRespuestaSugeridaCommand(generada, AccionRespuestaSugerida.Insertar), CancellationToken.None))
                .EsExitoso.Should().BeTrue();
        }

        await using (var runtime = CrearRuntime())
        {
            (await Resolver(runtime).Handle(new ResolverRespuestaSugeridaCommand(generada, AccionRespuestaSugerida.Confirmar), CancellationToken.None))
                .EsExitoso.Should().BeTrue();
        }

        await using var comprobacion = CrearPropietarioLimpio();
        var filas = await comprobacion.RespuestasSugeridas.IgnoreQueryFilters()
            .Where(r => r.ConversacionId == _conversacionPropia).ToListAsync();
        filas.Should().HaveCount(2);
        filas.Single(r => r.Id == _sugerenciaPropia).Estado.Should().Be(EstadoRespuestaSugerida.Descartada);
        var revisada = filas.Single(r => r.Id == generada);
        revisada.Estado.Should().Be(EstadoRespuestaSugerida.Revisada);
        revisada.RevisadaPorUsuarioId.Should().Be(_usuarioId);
        revisada.TenantId.Should().Be(_tenantSesion);

        await using var despues = CrearRuntime();
        (await despues.ComprobarEnvioAsync(_conversacionPropia, TextoEscritoAMano, CancellationToken.None)).EsExitoso.Should().BeTrue();
    }

    [Fact]
    public async Task El_indice_unico_parcial_no_admite_dos_activas_en_una_conversacion()
    {
        await using var runtime = CrearRuntime();
        runtime.RespuestasSugeridas.Add(RespuestaSugerida.DeVega(_conversacionPropia, _mensajePropio, "<p>Otra</p>", _usuarioId, DateTime.UtcNow));

        var excepcion = await Record.ExceptionAsync(() => runtime.SaveChangesAsync());

        excepcion.Should().BeOfType<DbUpdateException>()
            .Which.InnerException.Should().BeOfType<PostgresException>()
            .Which.SqlState.Should().Be("23505");
    }

    [Theory]
    [InlineData("""UPDATE "RespuestasSugeridas" SET "Texto" = 'otro'""")]
    [InlineData("""UPDATE "RespuestasSugeridas" SET "TextoHash" = 'otro'""")]
    [InlineData("""UPDATE "RespuestasSugeridas" SET "GeneradaPorUsuarioId" = gen_random_uuid()""")]
    [InlineData("""UPDATE "RespuestasSugeridas" SET "TenantId" = gen_random_uuid()""")]
    [InlineData("""DELETE FROM "RespuestasSugeridas" """)]
    public async Task La_aplicacion_no_puede_reescribir_el_texto_ni_borrar(string orden)
    {
        await using var runtime = CrearRuntime();

        var excepcion = await Record.ExceptionAsync(() => runtime.Database.ExecuteSqlRawAsync(orden));

        excepcion.Should().BeOfType<PostgresException>().Which.SqlState.Should().Be("42501");
    }

    /// <summary>
    /// Un texto escrito a mano que se aparta de las sugerencias sembradas más de 15 caracteres: con la regla única de revisión
    /// (revisión Codex r2 de la #2163, ALTO), un texto corto a 15 o menos de «Borrador» cuenta como la sugerencia sin revisar.
    /// </summary>
    private const string TextoEscritoAMano = "Lo escribo a mano y no se parece a ninguna sugerencia sembrada en este hilo.";

    private async Task DescartarLaSembradaAsync()
    {
        await using var runtime = CrearRuntime();
        (await Resolver(runtime).Handle(new ResolverRespuestaSugeridaCommand(_sugerenciaPropia, AccionRespuestaSugerida.Descartar), CancellationToken.None))
            .EsExitoso.Should().BeTrue();
    }

    private GenerarRespuestaSugeridaCommandHandler Generar(CaeManagerDbContext runtime, IRedaccionRespuestaIa? redaccion = null)
    {
        var alcance = Alcance(runtime);
        return new GenerarRespuestaSugeridaCommandHandler(
            runtime, new RespuestaSugeridaRepository(runtime),
            new RellenoMacroRespuesta(runtime, runtime, runtime, runtime, runtime, PendientesDeReclamacionFabrica.Crear(runtime, alcance), alcance),
            redaccion ?? new RedaccionFija(), new InstruccionHabilitada(), _tenantDeLaPeticion, alcance, Usuario(),
            new TransaccionDeComando(runtime), runtime, runtime);
    }

    private ResolverRespuestaSugeridaCommandHandler Resolver(CaeManagerDbContext runtime) => new(
        runtime, new RespuestaSugeridaRepository(runtime), Alcance(runtime), Usuario(), runtime);

    private ObtenerRespuestaSugeridaQueryHandler Obtener(CaeManagerDbContext runtime) => new(
        runtime, Alcance(runtime), PoliticaNotaInternaPruebas.Con(Roles.Administrador), new SesionPrivilegiadaAusente());

    private CurrentUserServiceFalso Usuario() => new(_usuarioId, Roles.Administrador, tenantOrigenId: _tenantSesion);

    private Task<Result> PorBuzon(CaeManagerDbContext contexto, Guid conexion, string cuerpo, CancellationToken cancellationToken)
    {
        var alcance = Alcance(contexto);
        return contexto.ComprobarEnvioPorBuzonAsync(alcance, alcance, conexion, ["remitente@example.test"], cuerpo, cancellationToken);
    }

    private AlcanceDatosService Alcance(CaeManagerDbContext contexto) => new(
        contexto, Usuario(), _tenantDeLaPeticion, new SesionPrivilegiadaAusente());

    private async Task<(Guid Conversacion, Guid Mensaje, Guid Sugerencia)> SembrarAsync(Guid tenantId, string razonSocial)
    {
        using var ambito = AmbitoTenantExplicito.Establecer(tenantId);
        var empresa = Empresa.CrearComoCliente(razonSocial, "B12345674", false, null, null);
        var conversacion = new Conversacion("Documentación", empresaId: empresa.Id);
        var mensaje = new Mensaje(conversacion.Id, DireccionMensaje.Entrante, CanalConversacion.Correo, "remitente@example.test", "Cuerpo", DateTime.UtcNow);
        var sugerencia = RespuestaSugerida.DeVega(conversacion.Id, mensaje.Id, "<p>Borrador</p>", Guid.NewGuid(), DateTime.UtcNow);
        sugerencia.Insertar();
        _propietario.Empresas.Add(empresa);
        _propietario.Conversaciones.Add(conversacion);
        _propietario.Mensajes.Add(mensaje);
        await _propietario.SaveChangesAsync();
        _propietario.RespuestasSugeridas.Add(sugerencia);
        await _propietario.SaveChangesAsync();
        return (conversacion.Id, mensaje.Id, sugerencia.Id);
    }

    private async Task<Guid> SembrarEntranteAsync(Guid tenantId, Guid conversacionId)
    {
        using var ambito = AmbitoTenantExplicito.Establecer(tenantId);
        var mensaje = new Mensaje(conversacionId, DireccionMensaje.Entrante, CanalConversacion.Correo, "remitente@example.test", "Otro cuerpo", DateTime.UtcNow);
        _propietario.Mensajes.Add(mensaje);
        await _propietario.SaveChangesAsync();
        return mensaje.Id;
    }

    private async Task<Guid> ConversacionDelBuzonAsync(Guid conexionId)
    {
        await using var propietario = CrearPropietarioLimpio();
        return await propietario.Conversaciones.IgnoreQueryFilters()
            .Where(c => c.ConexionIntegracionId == conexionId).Select(c => c.Id).SingleAsync();
    }

    private async Task<Guid> SembrarHiloDeBuzonConPropuestaAsync(string texto, bool descartada = false)
    {
        using var ambito = AmbitoTenantExplicito.Establecer(_tenantSesion);
        var empresaId = await _propietario.Conversaciones.IgnoreQueryFilters().Where(c => c.Id == _conversacionPropia).Select(c => c.EmpresaId).SingleAsync();
        var conexion = new ConexionIntegracion("cae@example.test", "Buzón CAE");
        var conversacion = new Conversacion("Documentación", empresaId: empresaId);
        conversacion.AsociarConexion(conexion.Id, "hilo-del-buzon");
        var mensaje = new Mensaje(conversacion.Id, DireccionMensaje.Entrante, CanalConversacion.Correo, "remitente@example.test", "Cuerpo", DateTime.UtcNow);
        _propietario.ConexionesIntegracion.Add(conexion);
        _propietario.Conversaciones.Add(conversacion);
        _propietario.Mensajes.Add(mensaje);
        await _propietario.SaveChangesAsync();
        var sugerencia = RespuestaSugerida.DeVega(conversacion.Id, mensaje.Id, texto, Guid.NewGuid(), DateTime.UtcNow);
        if (descartada)
            sugerencia.Descartar();
        _propietario.RespuestasSugeridas.Add(sugerencia);
        await _propietario.SaveChangesAsync();
        return conexion.Id;
    }

    private CaeManagerDbContext CrearPropietarioLimpio()
    {
        var tenant = new TenantActualPorAmbito();
        var opciones = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion)
            .AddInterceptors(new TenantSelladoInterceptor(tenant))
            .Options;
        return new CaeManagerDbContext(opciones, new EphemeralDataProtectionProvider(), tenant);
    }

    private CaeManagerDbContext CrearRuntime()
    {
        var opciones = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(BaseDatosPostgresDePruebas.CadenaComoRuntime(_cadenaConexion))
            .AddInterceptors(
                new TenantSelladoInterceptor(_tenantDeLaPeticion),
                new ConcurrenciaOptimistaInterceptor(),
                new TenantRlsConnectionInterceptor(_tenantDeLaPeticion, new SinClienteActivo(), Usuario(), BaseDatosPostgresDePruebas.FirmanteContextoRls))
            .Options;
        return new CaeManagerDbContext(opciones, new EphemeralDataProtectionProvider(), _tenantDeLaPeticion);
    }

    private sealed class RedaccionFija : IRedaccionRespuestaIa
    {
        public Task<Result<string>> RedactarAsync(string asunto, string cuerpoUltimoEntrante, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Exito("Recibido, gracias."));
    }

    private sealed class RedaccionConBarrera(int participantes) : IRedaccionRespuestaIa
    {
        private readonly TaskCompletionSource _todos = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _llegados;

        public async Task<Result<string>> RedactarAsync(string asunto, string cuerpoUltimoEntrante, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _llegados) == participantes)
                _todos.TrySetResult();

            await _todos.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            return Result.Exito("Recibido, gracias.");
        }
    }

    private sealed class UnidadQueAntesDeGuardar(CaeManagerDbContext contexto, Func<Task> antes) : IUnitOfWork
    {
        public async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            await antes();
            return await contexto.SaveChangesAsync(cancellationToken);
        }
    }

    private sealed class InstruccionHabilitada : IInstruccionTratamientoIaService
    {
        public Task<bool> EstaHabilitadaAsync(Guid tenantId, CancellationToken cancellationToken = default) => Task.FromResult(true);
    }

    private sealed class TenantActualPorAmbito : ITenantActual
    {
        public Guid? TenantId => AmbitoTenantExplicito.TenantIdActual;
    }

    /// <summary>Como el <c>TenantActual</c> real: primero el ámbito explícito, luego el Tenant de la sesión.</summary>
    private sealed class TenantActualDeLaPeticion(Guid tenantDeLaSesion) : ITenantActual
    {
        public Guid? TenantId => AmbitoTenantExplicito.TenantIdActual ?? tenantDeLaSesion;
    }

    private sealed class SinClienteActivo : IClienteActivoSeleccionado
    {
        public Guid? TenantIdSeleccionado => null;
        public Guid? AsignacionOperacionIdSeleccionada => null;
        public Guid? SesionPrivilegiadaIdSeleccionada => null;
    }

    private sealed class SesionPrivilegiadaAusente : ISesionPrivilegiadaActual
    {
        public Task<SesionPrivilegiadaActiva?> ObtenerAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<SesionPrivilegiadaActiva?>(null);

        public Task<SesionPrivilegiadaActiva?> RevalidarAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<SesionPrivilegiadaActiva?>(null);
    }
}
