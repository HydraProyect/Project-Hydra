using CaeManager.Application.Common;
using CaeManager.Application.Documentos.Eventos;
using CaeManager.Application.Gestiones.Eventos;
using CaeManager.Application.Gestiones.Queries.GestionesAutomaticas;
using CaeManager.Application.Plataforma;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Gestiones;
using CaeManager.Domain.Integraciones;
using CaeManager.Domain.Tenants;
using CaeManager.Domain.Trabajadores;
using CaeManager.Infrastructure.Autorizacion;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Interceptors;
using CaeManager.Infrastructure.Persistence.Repositories;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace CaeManager.IntegrationTests.Gestiones;

/// <summary>
/// La Gestión automática y sus pasos <b>como <c>cae_app_runtime</c></b>, con los interceptores de sellado y de sesión
/// RLS y el <see cref="AlcanceDatosService"/> real (mismo arnés que <c>RespuestaSugeridaBajoRlsTests</c>). Dos Tenants
/// propietarios con un Documento de Trabajador acreditado ante un acceso de plataforma cada uno; la sesión es de un
/// Administrador del primero: lo que se mide es la frontera entre Tenants, no la cartera (esa la cubren las pruebas
/// de Application con el alcance por Centro, Trabajador y Empresa).
/// </summary>
public class GestionesAutomaticasBajoRlsTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _usuarioId = Guid.NewGuid();
    private CaeManagerDbContext _propietario = null!;
    private TenantActualDeLaPeticion _tenantDeLaPeticion = null!;
    private Guid _tenantSesion;
    private Guid _tenantAjeno;
    private (Guid Documento, Guid Acreditacion) _propio;
    private (Guid Documento, Guid Acreditacion) _ajeno;

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
        var proveedor = new ProveedorPlataformaCae($"prueba-{Guid.NewGuid():N}", "Plataforma de prueba");
        _propietario.Tenants.AddRange(tenantSesion, tenantAjeno);
        _propietario.ProveedoresPlataformaCae.Add(proveedor);
        await _propietario.SaveChangesAsync();
        _tenantSesion = tenantSesion.Id;
        _tenantAjeno = tenantAjeno.Id;

        _propio = await SembrarAsync(tenantSesion.Id, proveedor.Id, "Montajes Springfield");
        _ajeno = await SembrarAsync(tenantAjeno.Id, proveedor.Id, "Montajes Shelbyville");

        _tenantDeLaPeticion = new TenantActualDeLaPeticion(_tenantSesion);
    }

    public async Task DisposeAsync()
    {
        await _propietario.DisposeAsync();
        await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);
    }

    [Fact]
    public async Task La_gestion_nace_en_el_Tenant_del_documento_y_la_de_otro_Tenant_no_se_ve_bajo_RLS()
    {
        await NacerComoPropietarioAsync(_tenantAjeno, _ajeno.Documento);

        await using (var runtime = CrearRuntime())
            await Handler(runtime).Handle(new DocumentoNacidoEvent(_propio.Documento), CancellationToken.None);

        await using (var runtime = CrearRuntime())
        {
            (await runtime.GestionesAutomaticas.IgnoreQueryFilters().CountAsync(g => g.DocumentoId == _ajeno.Documento)).Should().Be(0,
                "sin filtro global de EF, solo RLS puede ocultar la Gestión del Tenant ajeno");
            (await runtime.PasosGestionAutomatica.IgnoreQueryFilters().CountAsync(p => p.AcreditacionId == _ajeno.Acreditacion)).Should().Be(0,
                "ni sus pasos");
            (await runtime.GestionesAutomaticas.IgnoreQueryFilters().CountAsync(g => g.DocumentoId == _propio.Documento)).Should().Be(1,
                "control positivo: la Gestión del Tenant de la sesión sí se ve");
        }

        await using var comprobacion = CrearPropietarioLimpio();
        var propia = await comprobacion.GestionesAutomaticas.IgnoreQueryFilters().Include(g => g.Pasos)
            .SingleAsync(g => g.DocumentoId == _propio.Documento);
        propia.TenantId.Should().Be(_tenantSesion);
        propia.Pasos.Should().ContainSingle().Which.TenantId.Should().Be(_tenantSesion);
        (await comprobacion.GestionesAutomaticas.IgnoreQueryFilters().CountAsync(g => g.DocumentoId == _ajeno.Documento)).Should().Be(1,
            "control del instrumento: la Gestión ajena existe");
    }

    [Fact]
    public async Task La_renovacion_bajo_runtime_sustituye_la_gestion_anterior_y_enlaza_la_nueva()
    {
        await using (var runtime = CrearRuntime())
            await Handler(runtime).Handle(new DocumentoNacidoEvent(_propio.Documento), CancellationToken.None);
        var nuevoDocumento = await RenovarComoPropietarioAsync(_tenantSesion, _propio.Documento);

        await using (var runtime = CrearRuntime())
            await Handler(runtime).Handle(new DocumentoNacidoEvent(nuevoDocumento, _propio.Documento), CancellationToken.None);

        await using var comprobacion = CrearPropietarioLimpio();
        var gestiones = await comprobacion.GestionesAutomaticas.IgnoreQueryFilters()
            .Where(g => g.TenantId == _tenantSesion).ToListAsync();
        var anterior = gestiones.Single(g => g.DocumentoId == _propio.Documento);
        var nueva = gestiones.Single(g => g.DocumentoId == nuevoDocumento);
        anterior.SustituidaEnUtc.Should().NotBeNull("el UPDATE de la sustitución pasa el permiso y el WITH CHECK bajo runtime");
        anterior.SustituidaPorGestionId.Should().Be(nueva.Id);
        nueva.Origen.Should().Be(OrigenGestionAutomatica.RenovacionDocumento);
    }

    [Fact]
    public async Task El_evento_con_un_Documento_de_otro_Tenant_no_crea_nada()
    {
        await using (var runtime = CrearRuntime())
        {
            var handler = Handler(runtime);
            await handler.Handle(new DocumentoNacidoEvent(_ajeno.Documento), CancellationToken.None);
            // Control: el mismo handler y el mismo contexto sí crean la del Documento propio, así que el cero de
            // abajo no es un fallo tragado del arnés.
            await handler.Handle(new DocumentoNacidoEvent(_propio.Documento), CancellationToken.None);
        }

        await using (var runtime = CrearRuntime())
        {
            (await runtime.AcreditacionesDocumentoPlataforma.IgnoreQueryFilters().CountAsync(a => a.Id == _propio.Acreditacion))
                .Should().Be(1, "control positivo: la acreditación del Tenant de la sesión sí se ve");
            (await runtime.AcreditacionesDocumentoPlataforma.IgnoreQueryFilters().CountAsync(a => a.Id == _ajeno.Acreditacion))
                .Should().Be(0, "el cero sale de que RLS no deja leer la acreditación ajena, no de la FK compuesta al guardar");
        }

        await using var comprobacion = CrearPropietarioLimpio();
        (await comprobacion.GestionesAutomaticas.IgnoreQueryFilters().Select(g => g.DocumentoId).ToListAsync())
            .Should().Equal([_propio.Documento]);
    }

    [Fact]
    public async Task Descartar_deja_el_contexto_sin_la_gestion_nueva_ni_la_sustitucion_pendiente()
    {
        await using (var runtime = CrearRuntime())
            await Handler(runtime).Handle(new DocumentoNacidoEvent(_propio.Documento), CancellationToken.None);

        await using (var runtime = CrearRuntime())
        {
            var repositorio = new GestionAutomaticaRepository(runtime);
            var anterior = (await repositorio.ObtenerPorDocumentoIdAsync(_propio.Documento))!;
            anterior.Sustituir(null, DateTime.UtcNow);
            var nueva = GestionAutomatica.Nacer(_propio.Documento, OrigenGestionAutomatica.RenovacionDocumento,
                [_propio.Acreditacion, Guid.NewGuid()], null)!;
            repositorio.Agregar(nueva);
            runtime.ChangeTracker.HasChanges().Should().BeTrue("control: antes de descartar hay escrituras pendientes");

            repositorio.Descartar([nueva, anterior]);

            runtime.ChangeTracker.HasChanges().Should().BeFalse();
            runtime.ChangeTracker.Entries<GestionAutomatica>().Should().BeEmpty();
            runtime.ChangeTracker.Entries<PasoGestionAutomatica>().Should().BeEmpty();
            (await runtime.SaveChangesAsync()).Should().Be(0, "el siguiente guardado no reintenta nada");
        }

        await using var comprobacion = CrearPropietarioLimpio();
        (await comprobacion.GestionesAutomaticas.IgnoreQueryFilters().SingleAsync(g => g.DocumentoId == _propio.Documento))
            .SustituidaEnUtc.Should().BeNull();
    }

    [Fact]
    public async Task La_consulta_de_una_gestion_de_otro_Tenant_por_Id_no_devuelve_nada()
    {
        var ajena = await NacerComoPropietarioAsync(_tenantAjeno, _ajeno.Documento);
        var propia = await NacerComoPropietarioAsync(_tenantSesion, _propio.Documento);

        await using var runtime = CrearRuntime();
        var alcance = Alcance(runtime);
        var detalle = await new ObtenerGestionAutomaticaQueryHandler(runtime, runtime, runtime, runtime, runtime, runtime, runtime, alcance)
            .Handle(new ObtenerGestionAutomaticaQuery(ajena), CancellationToken.None);
        var listado = await new ObtenerGestionesAutomaticasQueryHandler(runtime, runtime, runtime, runtime, runtime, runtime, runtime, alcance)
            .Handle(new ObtenerGestionesAutomaticasQuery(), CancellationToken.None);

        detalle.Should().BeNull();
        listado.Filas.Select(f => f.Id).Should().Equal([propia],
            "control positivo: la Gestión del Tenant de la sesión sí sale, y la ajena no");
    }

    [Fact]
    public async Task Una_fila_con_el_Tenant_de_otro_no_entra_por_la_politica()
    {
        await using var runtime = CrearRuntime();

        var excepcion = await Record.ExceptionAsync(() => runtime.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO "GestionesAutomaticas"
                ("Id", "TenantId", "DocumentoId", "Origen", "Version", "CreadoEnUtc", "EstaEliminado")
            VALUES ({Guid.NewGuid()}, {_tenantAjeno}, {_ajeno.Documento}, 0, {Guid.NewGuid()}, now(), false)
            """));

        excepcion.Should().BeOfType<PostgresException>()
            .Which.SqlState.Should().Be("42501", "el WITH CHECK de aislamiento_tenant debe rechazar la fila antes que la FK");
    }

    [Fact]
    public async Task Un_paso_no_puede_colgar_de_una_acreditacion_de_otro_Tenant()
    {
        await using (var runtime = CrearRuntime())
            await Handler(runtime).Handle(new DocumentoNacidoEvent(_propio.Documento), CancellationToken.None);
        await using var comprobacion = CrearPropietarioLimpio();
        var gestionId = await comprobacion.GestionesAutomaticas.IgnoreQueryFilters()
            .Where(g => g.DocumentoId == _propio.Documento).Select(g => g.Id).SingleAsync();

        await using var otraSesion = CrearRuntime();
        var excepcion = await Record.ExceptionAsync(() => otraSesion.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO "PasosGestionAutomatica" ("Id", "TenantId", "GestionAutomaticaId", "AcreditacionId")
            VALUES ({Guid.NewGuid()}, {_tenantSesion}, {gestionId}, {_ajeno.Acreditacion})
            """));

        excepcion.Should().BeOfType<PostgresException>()
            .Which.SqlState.Should().Be("23503", "la clave foránea compuesta por Tenant no casa con la acreditación ajena");
    }

    [Fact]
    public async Task El_indice_unico_impide_dos_gestiones_del_mismo_documento()
    {
        await NacerComoPropietarioAsync(_tenantSesion, _propio.Documento);

        await using var runtime = CrearRuntime();
        var excepcion = await Record.ExceptionAsync(() => runtime.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO "GestionesAutomaticas"
                ("Id", "TenantId", "DocumentoId", "Origen", "Version", "CreadoEnUtc", "EstaEliminado")
            VALUES ({Guid.NewGuid()}, {_tenantSesion}, {_propio.Documento}, 0, {Guid.NewGuid()}, now(), false)
            """));

        excepcion.Should().BeOfType<PostgresException>().Which.SqlState.Should().Be("23505");
    }

    private CrearGestionAutomaticaAlNacerDocumentoHandler Handler(CaeManagerDbContext runtime) => new(
        new GestionAutomaticaRepository(runtime), runtime, runtime, Usuario(), runtime, TimeProvider.System,
        NullLogger<CrearGestionAutomaticaAlNacerDocumentoHandler>.Instance);

    private async Task<Guid> NacerComoPropietarioAsync(Guid tenantId, Guid documentoId)
    {
        using var ambito = AmbitoTenantExplicito.Establecer(tenantId);
        var acreditaciones = await _propietario.AcreditacionesDocumentoPlataforma
            .Where(a => a.DocumentoId == documentoId).Select(a => a.Id).ToListAsync();
        var gestion = GestionAutomatica.Nacer(documentoId, OrigenGestionAutomatica.AltaDocumento, acreditaciones, null)!;
        _propietario.GestionesAutomaticas.Add(gestion);
        await _propietario.SaveChangesAsync();
        return gestion.Id;
    }

    /// <summary>Como la renovación con archivo: nace un Documento nuevo, el anterior queda sustituido por él y el
    /// nuevo se acredita ante el mismo acceso de plataforma.</summary>
    private async Task<Guid> RenovarComoPropietarioAsync(Guid tenantId, Guid documentoAnteriorId)
    {
        using var ambito = AmbitoTenantExplicito.Establecer(tenantId);
        var anterior = await _propietario.Documentos.SingleAsync(d => d.Id == documentoAnteriorId);
        var canalId = await _propietario.AcreditacionesDocumentoPlataforma
            .Where(a => a.DocumentoId == documentoAnteriorId).Select(a => a.CanalGestionDocumentalId).SingleAsync();
        var hoy = DiaDeNegocio.Hoy();
        var nuevo = Documento.DeTrabajador(anterior.TrabajadorId!.Value, anterior.TipoDocumentoId, hoy, VigenciaDocumento.VenceEl(hoy.AddYears(1)));
        _propietario.Documentos.Add(nuevo);
        await _propietario.SaveChangesAsync();
        anterior.SustituirPor(nuevo, MotivoSustitucionDocumento.Renovacion, DateTime.UtcNow);
        _propietario.AcreditacionesDocumentoPlataforma.Add(new AcreditacionDocumentoPlataforma(nuevo.Id, canalId));
        await _propietario.SaveChangesAsync();
        return nuevo.Id;
    }

    private async Task<(Guid Documento, Guid Acreditacion)> SembrarAsync(Guid tenantId, Guid proveedorId, string razonSocial)
    {
        using var ambito = AmbitoTenantExplicito.Establecer(tenantId);
        var empresa = new Empresa(razonSocial, "B87654323");
        var cliente = Empresa.CrearComoCliente($"Cliente de {razonSocial}", "B12345674", false, null, null);
        var trabajador = Trabajador.DeEmpresa(empresa.Id, "Laura", "Gil", "77189989B");
        var tipo = new TipoDocumento("Reconocimiento médico", 12, aplicaVencimientoAutomatico: true, orden: 1,
            AmbitoAplicacion.Trabajador, RequisitoDocumental.Si);
        var centro = new Centro(cliente.Id, empresa.Id, "Planta Norte");
        _propietario.Empresas.AddRange(empresa, cliente);
        _propietario.Trabajadores.Add(trabajador);
        _propietario.TiposDocumento.Add(tipo);
        _propietario.Centros.Add(centro);
        await _propietario.SaveChangesAsync();

        var canal = CanalGestionDocumental.DePlataforma(centro.Id, "Gestión general", proveedorId, null, null, null);
        var hoy = DiaDeNegocio.Hoy();
        var documento = Documento.DeTrabajador(trabajador.Id, tipo.Id, hoy, VigenciaDocumento.VenceEl(hoy.AddYears(1)));
        _propietario.CanalesGestionDocumental.Add(canal);
        _propietario.Documentos.Add(documento);
        await _propietario.SaveChangesAsync();

        var acreditacion = new AcreditacionDocumentoPlataforma(documento.Id, canal.Id);
        _propietario.AcreditacionesDocumentoPlataforma.Add(acreditacion);
        await _propietario.SaveChangesAsync();
        return (documento.Id, acreditacion.Id);
    }

    private CurrentUserServiceFalso Usuario() => new(_usuarioId, Roles.Administrador, tenantOrigenId: _tenantSesion);

    private AlcanceDatosService Alcance(CaeManagerDbContext contexto) => new(
        contexto, Usuario(), _tenantDeLaPeticion, new SesionPrivilegiadaAusente());

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
