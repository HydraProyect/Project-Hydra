using CaeManager.Application.Common;
using CaeManager.Application.Documentos.Commands.ConfirmarVigenciaAcreditacion;
using CaeManager.Application.Documentos.Commands.MarcarAcreditacionNoRequerida;
using CaeManager.Application.Documentos.Commands.RenovarDocumento;
using CaeManager.Application.Documentos.Commands.RestaurarAnotacionAcreditacion;
using CaeManager.Application.Gestiones.Queries.GestionesAutomaticas;
using CaeManager.Domain.Asignaciones;
using CaeManager.Application.Plataforma;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.DocumentosIa;
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
    public async Task El_detalle_de_una_gestion_propia_bajo_RLS_trae_Centro_Asignacion_CIF_y_datos_del_Trabajador()
    {
        // Control positivo de la consulta del detalle (hallazgo de Codex r1): las lecturas nuevas del incremento 1 ejecutadas
        // como cae_app_runtime, no con contextos falsos.
        var inicio = DiaDeNegocio.Hoy().AddDays(-20);
        await CompletarFichaYAsignacionAsync(_tenantSesion, _propio, inicio, "Autorización de residencia y trabajo");
        await CompletarFichaYAsignacionAsync(_tenantAjeno, _ajeno, inicio.AddDays(-5), "Permiso del Tenant ajeno");
        var propia = await NacerComoPropietarioAsync(_tenantSesion, _propio.Documento);
        await NacerComoPropietarioAsync(_tenantAjeno, _ajeno.Documento);

        await using var runtime = CrearRuntime();
        var sinNota = await DetalleAsync(runtime, propia, new SinNotaInterna());
        var conNota = await DetalleAsync(runtime, propia, new ConNotaInterna());

        sinNota.Should().NotBeNull();
        sinNota!.Documento.Id.Should().Be(_propio.Documento);
        sinNota.EmpresaGestionadaCif.Should().Be("B87654323");
        var paso = sinNota.Pasos.Should().ContainSingle().Subject;
        paso.AcreditacionId.Should().Be(_propio.Acreditacion);
        paso.CentroId.Should().Be(_propio.Centro);
        paso.CentroNombre.Should().Be("Planta Norte");
        paso.InicioAsignacion.Should().Be(inicio, "la Asignación del Trabajador titular a ese Centro, no la del Tenant ajeno");
        paso.VersionAcreditacion.Should().NotBe(Guid.Empty, "la pantalla la devuelve al marcar «no lo pide»");

        var trabajador = sinNota.Trabajador!;
        trabajador.Id.Should().Be(_propio.Trabajador);
        trabajador.NombreCompleto.Should().Be("Laura Gil");
        trabajador.Dni.Should().Be("77189989B");
        trabajador.Nacionalidad.Should().Be("Española");
        trabajador.PermisoTrabajoVisible.Should().BeFalse();
        trabajador.TipoPermisoTrabajo.Should().BeNull("sin la política de la nota interna no sale del servidor");

        conNota!.Trabajador!.PermisoTrabajoVisible.Should().BeTrue("control positivo de la política");
        conNota.Trabajador.TipoPermisoTrabajo.Should().Be("Autorización de residencia y trabajo");
    }

    [Fact]
    public async Task Una_vigencia_confirmada_en_otra_sesion_mueve_la_version_y_no_lo_pide_con_la_vista_vieja_no_la_borra()
    {
        // Hallazgo ALTO de Codex (r1) con EF real: es el interceptor de concurrencia quien renueva la versión que la pantalla
        // compara. Dos sesiones como cae_app_runtime sobre la misma acreditación.
        var propia = await NacerComoPropietarioAsync(_tenantSesion, _propio.Documento);
        Guid vista;
        await using (var pantalla = CrearRuntime())
            vista = (await DetalleAsync(pantalla, propia, new SinNotaInterna()))!.Pasos.Single().VersionAcreditacion;

        var confirmada = VigenciaEnPlataforma.VenceEl(DiaDeNegocio.Hoy().AddMonths(6));
        await using (var otra = CrearRuntime())
        {
            var acreditacion = await otra.AcreditacionesDocumentoPlataforma.SingleAsync(a => a.Id == _propio.Acreditacion);
            acreditacion.MarcarAceptada(VigenciaEnPlataforma.SinConfirmar);
            acreditacion.ConfirmarVigencia(confirmada);
            await otra.SaveChangesAsync();
        }

        await using (var runtime = CrearRuntime())
            (await NoPide(runtime).Handle(new MarcarAcreditacionNoRequeridaCommand(_propio.Acreditacion, vista), CancellationToken.None))
                .Error.Codigo.Should().Be("Acreditacion.CambiadaDesdeQueSeAbrio");

        using (AmbitoTenantExplicito.Establecer(_tenantSesion))
        {
            var fila = await _propietario.AcreditacionesDocumentoPlataforma.AsNoTracking().SingleAsync(a => a.Id == _propio.Acreditacion);
            fila.Estado.Should().Be(EstadoAcreditacion.Aceptada);
            fila.Vigencia.Should().Be(confirmada, "la vigencia anotada en la otra sesión sigue ahí");
            fila.Version.Should().NotBe(vista);
        }
    }

    [Fact]
    public async Task Solo_una_vigencia_confirmada_en_otra_sesion_sin_cambiar_el_estado_mueve_la_version_y_no_lo_pide_no_la_borra()
    {
        // Codex r2 (MEDIO): el test anterior cambia también el estado, y habría pasado con la comparación antigua por estado.
        // Aquí el estado visto y el de después son el mismo (Validado); solo cambia la vigencia, con el Command real.
        using (AmbitoTenantExplicito.Establecer(_tenantSesion))
        {
            var validada = await _propietario.AcreditacionesDocumentoPlataforma.SingleAsync(a => a.Id == _propio.Acreditacion);
            validada.MarcarSubida();
            validada.MarcarAceptada(VigenciaEnPlataforma.SinConfirmar);
            await _propietario.SaveChangesAsync();
        }
        var propia = await NacerComoPropietarioAsync(_tenantSesion, _propio.Documento);
        Guid vista;
        await using (var pantalla = CrearRuntime())
        {
            var paso = (await DetalleAsync(pantalla, propia, new SinNotaInterna()))!.Pasos.Single();
            paso.Estado.Should().Be(EstadoAcreditacion.Aceptada, "precondición: la pantalla ve el paso validado");
            vista = paso.VersionAcreditacion;
        }

        var confirmada = VigenciaEnPlataforma.VenceEl(DiaDeNegocio.Hoy().AddMonths(6));
        await using (var otra = CrearRuntime())
            (await new ConfirmarVigenciaAcreditacionCommandHandler(
                    new AcreditacionDocumentoPlataformaRepository(otra), new DocumentoRepository(otra), Alcance(otra), otra, otra, otra)
                .Handle(new ConfirmarVigenciaAcreditacionCommand(_propio.Acreditacion, confirmada), CancellationToken.None))
                .EsExitoso.Should().BeTrue("control: la otra sesión confirma la vigencia");

        await using (var runtime = CrearRuntime())
            (await NoPide(runtime).Handle(new MarcarAcreditacionNoRequeridaCommand(_propio.Acreditacion, vista), CancellationToken.None))
                .Error.Codigo.Should().Be("Acreditacion.CambiadaDesdeQueSeAbrio");

        using (AmbitoTenantExplicito.Establecer(_tenantSesion))
        {
            var fila = await _propietario.AcreditacionesDocumentoPlataforma.AsNoTracking().SingleAsync(a => a.Id == _propio.Acreditacion);
            fila.Estado.Should().Be(EstadoAcreditacion.Aceptada, "el estado no cambió en ningún momento");
            fila.Vigencia.Should().Be(confirmada, "la vigencia anotada en la otra sesión sigue ahí");
            fila.Version.Should().NotBe(vista);
        }
    }

    [Fact]
    public async Task Un_estado_que_cambia_y_vuelve_al_visto_en_otras_sesiones_mueve_la_version_y_no_lo_pide_da_conflicto()
    {
        // Codex r2 (MEDIO), A→B→A con EF real: Pendiente de subir → Subida → Pendiente de subir, cada paso en su sesión.
        var propia = await NacerComoPropietarioAsync(_tenantSesion, _propio.Documento);
        Guid vista;
        await using (var pantalla = CrearRuntime())
            vista = (await DetalleAsync(pantalla, propia, new SinNotaInterna()))!.Pasos.Single().VersionAcreditacion;

        await using (var subida = CrearRuntime())
        {
            (await subida.AcreditacionesDocumentoPlataforma.SingleAsync(a => a.Id == _propio.Acreditacion)).MarcarSubida();
            await subida.SaveChangesAsync();
        }
        await using (var devuelta = CrearRuntime())
        {
            (await devuelta.AcreditacionesDocumentoPlataforma.SingleAsync(a => a.Id == _propio.Acreditacion)).DevolverAPendienteDeSubir();
            await devuelta.SaveChangesAsync();
        }

        await using (var runtime = CrearRuntime())
            (await NoPide(runtime).Handle(new MarcarAcreditacionNoRequeridaCommand(_propio.Acreditacion, vista), CancellationToken.None))
                .Error.Codigo.Should().Be("Acreditacion.CambiadaDesdeQueSeAbrio");

        using (AmbitoTenantExplicito.Establecer(_tenantSesion))
        {
            var fila = await _propietario.AcreditacionesDocumentoPlataforma.AsNoTracking().SingleAsync(a => a.Id == _propio.Acreditacion);
            fila.Estado.Should().Be(EstadoAcreditacion.PendienteDeSubir, "precondición: el estado volvió al que se vio");
            fila.Version.Should().NotBe(vista);
        }
    }

    [Fact]
    public async Task Corregir_el_documento_sin_cambiar_la_acreditacion_mueve_su_version_y_no_lo_pide_con_la_vista_vieja_da_conflicto()
    {
        // Codex r2 (ALTO) con el RenovarDocumentoCommandHandler real como runtime: la acreditación ya está Pendiente de subir,
        // sin vigencia y sin cambio a mano, y la corrección le asigna los mismos valores. Sin renovar la versión en el
        // dominio, EF no la daría por modificada y el interceptor no tocaría su versión.
        var propia = await NacerComoPropietarioAsync(_tenantSesion, _propio.Documento);
        Guid vista;
        await using (var pantalla = CrearRuntime())
        {
            var paso = (await DetalleAsync(pantalla, propia, new SinNotaInterna()))!.Pasos.Single();
            paso.Estado.Should().Be(EstadoAcreditacion.PendienteDeSubir, "precondición: la corrección no cambiará el estado");
            vista = paso.VersionAcreditacion;
        }

        await using (var correccion = CrearRuntime())
            (await CorregirDocumentoAsync(correccion)).Should().Be(_propio.Documento, "precondición: corrige el mismo registro");

        await using (var runtime = CrearRuntime())
            (await NoPide(runtime).Handle(new MarcarAcreditacionNoRequeridaCommand(_propio.Acreditacion, vista), CancellationToken.None))
                .Error.Codigo.Should().Be("Acreditacion.CambiadaDesdeQueSeAbrio");

        using (AmbitoTenantExplicito.Establecer(_tenantSesion))
        {
            var fila = await _propietario.AcreditacionesDocumentoPlataforma.AsNoTracking().SingleAsync(a => a.Id == _propio.Acreditacion);
            fila.Estado.Should().Be(EstadoAcreditacion.PendienteDeSubir, "«no lo pide» no se escribió");
            fila.Vigencia.EstaSinConfirmar.Should().BeTrue();
            fila.Version.Should().NotBe(vista, "la corrección la renovó aunque no cambiara ningún campo");
        }
    }

    [Fact]
    public async Task Tras_renovar_con_archivo_nuevo_no_lo_pide_con_la_vista_vieja_no_marca_la_acreditacion_del_historial()
    {
        // Revisión puente (r2): la renovación con archivo nuevo no toca la acreditación del anterior, cuya versión sigue
        // siendo la que vio la pantalla. La guarda es la del historial (D5/D8), no la de la versión.
        var propia = await NacerComoPropietarioAsync(_tenantSesion, _propio.Documento);
        Guid vista;
        await using (var pantalla = CrearRuntime())
            vista = (await DetalleAsync(pantalla, propia, new SinNotaInterna()))!.Pasos.Single().VersionAcreditacion;

        await using (var renovacion = CrearRuntime())
            await RenovacionConArchivoAsync(renovacion, await renovacion.Documentos.SingleAsync(d => d.Id == _propio.Documento), _propio.Canal);

        await using (var runtime = CrearRuntime())
            (await NoPide(runtime).Handle(new MarcarAcreditacionNoRequeridaCommand(_propio.Acreditacion, vista), CancellationToken.None))
                .Error.Codigo.Should().Be("Documento.EnHistorial");

        using (AmbitoTenantExplicito.Establecer(_tenantSesion))
        {
            var fila = await _propietario.AcreditacionesDocumentoPlataforma.AsNoTracking().SingleAsync(a => a.Id == _propio.Acreditacion);
            fila.Version.Should().Be(vista, "control del instrumento: la versión no se movió, así que solo la guarda del historial para");
            fila.Estado.Should().Be(EstadoAcreditacion.PendienteDeSubir, "la acreditación del historial no cambia");
        }
    }

    [Fact]
    public async Task Con_el_Documento_ya_rastreado_por_el_circuito_una_renovacion_de_otra_sesion_hace_que_no_lo_pide_lo_vea_en_el_historial()
    {
        // Hallazgo 4 (33b156b78): el contexto del circuito vive toda la visita y ya tiene el Documento rastreado de cuando
        // se abrió la ventana. Una lectura rastreada devolvería esa instancia vieja, todavía operativa, y como «no lo pide»
        // no escribe el Documento ningún token pararía la marca sobre el historial. La guarda debe releerlo de la base.
        var propia = await NacerComoPropietarioAsync(_tenantSesion, _propio.Documento);
        await using var circuito = CrearRuntime();
        var vista = (await DetalleAsync(circuito, propia, new SinNotaInterna()))!.Pasos.Single().VersionAcreditacion;
        var rastreado = await circuito.Documentos.SingleAsync(d => d.Id == _propio.Documento);

        await using (var renovacion = CrearRuntime())
            await RenovacionConArchivoAsync(renovacion, await renovacion.Documentos.SingleAsync(d => d.Id == _propio.Documento), _propio.Canal);

        DocumentoOperativo.Es(rastreado).Should().BeTrue(
            "precondición del instrumento: la instancia que rastrea el circuito sigue vieja, sin la sustitución de la otra sesión");
        (await NoPide(circuito).Handle(new MarcarAcreditacionNoRequeridaCommand(_propio.Acreditacion, vista), CancellationToken.None))
            .Error.Codigo.Should().Be("Documento.EnHistorial");

        using (AmbitoTenantExplicito.Establecer(_tenantSesion))
        {
            var fila = await _propietario.AcreditacionesDocumentoPlataforma.AsNoTracking().SingleAsync(a => a.Id == _propio.Acreditacion);
            fila.Version.Should().Be(vista, "control del instrumento: la versión no se movió, así que solo la guarda del historial para");
            fila.Estado.Should().Be(EstadoAcreditacion.PendienteDeSubir, "la acreditación del historial no cambia");
        }
    }

    [Fact]
    public async Task Una_renovacion_confirmada_entre_la_guarda_y_el_guardado_de_no_lo_pide_deshace_la_marca_con_el_historial()
    {
        // Codex r3 (MEDIO) sobre f83535b9e: la guarda vio el Documento vigente y otra sesión lo renueva con archivo nuevo
        // antes del guardado. La renovación no toca esta acreditación (su versión sigue siendo la vista) y «no lo pide» no
        // escribe el Documento: solo la comprobación final, con la fila bloqueada y dentro de la transacción, lo para.
        var propia = await NacerComoPropietarioAsync(_tenantSesion, _propio.Documento);
        Guid vista;
        await using (var pantalla = CrearRuntime())
            vista = (await DetalleAsync(pantalla, propia, new SinNotaInterna()))!.Pasos.Single().VersionAcreditacion;

        await using var runtime = CrearRuntime();
        var entremedias = new OtraEscrituraAntesDeGuardar(runtime, async () =>
        {
            await using var renovacion = CrearRuntime();
            await RenovacionConArchivoAsync(renovacion, await renovacion.Documentos.SingleAsync(d => d.Id == _propio.Documento), _propio.Canal);
        });

        var resultado = await NoPide(runtime, entremedias).Handle(
            new MarcarAcreditacionNoRequeridaCommand(_propio.Acreditacion, vista), CancellationToken.None);

        entremedias.Ejecutada.Should().BeTrue("control del instrumento: la renovación se confirmó entre la guarda y el guardado");
        resultado.Error.Codigo.Should().Be("Documento.EnHistorial");
        using (AmbitoTenantExplicito.Establecer(_tenantSesion))
        {
            var fila = await _propietario.AcreditacionesDocumentoPlataforma.AsNoTracking().SingleAsync(a => a.Id == _propio.Acreditacion);
            fila.Version.Should().Be(vista, "la marca guardada se deshizo con la transacción: ni el estado ni la versión cambian");
            fila.Estado.Should().Be(EstadoAcreditacion.PendienteDeSubir, "la acreditación del historial no cambia");
            (await _propietario.Documentos.AsNoTracking().SingleAsync(d => d.Id == _propio.Documento)).SustituidoEnUtc
                .Should().NotBeNull("control: la renovación de la otra sesión sí se confirmó");
        }
    }

    [Fact]
    public async Task Con_el_Documento_rastreado_con_cambios_pendientes_el_atajo_de_ya_no_requerida_no_responde_exito_sobre_el_historial()
    {
        // Codex r3 (BAJO) sobre f83535b9e: ObtenerActualAsync no relee una instancia rastreada con cambios pendientes y la
        // versión intacta, para no perderlos. Si la guarda la usara, vería el Documento vigente de cuando se abrió la
        // ventana y el atajo de «ya está No requerida» respondería éxito sobre el historial. La guarda lee sin rastrear.
        var propia = await NacerComoPropietarioAsync(_tenantSesion, _propio.Documento);
        await using (var previa = CrearRuntime())
        {
            var vista = (await DetalleAsync(previa, propia, new SinNotaInterna()))!.Pasos.Single().VersionAcreditacion;
            (await NoPide(previa).Handle(new MarcarAcreditacionNoRequeridaCommand(_propio.Acreditacion, vista), CancellationToken.None))
                .EsExitoso.Should().BeTrue("control: la acreditación ya está No requerida antes de la renovación");
        }

        await using var circuito = CrearRuntime();
        var rastreado = await circuito.Documentos.SingleAsync(d => d.Id == _propio.Documento);
        rastreado.ActualizarComentarios("Cambio pendiente del circuito");
        circuito.Entry(rastreado).State.Should().Be(EntityState.Modified,
            "precondición del instrumento: la instancia rastreada arrastra un cambio pendiente con la versión intacta");

        await using (var renovacion = CrearRuntime())
            await RenovacionConArchivoAsync(renovacion, await renovacion.Documentos.SingleAsync(d => d.Id == _propio.Documento), _propio.Canal);

        DocumentoOperativo.Es(rastreado).Should().BeTrue("precondición: la instancia rastreada sigue vieja");
        (await NoPide(circuito).Handle(new MarcarAcreditacionNoRequeridaCommand(_propio.Acreditacion, Guid.NewGuid()), CancellationToken.None))
            .Error.Codigo.Should().Be("Documento.EnHistorial");
    }

    [Fact]
    public async Task Un_no_lo_pide_confirmado_entre_la_lectura_y_el_guardado_de_la_correccion_la_para_con_un_conflicto()
    {
        // La carrera inversa del ALTO de Codex r2: la corrección ya leyó la acreditación (Pendiente de subir, sin vigencia)
        // cuando otra sesión la marca No requerida con su versión vigente. Sin la versión renovada, la corrección no emitiría
        // UPDATE de la acreditación y la No requerida sobreviviría a ella sin aviso.
        Guid versionDocumento;
        using (AmbitoTenantExplicito.Establecer(_tenantSesion))
            versionDocumento = await _propietario.Documentos.AsNoTracking()
                .Where(d => d.Id == _propio.Documento).Select(d => d.Version).SingleAsync();

        await using var correccion = CrearRuntime();
        var entremedias = new OtraEscrituraAntesDeGuardar(correccion, async () =>
        {
            await using var otra = CrearRuntime();
            var version = await otra.AcreditacionesDocumentoPlataforma.AsNoTracking()
                .Where(a => a.Id == _propio.Acreditacion).Select(a => a.Version).SingleAsync();
            (await NoPide(otra).Handle(new MarcarAcreditacionNoRequeridaCommand(_propio.Acreditacion, version), CancellationToken.None))
                .EsExitoso.Should().BeTrue("control: la otra sesión confirma «no lo pide» antes del guardado de la corrección");
        });

        var excepcion = await Record.ExceptionAsync(() => CorregirDocumentoAsync(correccion, entremedias));

        entremedias.Ejecutada.Should().BeTrue("control del instrumento: la carrera ocurrió entre la lectura y el guardado");
        excepcion.Should().BeAssignableTo<DbUpdateConcurrencyException>(
            "la acreditación entra en el UPDATE con la versión que leyó la corrección, y esa ya no está");
        using (AmbitoTenantExplicito.Establecer(_tenantSesion))
        {
            (await _propietario.AcreditacionesDocumentoPlataforma.AsNoTracking().SingleAsync(a => a.Id == _propio.Acreditacion))
                .Estado.Should().Be(EstadoAcreditacion.NoRequerida, "la corrección no se confirmó y no la pisó a ciegas");
            (await _propietario.Documentos.AsNoTracking().Where(d => d.Id == _propio.Documento).Select(d => d.Version).SingleAsync())
                .Should().Be(versionDocumento, "la corrección del Documento tampoco se confirmó: todo o nada");
        }
    }

    /// <summary>
    /// La barrera de escritura medida sin el repositorio ni el filtro global de EF: solo RLS puede impedir que un UPDATE por
    /// Id llegue a la acreditación de otro Tenant propietario (mismo patrón que <c>FijarResponsableBuzonBajoRlsTests</c>).
    /// </summary>
    [Fact]
    public async Task Un_UPDATE_por_Id_de_la_acreditacion_de_otro_Tenant_no_afecta_a_ninguna_fila()
    {
        await using (var runtime = CrearRuntime())
        {
            (await runtime.AcreditacionesDocumentoPlataforma.IgnoreQueryFilters()
                    .Where(a => a.Id == _ajeno.Acreditacion)
                    .ExecuteUpdateAsync(s => s.SetProperty(a => a.Estado, EstadoAcreditacion.NoRequerida)))
                .Should().Be(0, "sin filtro global de EF, solo RLS puede dejar fuera la fila ajena");
            (await runtime.AcreditacionesDocumentoPlataforma.IgnoreQueryFilters()
                    .Where(a => a.Id == _propio.Acreditacion)
                    .ExecuteUpdateAsync(s => s.SetProperty(a => a.Estado, EstadoAcreditacion.NoRequerida)))
                .Should().Be(1, "control positivo: el mismo UPDATE sobre la acreditación propia sí llega a la fila");
        }

        using (AmbitoTenantExplicito.Establecer(_tenantAjeno))
            (await _propietario.AcreditacionesDocumentoPlataforma.AsNoTracking().SingleAsync(a => a.Id == _ajeno.Acreditacion))
                .Estado.Should().Be(EstadoAcreditacion.PendienteDeSubir, "la acreditación del Tenant ajeno no cambia");
    }

    /// <summary>
    /// La frontera entre Tenants de la ventana de la Gestión tal como la ve el código de producción: el filtro global de
    /// EF y la política RLS a la vez (la RLS sola, sin el filtro, la aíslan los tests de inserción de esta clase y el UPDATE
    /// por Id de arriba). La sesión es de un Administrador: la Asignación de Cartera real no se mide aquí.
    /// </summary>
    [Fact]
    public async Task Como_runtime_el_detalle_y_los_tres_comandos_de_la_ventana_no_alcanzan_una_acreditacion_de_otro_Tenant()
    {
        await CompletarFichaYAsignacionAsync(_tenantAjeno, _ajeno, DiaDeNegocio.Hoy(), "Permiso del Tenant ajeno");
        var ajena = await NacerComoPropietarioAsync(_tenantAjeno, _ajeno.Documento);
        var propia = await NacerComoPropietarioAsync(_tenantSesion, _propio.Documento);
        Guid versionAjena;
        using (AmbitoTenantExplicito.Establecer(_tenantAjeno))
            versionAjena = await _propietario.AcreditacionesDocumentoPlataforma.AsNoTracking()
                .Where(a => a.Id == _ajeno.Acreditacion).Select(a => a.Version).SingleAsync();

        await using (var runtime = CrearRuntime())
        {
            (await DetalleAsync(runtime, ajena, new ConNotaInterna())).Should().BeNull();

            var noPide = await NoPide(runtime).Handle(
                new MarcarAcreditacionNoRequeridaCommand(_ajeno.Acreditacion, versionAjena), CancellationToken.None);
            var vigencia = await new ConfirmarVigenciaAcreditacionCommandHandler(
                    new AcreditacionDocumentoPlataformaRepository(runtime), new DocumentoRepository(runtime), Alcance(runtime), runtime, runtime, runtime)
                .Handle(new ConfirmarVigenciaAcreditacionCommand(_ajeno.Acreditacion, VigenciaEnPlataforma.VenceEl(new DateOnly(2020, 1, 1))),
                    CancellationToken.None);
            var restaurar = await new RestaurarAnotacionAcreditacionCommandHandler(
                    new AcreditacionDocumentoPlataformaRepository(runtime), new DocumentoRepository(runtime), Alcance(runtime), runtime, runtime, runtime)
                .Handle(new RestaurarAnotacionAcreditacionCommand(_ajeno.Acreditacion, EstadoAcreditacion.NoRequerida,
                    VigenciaEnPlataforma.SinConfirmar, versionAjena), CancellationToken.None);

            noPide.Error.Codigo.Should().Be("Acreditacion.NoEncontrada");
            vigencia.Error.Codigo.Should().Be("Acreditacion.NoEncontrada");
            restaurar.Error.Codigo.Should().Be("Acreditacion.NoEncontrada");
        }

        using (AmbitoTenantExplicito.Establecer(_tenantAjeno))
        {
            var fila = await _propietario.AcreditacionesDocumentoPlataforma.AsNoTracking().SingleAsync(a => a.Id == _ajeno.Acreditacion);
            fila.Estado.Should().Be(EstadoAcreditacion.PendienteDeSubir, "la acreditación del Tenant ajeno no cambia");
            fila.Vigencia.EstaSinConfirmar.Should().BeTrue();
            fila.Version.Should().Be(versionAjena);
        }

        // Control positivo: el mismo comando, con la versión que da el detalle, sí escribe en la acreditación propia.
        await using (var runtime = CrearRuntime())
        {
            var detalle = await DetalleAsync(runtime, propia, new SinNotaInterna());
            var paso = detalle!.Pasos.Should().ContainSingle().Subject;
            (await NoPide(runtime).Handle(
                    new MarcarAcreditacionNoRequeridaCommand(paso.AcreditacionId, paso.VersionAcreditacion), CancellationToken.None))
                .EsExitoso.Should().BeTrue();
        }

        using (AmbitoTenantExplicito.Establecer(_tenantSesion))
            (await _propietario.AcreditacionesDocumentoPlataforma.AsNoTracking().SingleAsync(a => a.Id == _propio.Acreditacion))
                .Estado.Should().Be(EstadoAcreditacion.NoRequerida);
    }

    /// <summary>
    /// El handler real sobre <paramref name="runtime"/>, con su transacción real. <paramref name="unitOfWork"/> deja meter
    /// otra escritura justo antes del guardado (<see cref="OtraEscrituraAntesDeGuardar"/>).
    /// </summary>
    private MarcarAcreditacionNoRequeridaCommandHandler NoPide(CaeManagerDbContext runtime, IUnitOfWork? unitOfWork = null) => new(
        new AcreditacionDocumentoPlataformaRepository(runtime), new DocumentoRepository(runtime), Alcance(runtime), runtime, runtime,
        unitOfWork ?? runtime, new TransaccionDeComando(runtime));

    /// <summary>
    /// <see cref="RenovarDocumentoCommand"/> sin archivo nuevo con el handler real sobre <paramref name="runtime"/>: corrige
    /// la fecha de emisión del mismo registro y reinicia sus acreditaciones. <paramref name="unitOfWork"/> deja meter otra
    /// escritura justo antes del guardado (<see cref="OtraEscrituraAntesDeGuardar"/>). Devuelve el Id del Documento en uso.
    /// </summary>
    private async Task<Guid> CorregirDocumentoAsync(CaeManagerDbContext runtime, IUnitOfWork? unitOfWork = null)
    {
        var version = await runtime.Documentos.AsNoTracking()
            .Where(d => d.Id == _propio.Documento).Select(d => d.Version).SingleAsync();
        var resultado = await new RenovarDocumentoCommandHandler(
                new DocumentoRepository(runtime), runtime, Alcance(runtime), runtime, new ColaAnalisisSinUso(), Usuario(),
                new AcreditacionDocumentoPlataformaRepository(runtime), AltaAcreditacionesDePrueba.Con(runtime),
                new SerializacionAltasSinBloqueo(), new PublisherFalso(), unitOfWork ?? runtime,
                new VerificadorArchivoAlmacenadoPermisivo(), AltaAcreditacionesDePrueba.GestionAutomatica(runtime, Usuario()))
            .Handle(new RenovarDocumentoCommand(_propio.Documento, DiaDeNegocio.Hoy().AddDays(-2), null, null,
                "Fecha de emisión corregida", version), CancellationToken.None);
        resultado.EsExitoso.Should().BeTrue("control: la corrección del Documento se aplica");
        return resultado.Valor;
    }

    /// <summary>El guardado de <paramref name="contexto"/>, precedido de otra escritura confirmada en otra sesión.</summary>
    private sealed class OtraEscrituraAntesDeGuardar(CaeManagerDbContext contexto, Func<Task> otraEscritura) : IUnitOfWork
    {
        public bool Ejecutada { get; private set; }

        public async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            await otraEscritura();
            Ejecutada = true;
            return await contexto.SaveChangesAsync(cancellationToken);
        }
    }

    private sealed class ColaAnalisisSinUso : ITrabajoAnalisisDocumentoRepository
    {
        public void Agregar(TrabajoAnalisisDocumento trabajo)
        {
        }

        public Task<TrabajoAnalisisDocumento?> ObtenerSiguientePendienteAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<TrabajoAnalisisDocumento?>(null);

        public Task<TrabajoAnalisisDocumento?> ReclamarSiguientePendienteAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<TrabajoAnalisisDocumento?>(null);

        public Task<IReadOnlyList<TrabajoAnalisisDocumento>> ObtenerEstancadosAsync(
            TimeSpan umbral, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<TrabajoAnalisisDocumento>>([]);

        public Task<int> ContarActivosAsync(CancellationToken cancellationToken = default) => Task.FromResult(0);
    }

    private Task<GestionAutomaticaDetalleDto?> DetalleAsync(CaeManagerDbContext runtime, Guid gestionId, IPoliticaNotaInterna politica) =>
        new ObtenerGestionAutomaticaQueryHandler(runtime, runtime, runtime, runtime, runtime, runtime, runtime, runtime, Alcance(runtime), politica)
            .Handle(new ObtenerGestionAutomaticaQuery(gestionId), CancellationToken.None);

    /// <summary>La ficha del Trabajador sembrado (nacionalidad y permiso de trabajo) y su Asignación al Centro del acceso.</summary>
    private async Task CompletarFichaYAsignacionAsync(Guid tenantId, Sembrado sembrado, DateOnly inicio, string permiso)
    {
        using var ambito = AmbitoTenantExplicito.Establecer(tenantId);
        var trabajador = await _propietario.Trabajadores.SingleAsync(t => t.Id == sembrado.Trabajador);
        trabajador.ActualizarFicha(null, null, null, "Española", null, null, null, null, permiso, null, null);
        _propietario.Asignaciones.Add(new Asignacion(sembrado.Trabajador, sembrado.Centro, inicio));
        await _propietario.SaveChangesAsync();
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

    private sealed record Sembrado(Guid Documento, Guid Acreditacion, Guid Canal, Guid Trabajador, Guid Tipo, Guid Centro);

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
        return new Sembrado(documento.Id, acreditacion.Id, canal.Id, trabajador.Id, tipo.Id, centro.Id);
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

    /// <summary>Quien consulta sí lee la nota interna: el tipo de permiso de trabajo sale.</summary>
    private sealed class ConNotaInterna : IPoliticaNotaInterna
    {
        public Task<bool> PuedeLeerAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task<bool> TieneAccesoTotalAlTenantAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
    }

    private sealed class SesionPrivilegiadaAusente : ISesionPrivilegiadaActual
    {
        public Task<SesionPrivilegiadaActiva?> ObtenerAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<SesionPrivilegiadaActiva?>(null);

        public Task<SesionPrivilegiadaActiva?> RevalidarAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<SesionPrivilegiadaActiva?>(null);
    }
}
