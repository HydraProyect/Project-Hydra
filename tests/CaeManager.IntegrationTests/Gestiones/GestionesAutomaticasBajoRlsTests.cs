using CaeManager.Application.Common;
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
using Npgsql;
using Xunit;

namespace CaeManager.IntegrationTests.Gestiones;

/// <summary>
/// La Gestión automática y sus pasos <b>como <c>cae_app_runtime</c></b>, con los interceptores de sellado y de sesión
/// RLS y el <see cref="AlcanceDatosService"/> real (mismo arnés que <c>RespuestaSugeridaBajoRlsTests</c>). Dos Tenants
/// propietarios con un Documento de Trabajador acreditado ante un acceso de plataforma cada uno; la sesión es de un
/// Administrador del primero: lo que se mide es la frontera entre Tenants, no la cartera (esa la cubren las pruebas
/// de Application con el alcance por Centro, Trabajador y Empresa). Los actos (alta y renovación con archivo) se
/// reproducen como los Commands: el Documento, su acreditación y la Gestión en un solo <c>SaveChangesAsync</c>.
/// </summary>
public class GestionesAutomaticasBajoRlsTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _usuarioId = Guid.NewGuid();
    private CaeManagerDbContext _propietario = null!;
    private TenantActualDeLaPeticion _tenantDeLaPeticion = null!;
    private Guid _tenantSesion;
    private Guid _tenantAjeno;
    private Sembrado _propio = null!;
    private Sembrado _ajeno = null!;

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
    public async Task La_gestion_nace_en_el_mismo_guardado_que_el_alta_en_el_Tenant_del_documento_y_la_ajena_no_se_ve()
    {
        await NacerComoPropietarioAsync(_tenantAjeno, _ajeno.Documento);

        Guid nacido;
        await using (var runtime = CrearRuntime())
            nacido = (await AltaConArchivoAsync(runtime, _propio)).Id;

        await using (var runtime = CrearRuntime())
        {
            (await runtime.GestionesAutomaticas.IgnoreQueryFilters().CountAsync(g => g.DocumentoId == _ajeno.Documento)).Should().Be(0,
                "sin filtro global de EF, solo RLS puede ocultar la Gestión del Tenant ajeno");
            (await runtime.PasosGestionAutomatica.IgnoreQueryFilters().CountAsync(p => p.AcreditacionId == _ajeno.Acreditacion)).Should().Be(0,
                "ni sus pasos");
            (await runtime.GestionesAutomaticas.IgnoreQueryFilters().CountAsync(g => g.DocumentoId == nacido)).Should().Be(1,
                "control positivo: la Gestión del Tenant de la sesión sí se ve");
        }

        await using var comprobacion = CrearPropietarioLimpio();
        var propia = await comprobacion.GestionesAutomaticas.IgnoreQueryFilters().Include(g => g.Pasos)
            .SingleAsync(g => g.DocumentoId == nacido);
        propia.TenantId.Should().Be(_tenantSesion);
        var paso = propia.Pasos.Should().ContainSingle().Subject;
        paso.TenantId.Should().Be(_tenantSesion);
        (await comprobacion.AcreditacionesDocumentoPlataforma.IgnoreQueryFilters().SingleAsync(a => a.Id == paso.AcreditacionId))
            .DocumentoId.Should().Be(nacido, "el paso es la acreditación que nació en el mismo guardado");
        (await comprobacion.GestionesAutomaticas.IgnoreQueryFilters().CountAsync(g => g.DocumentoId == _ajeno.Documento)).Should().Be(1,
            "control del instrumento: la Gestión ajena existe");
    }

    [Fact]
    public async Task La_renovacion_bajo_runtime_sustituye_la_gestion_anterior_en_su_mismo_guardado()
    {
        await NacerComoPropietarioAsync(_tenantSesion, _propio.Documento);

        Guid nuevoDocumento;
        await using (var runtime = CrearRuntime())
        {
            var anterior = await runtime.Documentos.SingleAsync(d => d.Id == _propio.Documento);
            nuevoDocumento = (await RenovacionConArchivoAsync(runtime, anterior, _propio.Canal)).Id;
        }

        await using var comprobacion = CrearPropietarioLimpio();
        var gestiones = await comprobacion.GestionesAutomaticas.IgnoreQueryFilters()
            .Where(g => g.TenantId == _tenantSesion).ToListAsync();
        var gestionAnterior = gestiones.Single(g => g.DocumentoId == _propio.Documento);
        var nueva = gestiones.Single(g => g.DocumentoId == nuevoDocumento);
        gestionAnterior.SustituidaEnUtc.Should().NotBeNull("el UPDATE de la sustitución pasa el permiso y el WITH CHECK bajo runtime");
        gestionAnterior.SustituidaPorGestionId.Should().Be(nueva.Id);
        nueva.Origen.Should().Be(OrigenGestionAutomatica.RenovacionDocumento);
    }

    [Fact]
    public async Task Dos_renovaciones_a_la_vez_del_mismo_documento_dejan_una_sola_gestion_nueva_y_una_sola_sustitucion()
    {
        // Hallazgo 4 de la primera ronda de Codex: la sustitución va en la transacción de la renovación, protegida por la
        // versión del Documento anterior. La segunda renovación no se confirma, ni su Gestión con ella.
        await NacerComoPropietarioAsync(_tenantSesion, _propio.Documento);
        await using var primera = CrearRuntime();
        await using var segunda = CrearRuntime();
        var anteriorEnLaPrimera = await primera.Documentos.SingleAsync(d => d.Id == _propio.Documento);
        var anteriorEnLaSegunda = await segunda.Documentos.SingleAsync(d => d.Id == _propio.Documento);

        var ganadora = await RenovacionConArchivoAsync(primera, anteriorEnLaPrimera, _propio.Canal);
        var excepcion = await Record.ExceptionAsync(() => RenovacionConArchivoAsync(segunda, anteriorEnLaSegunda, _propio.Canal));

        excepcion.Should().BeAssignableTo<DbUpdateException>("la versión del anterior ya cambió");
        await using var comprobacion = CrearPropietarioLimpio();
        var gestiones = await comprobacion.GestionesAutomaticas.IgnoreQueryFilters()
            .Where(g => g.TenantId == _tenantSesion).ToListAsync();
        gestiones.Select(g => g.DocumentoId).Should().BeEquivalentTo([_propio.Documento, ganadora.Id],
            "la Gestión de la renovación perdedora no se confirmó con ella");
        gestiones.Single(g => g.DocumentoId == _propio.Documento).SustituidaPorGestionId
            .Should().Be(gestiones.Single(g => g.DocumentoId == ganadora.Id).Id);
    }

    [Fact]
    public async Task Un_rechazo_confirmado_mientras_la_renovacion_decide_no_deja_abierta_la_gestion_del_documento_historico()
    {
        // Hallazgo 2 de la segunda ronda de Codex: la renovación lee los pasos de la Gestión anterior todos hechos y no la
        // marca; antes de su commit, otra conexión rechaza esa acreditación. El rechazo no toca la versión del Documento,
        // así que las dos se confirman. La lectura no debe dar por abierta la Gestión del Documento histórico.
        using (AmbitoTenantExplicito.Establecer(_tenantSesion))
        {
            var validada = await _propietario.AcreditacionesDocumentoPlataforma.SingleAsync(a => a.Id == _propio.Acreditacion);
            validada.MarcarSubida();
            validada.MarcarAceptada(VigenciaEnPlataforma.SinConfirmar);
            await _propietario.SaveChangesAsync();
        }
        var gestionAnteriorId = await NacerComoPropietarioAsync(_tenantSesion, _propio.Documento);

        await using var renovacion = CrearRuntime();
        var anterior = await renovacion.Documentos.SingleAsync(d => d.Id == _propio.Documento);
        var hoy = DiaDeNegocio.Hoy();
        var nuevo = anterior.NuevoDelMismoTitular(hoy, VigenciaDocumento.VenceEl(hoy.AddYears(1)), $"tenant/{Guid.NewGuid():N}.pdf", null, null);
        renovacion.Documentos.Add(nuevo);
        anterior.SustituirPor(nuevo, MotivoSustitucionDocumento.Renovacion, DateTime.UtcNow);
        new AcreditacionDocumentoPlataformaRepository(renovacion).Agregar(new AcreditacionDocumentoPlataforma(nuevo.Id, _propio.Canal));
        (await AltaAcreditacionesDePrueba.GestionAutomatica(renovacion, Usuario()).AgregarAsync(nuevo, anterior))
            .Should().NotBeNull("control: la renovación hace nacer su Gestión");

        await using (var rechazo = CrearRuntime())
        {
            var acreditacion = await rechazo.AcreditacionesDocumentoPlataforma.SingleAsync(a => a.Id == _propio.Acreditacion);
            acreditacion.Rechazar(CausaRechazoAcreditacion.Ilegible, "Documento ilegible", DateTime.UtcNow);
            await rechazo.SaveChangesAsync();
        }
        await renovacion.SaveChangesAsync();

        await using (var comprobacion = CrearPropietarioLimpio())
            (await comprobacion.GestionesAutomaticas.IgnoreQueryFilters().SingleAsync(g => g.Id == gestionAnteriorId))
                .SustituidaEnUtc.Should().BeNull("control del instrumento: la renovación decidió con los pasos hechos y la carrera ocurrió");

        await using var consulta = CrearRuntime();
        var listado = await new ObtenerGestionesAutomaticasQueryHandler(consulta, consulta, consulta, consulta, consulta, consulta, consulta, Alcance(consulta))
            .Handle(new ObtenerGestionesAutomaticasQuery(), CancellationToken.None);
        var filaAnterior = listado.Filas.Single(f => f.Id == gestionAnteriorId);
        filaAnterior.TieneRechazo.Should().BeTrue("control: el rechazo se confirmó");
        filaAnterior.Estado.Should().Be(EstadoGestionAutomatica.Sustituida);
        listado.Filas
            .Where(f => f.Estado is EstadoGestionAutomatica.EnCurso or EstadoGestionAutomatica.PendienteDeValidar)
            .Select(f => f.DocumentoId)
            .Should().Equal([nuevo.Id], "solo queda abierta la Gestión del Documento vigente");
    }

    [Fact]
    public async Task Los_pasos_son_solo_las_acreditaciones_agregadas_del_Documento_nacido_en_el_ChangeTracker_real()
    {
        await using (var runtime = CrearRuntime())
        {
            // En el mismo contexto: una acreditación ya guardada y cargada (sin cambios) de otro Documento y otro Documento
            // nuevo con su acreditación agregada. Ninguna de las dos es del Documento que nace.
            (await runtime.AcreditacionesDocumentoPlataforma.SingleAsync(a => a.Id == _propio.Acreditacion)).Should().NotBeNull();
            var hoy = DiaDeNegocio.Hoy();
            var otro = Documento.DeTrabajador(_propio.Trabajador, _propio.Tipo, hoy, VigenciaDocumento.VenceEl(hoy.AddYears(1)), "tenant/otro.pdf");
            runtime.Documentos.Add(otro);
            new AcreditacionDocumentoPlataformaRepository(runtime).Agregar(new AcreditacionDocumentoPlataforma(otro.Id, _propio.Canal));

            var nacido = await AltaConArchivoAsync(runtime, _propio);

            var gestion = await new GestionAutomaticaRepository(runtime).ObtenerPorDocumentoIdAsync(nacido.Id);
            var acreditacionDelNacido = await runtime.AcreditacionesDocumentoPlataforma
                .Where(a => a.DocumentoId == nacido.Id).Select(a => a.Id).SingleAsync();
            gestion!.Pasos.Select(p => p.AcreditacionId).Should().Equal([acreditacionDelNacido]);

            // Tras el guardado, la acreditación del propio nacido ya no es «agregada»: ningún acto posterior en el mismo
            // contexto la vuelve a tomar como paso (fija el filtro por estado Added, no solo el de Documento).
            (await AltaAcreditacionesDePrueba.GestionAutomatica(runtime, Usuario()).AgregarAsync(nacido, null))
                .Should().BeNull();
        }
    }

    [Fact]
    public async Task El_servicio_no_sustituye_la_gestion_de_un_Documento_de_otro_Tenant()
    {
        await NacerComoPropietarioAsync(_tenantAjeno, _ajeno.Documento);
        await NacerComoPropietarioAsync(_tenantSesion, _propio.Documento);
        Documento documentoAjeno;
        using (AmbitoTenantExplicito.Establecer(_tenantAjeno))
            documentoAjeno = await _propietario.Documentos.AsNoTracking().SingleAsync(d => d.Id == _ajeno.Documento);

        await using (var runtime = CrearRuntime())
        {
            // Aunque un acto le pasara como anterior un Documento de otro Tenant, RLS no le deja leer su Gestión.
            var hoy = DiaDeNegocio.Hoy();
            var nacido = Documento.DeTrabajador(_propio.Trabajador, _propio.Tipo, hoy, VigenciaDocumento.VenceEl(hoy.AddYears(1)), "tenant/nuevo.pdf");
            runtime.Documentos.Add(nacido);
            await ActoAsync(runtime, nacido, documentoAjeno, _propio.Canal);
        }

        await using (var comprobacion = CrearPropietarioLimpio())
            (await comprobacion.GestionesAutomaticas.IgnoreQueryFilters().SingleAsync(g => g.DocumentoId == _ajeno.Documento))
                .SustituidaEnUtc.Should().BeNull();

        await using (var runtime = CrearRuntime())
        {
            var repositorio = new GestionAutomaticaRepository(runtime);
            (await repositorio.ObtenerPorDocumentoIdAsync(_ajeno.Documento)).Should().BeNull(
                "RLS no deja leer la Gestión ajena, así que ningún acto puede sustituirla");
            (await repositorio.ObtenerPorDocumentoIdAsync(_propio.Documento)).Should().NotBeNull(
                "control positivo: el mismo repositorio y la misma sesión sí leen la propia");
            (await runtime.Documentos.IgnoreQueryFilters().CountAsync(d => d.Id == _ajeno.Documento)).Should().Be(0,
                "ni el Documento ajeno, del que tendría que partir el acto");
            (await runtime.AcreditacionesDocumentoPlataforma.IgnoreQueryFilters().CountAsync(a => a.Id == _propio.Acreditacion))
                .Should().Be(1, "control positivo: la acreditación del Tenant de la sesión sí se ve");
            (await runtime.AcreditacionesDocumentoPlataforma.IgnoreQueryFilters().CountAsync(a => a.Id == _ajeno.Acreditacion))
                .Should().Be(0, "el cero sale de que RLS no deja leer la acreditación ajena");
        }
    }

    [Fact]
    public async Task La_consulta_de_una_gestion_de_otro_Tenant_por_Id_no_devuelve_nada()
    {
        var ajena = await NacerComoPropietarioAsync(_tenantAjeno, _ajeno.Documento);
        var propia = await NacerComoPropietarioAsync(_tenantSesion, _propio.Documento);

        await using var runtime = CrearRuntime();
        var alcance = Alcance(runtime);
        var detalle = await new ObtenerGestionAutomaticaQueryHandler(runtime, runtime, runtime, runtime, runtime, runtime, runtime, runtime, alcance,
                new SinNotaInterna())
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
        var gestionId = await NacerComoPropietarioAsync(_tenantSesion, _propio.Documento);

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

    /// <summary>
    /// Como <c>CrearDocumentoCommand</c> con archivo: el Documento, su acreditación y la Gestión en un solo guardado.
    /// </summary>
    private async Task<Documento> AltaConArchivoAsync(CaeManagerDbContext runtime, Sembrado sembrado)
    {
        var hoy = DiaDeNegocio.Hoy();
        var nacido = Documento.DeTrabajador(sembrado.Trabajador, sembrado.Tipo, hoy, VigenciaDocumento.VenceEl(hoy.AddYears(1)),
            "tenant/reconocimiento.pdf");
        runtime.Documentos.Add(nacido);
        await ActoAsync(runtime, nacido, sustituido: null, sembrado.Canal);
        return nacido;
    }

    /// <summary>
    /// Como <c>RenovarDocumentoCommand</c> con archivo nuevo: Documento nuevo, sustitución del anterior, acreditación del
    /// nuevo, Gestión nueva y sustitución de la anterior, en un solo guardado.
    /// </summary>
    private async Task<Documento> RenovacionConArchivoAsync(CaeManagerDbContext runtime, Documento anterior, Guid canalId)
    {
        var hoy = DiaDeNegocio.Hoy();
        var nuevo = anterior.NuevoDelMismoTitular(hoy, VigenciaDocumento.VenceEl(hoy.AddYears(1)), $"tenant/{Guid.NewGuid():N}.pdf", null, null);
        runtime.Documentos.Add(nuevo);
        anterior.SustituirPor(nuevo, MotivoSustitucionDocumento.Renovacion, DateTime.UtcNow);
        await ActoAsync(runtime, nuevo, anterior, canalId);
        return nuevo;
    }

    private async Task ActoAsync(CaeManagerDbContext runtime, Documento nacido, Documento? sustituido, Guid canalId)
    {
        new AcreditacionDocumentoPlataformaRepository(runtime).Agregar(new AcreditacionDocumentoPlataforma(nacido.Id, canalId));
        (await AltaAcreditacionesDePrueba.GestionAutomatica(runtime, Usuario()).AgregarAsync(nacido, sustituido))
            .Should().NotBeNull("control: el acto hace nacer su Gestión");
        await runtime.SaveChangesAsync();
    }

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

    private sealed record Sembrado(Guid Documento, Guid Acreditacion, Guid Canal, Guid Trabajador, Guid Tipo);

    private async Task<Sembrado> SembrarAsync(Guid tenantId, Guid proveedorId, string razonSocial)
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
        var documento = Documento.DeTrabajador(trabajador.Id, tipo.Id, hoy, VigenciaDocumento.VenceEl(hoy.AddYears(1)),
            "tenant/reconocimiento-anterior.pdf");
        _propietario.CanalesGestionDocumental.Add(canal);
        _propietario.Documentos.Add(documento);
        await _propietario.SaveChangesAsync();

        var acreditacion = new AcreditacionDocumentoPlataforma(documento.Id, canal.Id);
        _propietario.AcreditacionesDocumentoPlataforma.Add(acreditacion);
        await _propietario.SaveChangesAsync();
        return new Sembrado(documento.Id, acreditacion.Id, canal.Id, trabajador.Id, tipo.Id);
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

    /// <summary>Lo que se prueba aquí es el aislamiento entre Tenants, no la política de la nota interna.</summary>
    private sealed class SinNotaInterna : IPoliticaNotaInterna
    {
        public Task<bool> PuedeLeerAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);

        public Task<bool> TieneAccesoTotalAlTenantAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);
    }

    private sealed class SesionPrivilegiadaAusente : ISesionPrivilegiadaActual
    {
        public Task<SesionPrivilegiadaActiva?> ObtenerAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<SesionPrivilegiadaActiva?>(null);

        public Task<SesionPrivilegiadaActiva?> RevalidarAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<SesionPrivilegiadaActiva?>(null);
    }
}
