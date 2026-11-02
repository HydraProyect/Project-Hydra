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
                ("Id", "TenantId", "ConversacionId", "MensajeOrigenId", "Origen", "Texto", "Estado", "GeneradaPorUsuarioId",
                 "GeneradaEnUtc", "Version", "CreadoEnUtc", "EstaEliminado")
            VALUES ({Guid.NewGuid()}, {_tenantAjeno}, {_conversacionAjena}, {_mensajeAjeno}, 1, 'Texto', 3, {_usuarioId},
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

    /// <summary>Control positivo: el ciclo completo persiste bajo RLS, también el descarte de la anterior.</summary>
    [Fact]
    public async Task Generar_insertar_y_confirmar_persisten_y_la_nueva_descarta_la_anterior()
    {
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
        (await despues.ComprobarEnvioAsync(_conversacionPropia, "Escrito a mano", CancellationToken.None)).EsExitoso.Should().BeTrue();
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
    [InlineData("""UPDATE "RespuestasSugeridas" SET "GeneradaPorUsuarioId" = gen_random_uuid()""")]
    [InlineData("""UPDATE "RespuestasSugeridas" SET "TenantId" = gen_random_uuid()""")]
    [InlineData("""DELETE FROM "RespuestasSugeridas" """)]
    public async Task La_aplicacion_no_puede_reescribir_el_texto_ni_borrar(string orden)
    {
        await using var runtime = CrearRuntime();

        var excepcion = await Record.ExceptionAsync(() => runtime.Database.ExecuteSqlRawAsync(orden));

        excepcion.Should().BeOfType<PostgresException>().Which.SqlState.Should().Be("42501");
    }

    private GenerarRespuestaSugeridaCommandHandler Generar(CaeManagerDbContext runtime)
    {
        var alcance = Alcance(runtime);
        return new GenerarRespuestaSugeridaCommandHandler(
            runtime, new RespuestaSugeridaRepository(runtime),
            new RellenoMacroRespuesta(runtime, runtime, runtime, runtime, runtime, PendientesDeReclamacionFabrica.Crear(runtime, alcance), alcance),
            new RedaccionFija(), new InstruccionHabilitada(), _tenantDeLaPeticion, alcance, Usuario(),
            new TransaccionDeComando(runtime), runtime, runtime);
    }

    private ResolverRespuestaSugeridaCommandHandler Resolver(CaeManagerDbContext runtime) => new(
        runtime, new RespuestaSugeridaRepository(runtime), Alcance(runtime), Usuario(), runtime);

    private ObtenerRespuestaSugeridaQueryHandler Obtener(CaeManagerDbContext runtime) => new(
        runtime, Alcance(runtime), PoliticaNotaInternaPruebas.Con(Roles.Administrador));

    private CurrentUserServiceFalso Usuario() => new(_usuarioId, Roles.Administrador, tenantOrigenId: _tenantSesion);

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
                new TenantRlsConnectionInterceptor(_tenantDeLaPeticion, new SinClienteActivo(), Usuario(), BaseDatosPostgresDePruebas.FirmanteContextoRls))
            .Options;
        return new CaeManagerDbContext(opciones, new EphemeralDataProtectionProvider(), _tenantDeLaPeticion);
    }

    private sealed class RedaccionFija : IRedaccionRespuestaIa
    {
        public Task<Result<string>> RedactarAsync(string asunto, string cuerpoUltimoEntrante, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Exito("Recibido, gracias."));
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
