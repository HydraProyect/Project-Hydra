using CaeManager.Application.Common;
using CaeManager.Application.Dashboard.Queries;
using CaeManager.Application.DependencyInjection;
using CaeManager.Application.Operaciones;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Common;
using CaeManager.Domain.Comunicaciones;
using CaeManager.Domain.Configuracion;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Operaciones;
using CaeManager.Domain.Reclamaciones;
using CaeManager.Domain.Tenants;
using CaeManager.Domain.Trabajadores;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Operaciones;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CaeManager.IntegrationTests.Dashboard;

/// <summary>
/// <c>ObtenerCuadroDireccionQuery</c> contra PostgreSQL real con dos Tenants beneficiarios: cada fila del
/// cuadro tiene solo los datos de su Tenant (ninguna consulta cruza el filtro global, tampoco las tres que
/// leen filas eliminadas con <c>IgnoreQueryFilters()</c> y comparan el <c>TenantId</c> a mano), el total es
/// la suma, y las definiciones (Trabajador activo sin Asignaciones, baja = eliminación, solicitud = primer
/// entrante de una conversación viva) se traducen a SQL con el mismo resultado que en memoria. El Operador CAE
/// de origen es la Consultora, que también es Tenant beneficiario de sí misma; Ibertec le delega.
/// </summary>
public class CuadroDireccionMultiTenantTests : IAsyncLifetime
{
    private const string NombreGestora = "Gestora de prueba";

    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _usuario = Guid.NewGuid();
    private CaeManagerDbContext _dbContext = null!;

    private Guid _tenantConsultora;
    private Guid _tenantIbertec;

    public async Task InitializeAsync()
    {
        var tenantActual = new TenantActualPorAmbito();
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual))
            .Options;

        _dbContext = new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
        await _dbContext.Database.MigrateAsync();

        var tenantConsultora = new Tenant("Consultora Demo");
        var tenantIbertec = new Tenant("Ibertec S.A.");
        _dbContext.Tenants.AddRange(tenantConsultora, tenantIbertec);

        var delegacion = new DelegacionTenant(tenantConsultora.Id, tenantIbertec.Id);
        _dbContext.DelegacionesTenant.Add(delegacion);
        _dbContext.AsignacionesOperadorDelegadoConRevocadas.Add(new AsignacionOperadorDelegado(delegacion.Id, _usuario, "DireccionCae"));
        await _dbContext.SaveChangesAsync();

        _tenantConsultora = tenantConsultora.Id;
        _tenantIbertec = tenantIbertec.Id;

        await SembrarConsultoraAsync();
        await SembrarIbertecAsync();
    }

    public async Task DisposeAsync()
    {
        await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);
        await _dbContext.DisposeAsync();
    }

    [Fact]
    public async Task Cada_Tenant_beneficiario_ve_solo_lo_suyo_y_el_total_es_la_suma()
    {
        var cuadro = await PedirMesEnCursoAsync(ConstruirServicios());

        cuadro.SinCifras.Should().BeNull();
        cuadro.Estado.Should().Be(EstadoCifraMensual.Parcial, "es el mes en curso");
        cuadro.TenantsSinAlcanceCompleto.Should().Be(0);
        cuadro.Tenants.Should().HaveCount(2);

        var consultora = cuadro.Tenants.Single(t => t.TenantId == _tenantConsultora).Cifras;
        consultora.TrabajadoresActivos.Valor.Should().Be(2, "sin Asignaciones siguen dados de alta");
        consultora.AltasTrabajadores.Valor.Should().Be(2);
        consultora.BajasTrabajadores.Valor.Should().Be(0);
        consultora.CentrosActivos.Valor.Should().Be(1);
        consultora.AltasCentros.Valor.Should().Be(1);
        consultora.DocumentosGestionados.Valor.Should().Be(0);
        consultora.Requerimientos.Valor.Should().Be(0);
        consultora.Solicitudes.Total.Valor.Should().Be(0);

        var ibertec = cuadro.Tenants.Single(t => t.TenantId == _tenantIbertec).Cifras;
        ibertec.TrabajadoresActivos.Valor.Should().Be(3, "4 dados de alta, 1 eliminado (baja por RNT/ITA o a mano)");
        ibertec.AltasTrabajadores.Valor.Should().Be(4, "el eliminado también se dio de alta este mes");
        ibertec.BajasTrabajadores.Valor.Should().Be(1);
        ibertec.CentrosActivos.Valor.Should().Be(2);
        ibertec.AltasCentros.Valor.Should().Be(3);
        ibertec.DocumentosGestionados.Valor.Should().Be(2, "el Documento eliminado después también se gestionó este mes");
        ibertec.Requerimientos.Valor.Should().Be(1, "la reclamación enviada hace 40 días es de otro mes");
        ibertec.Solicitudes.Total.Valor.Should().Be(1,
            "una conversación con dos entrantes es una solicitud; la que empezó el mes pasado y la eliminada no cuentan");
        ibertec.Cumplimiento.Porcentaje.Should().Be(100, "su único Documento vivo de Trabajador está vigente");
        ibertec.Cumplimiento.SinDatos.Should().BeFalse();

        cuadro.Total.TrabajadoresActivos.Valor.Should().Be(5);
        cuadro.Total.BajasTrabajadores.Valor.Should().Be(1);
        cuadro.Total.CentrosActivos.Valor.Should().Be(3);
        cuadro.Total.DocumentosGestionados.Valor.Should().Be(2);
        cuadro.Total.Solicitudes.Total.Valor.Should().Be(1);
        cuadro.Tenants.Should().OnlyContain(t => t.PrincipalDeCartera == null, "ninguna Asignación de Cartera es principal");
    }

    [Fact]
    public async Task Las_filas_eliminadas_de_un_Tenant_no_se_cuentan_en_el_otro()
    {
        // El handler lee Trabajadores, Centros y Documentos con IgnoreQueryFilters y vuelve a acotar con
        // .Where(TenantId == Tenant del ámbito). Ibertec tiene 4 Trabajadores (1 eliminado), 3 Centros (1
        // eliminado) y 2 Documentos (1 eliminado); la Consultora, 2, 1 y ninguno. Sin ese Where la Consultora
        // sumaría las filas de Ibertec: esta base de pruebas conecta con el propietario de las tablas, al que
        // RLS no le aplica, así que el Where es lo único que lo impide aquí (en ejecución RLS es la segunda red).
        var cuadro = await PedirMesEnCursoAsync(ConstruirServicios());

        var consultora = cuadro.Tenants.Single(t => t.TenantId == _tenantConsultora).Cifras;
        consultora.TrabajadoresActivos.Valor.Should().Be(2);
        consultora.AltasTrabajadores.Valor.Should().Be(2);
        consultora.BajasTrabajadores.Valor.Should().Be(0, "la baja es de Ibertec");
        consultora.CentrosActivos.Valor.Should().Be(1);
        consultora.AltasCentros.Valor.Should().Be(1);
        consultora.DocumentosGestionados.Valor.Should().Be(0, "los Documentos, vivos y eliminados, son de Ibertec");

        var ibertec = cuadro.Tenants.Single(t => t.TenantId == _tenantIbertec).Cifras;
        ibertec.AltasTrabajadores.Valor.Should().Be(4);
        ibertec.AltasCentros.Valor.Should().Be(3);
    }

    [Fact]
    public async Task Sin_alcance_del_Tenant_entero_el_Tenant_no_sale_en_el_cuadro()
    {
        // Una lista explícita (vacía) de Clientes: no es acceso total ni cartera universal.
        var cuadro = await PedirMesEnCursoAsync(ConstruirServicios(alcance: new AlcanceDatosServiceFalso(clienteIds: [])));

        cuadro.Tenants.Should().BeEmpty();
        cuadro.TenantsSinAlcanceCompleto.Should().Be(2);
        cuadro.Total.TrabajadoresActivos.Valor.Should().Be(0);
    }

    [Fact]
    public async Task Direccion_CAE_ve_el_Gestor_CAE_principal_con_su_nombre_en_su_Tenant_beneficiario()
    {
        var gestora = Guid.NewGuid();
        await DesignarPrincipalAsync(gestora, "GestorCae");

        var cuadro = await PedirMesEnCursoAsync(ConstruirServicios(
            perfiles: new() { [_usuario] = "DireccionCae" }, nombres: new() { [gestora] = NombreGestora }));

        cuadro.Tenants.Single(t => t.TenantId == _tenantIbertec).PrincipalDeCartera
            .Should().Be(new PrincipalDeCarteraDto(gestora, NombreGestora, "GestorCae"));
        cuadro.Tenants.Single(t => t.TenantId == _tenantConsultora).PrincipalDeCartera
            .Should().BeNull("el Operador CAE no tiene Asignación de Operación externa sobre sí mismo");
    }

    [Fact]
    public async Task En_un_relevo_el_principal_es_el_Coordinador_CAE_y_sale_con_su_rol()
    {
        var coordinadora = Guid.NewGuid();
        await DesignarPrincipalAsync(coordinadora, "CoordinadorCae");

        var cuadro = await PedirMesEnCursoAsync(ConstruirServicios(
            perfiles: new() { [_usuario] = "CoordinadorCae" }, nombres: new() { [coordinadora] = "Coordinadora de prueba" }));

        cuadro.Tenants.Single(t => t.TenantId == _tenantIbertec).PrincipalDeCartera
            .Should().Be(new PrincipalDeCarteraDto(coordinadora, "Coordinadora de prueba", "CoordinadorCae"));
    }

    [Fact]
    public async Task Sin_rol_de_negocio_en_la_sesion_no_se_ve_el_principal()
    {
        // Soporte TALVEG nunca es Operador CAE: aunque su cuenta tuviera un perfil que asume, sin rol efectivo no lo ve.
        var gestora = Guid.NewGuid();
        await DesignarPrincipalAsync(gestora, "GestorCae");

        var cuadro = await PedirMesEnCursoAsync(ConstruirServicios(
            rol: null, perfiles: new() { [_usuario] = "DireccionCae" }, nombres: new() { [gestora] = NombreGestora }));

        cuadro.Tenants.Should().HaveCount(2, "las cifras no dependen del rol: el alcance lo decide IAlcanceDatosService");
        cuadro.Tenants.Should().OnlyContain(t => t.PrincipalDeCartera == null);
    }

    [Fact]
    public async Task Un_Gestor_CAE_no_ve_el_principal()
    {
        // El perfil Gestor CAE no está entre los que asumen (Coordinador CAE, Dirección CAE, Administrador).
        var gestora = Guid.NewGuid();
        await DesignarPrincipalAsync(gestora, "GestorCae");

        var cuadro = await PedirMesEnCursoAsync(ConstruirServicios(
            rol: "GestorCae", perfiles: new() { [_usuario] = "GestorCae" }, nombres: new() { [gestora] = NombreGestora }));

        cuadro.Tenants.Should().NotBeEmpty();
        cuadro.Tenants.Should().OnlyContain(t => t.PrincipalDeCartera == null);
    }

    [Fact]
    public async Task Un_mes_futuro_o_no_valido_sale_vacio_con_su_motivo_y_sin_lanzar()
    {
        using var servicios = ConstruirServicios();
        var mediator = servicios.GetRequiredService<IMediator>();
        var siguiente = DiaDeNegocio.Hoy().AddMonths(1);

        var futuro = await mediator.Send(new ObtenerCuadroDireccionQuery(siguiente.Year, siguiente.Month));
        futuro.SinCifras.Should().Be(CuadroDireccionSinCifras.MesFuturo);
        futuro.Tenants.Should().BeEmpty();

        var noValido = await mediator.Send(new ObtenerCuadroDireccionQuery(2026, 13));
        noValido.SinCifras.Should().Be(CuadroDireccionSinCifras.MesNoValido);
        noValido.Tenants.Should().BeEmpty();

        var fueraDeRango = await mediator.Send(new ObtenerCuadroDireccionQuery(int.MaxValue, 1));
        fueraDeRango.SinCifras.Should().Be(CuadroDireccionSinCifras.MesNoValido);
    }

    [Fact]
    public async Task Sin_Tenant_en_el_ambito_la_medicion_falla_cerrada()
    {
        // Las lecturas con IgnoreQueryFilters no tienen con qué comparar el TenantId: lanza en vez de contar todo.
        using var servicios = ConstruirServicios(tenantActual: new TenantActualNulo());

        var pedir = () => PedirMesEnCursoAsync(servicios);

        await pedir.Should().ThrowAsync<InvalidOperationException>();
    }

    private static async Task<CuadroDireccionDto> PedirMesEnCursoAsync(ServiceProvider servicios)
    {
        using (servicios)
        {
            var hoy = DiaDeNegocio.Hoy();
            return await servicios.GetRequiredService<IMediator>().Send(new ObtenerCuadroDireccionQuery(hoy.Year, hoy.Month));
        }
    }

    /// <summary>La Asignación de Operación externa de la Consultora sobre Ibertec, con su cartera principal.</summary>
    private async Task DesignarPrincipalAsync(Guid usuarioId, string rol)
    {
        using var ambito = AmbitoTenantExplicito.Establecer(_tenantConsultora);
        var ahora = DateTime.UtcNow;
        var operacion = AsignacionOperacion.Externa(
            _tenantIbertec, _tenantConsultora, ServicioCae.Outbound, AmbitoAsignacion.Universal, ahora.AddDays(-2), null, ahora);
        var cartera = AsignacionCartera.Externa(operacion, usuarioId, rol, AmbitoAsignacion.Universal, ahora.AddDays(-1), null, ahora);
        cartera.DesignarPrincipal();
        _dbContext.AsignacionesOperacion.Add(operacion);
        _dbContext.AsignacionesCartera.Add(cartera);
        await _dbContext.SaveChangesAsync();
    }

    private ServiceProvider ConstruirServicios(
        IAlcanceDatosService? alcance = null,
        string? rol = "DireccionCae",
        Dictionary<Guid, string>? perfiles = null,
        Dictionary<Guid, string>? nombres = null,
        ITenantActual? tenantActual = null)
    {
        var usuario = new CurrentUserServiceFalso(_usuario, rol, tenantOrigenId: _tenantConsultora);
        var servicios = new ServiceCollection();
        servicios.AddApplication();
        servicios.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        servicios.AddSingleton(tenantActual ?? new TenantActualPorAmbito());
        servicios.AddSingleton<IUnitOfWork>(_dbContext);
        servicios.AddSingleton<CaeManager.Application.Empresas.IEmpresasQueryContext>(_dbContext);
        servicios.AddSingleton<CaeManager.Application.Proyectos.IRequisitosEventoQueryContext>(_dbContext);
        servicios.AddSingleton<CaeManager.Application.Subcontratas.ISubcontratasQueryContext>(_dbContext);
        servicios.AddSingleton<CaeManager.Application.Centros.ICentrosQueryContext>(_dbContext);
        servicios.AddSingleton<CaeManager.Application.Trabajadores.ITrabajadoresQueryContext>(_dbContext);
        servicios.AddSingleton<CaeManager.Application.TiposDocumento.ITiposDocumentoQueryContext>(_dbContext);
        servicios.AddSingleton<CaeManager.Application.Documentos.IDocumentosQueryContext>(_dbContext);
        servicios.AddSingleton<CaeManager.Application.Asignaciones.IAsignacionesQueryContext>(_dbContext);
        servicios.AddSingleton<CaeManager.Application.Visitas.IVisitasQueryContext>(_dbContext);
        servicios.AddSingleton<CaeManager.Application.Vehiculos.IVehiculosQueryContext>(_dbContext);
        servicios.AddSingleton<CaeManager.Application.Configuracion.IConfiguracionQueryContext>(_dbContext);
        servicios.AddSingleton<CaeManager.Application.Tenants.ITenantsQueryContext>(_dbContext);
        servicios.AddSingleton<CaeManager.Application.Operaciones.IOperacionesQueryContext>(_dbContext);
        servicios.AddSingleton<CaeManager.Application.Proyectos.IProyectosQueryContext>(_dbContext);
        servicios.AddSingleton<CaeManager.Application.Reclamaciones.IReclamacionesQueryContext>(_dbContext);
        servicios.AddSingleton<CaeManager.Application.Comunicaciones.IComunicacionesQueryContext>(_dbContext);
        servicios.AddSingleton<ICatalogoIncorporacionCartera>(new CatalogoIncorporacionCartera(_dbContext, usuario));
        servicios.AddSingleton<IDirectorioUsuariosService>(new DirectorioUsuariosFalso(perfiles ?? [], nombres ?? []));
        servicios.AddSingleton(TimeProvider.System);
        servicios.AddSingleton(alcance ?? new AlcanceDatosServiceFalso());
        servicios.AddSingleton<ICurrentUserService>(usuario);
        return servicios.BuildServiceProvider();
    }

    /// <summary>
    /// Un instante del mes en curso no posterior a ahora, a la fracción <paramref name="fraccion"/> del tramo ya
    /// transcurrido: la siembra cae dentro del mes aunque el test corra en su primera hora.
    /// </summary>
    private static DateTime EnElMesEnCurso(double fraccion)
    {
        var hoy = DiaDeNegocio.Hoy();
        var inicio = new MesCuadroDireccion(hoy.Year, hoy.Month).InicioUtc;
        var ahora = DateTime.UtcNow;
        return inicio + TimeSpan.FromTicks((long)((ahora - inicio).Ticks * fraccion));
    }

    private static DateTime EnElMesAnterior()
    {
        var hoy = DiaDeNegocio.Hoy();
        return new MesCuadroDireccion(hoy.Year, hoy.Month).InicioUtc.AddDays(-10);
    }

    private async Task SembrarConsultoraAsync()
    {
        using var ambito = AmbitoTenantExplicito.Establecer(_tenantConsultora);

        var (cliente, empresa) = await SembrarEmpresasAsync("Consultora");
        _dbContext.Centros.Add(new Centro(cliente.Id, empresa.Id, "Consultora Centro 0"));
        _dbContext.Trabajadores.Add(Trabajador.DeEmpresa(empresa.Id, "Consultora0", "Apellido", GenerarDni(0)));
        _dbContext.Trabajadores.Add(Trabajador.DeEmpresa(empresa.Id, "Consultora1", "Apellido", GenerarDni(1)));
        await _dbContext.SaveChangesAsync();
    }

    private async Task SembrarIbertecAsync()
    {
        using var ambito = AmbitoTenantExplicito.Establecer(_tenantIbertec);

        var (cliente, empresa) = await SembrarEmpresasAsync("Ibertec");
        var centros = Enumerable.Range(0, 3).Select(i => new Centro(cliente.Id, empresa.Id, $"Ibertec Centro {i}")).ToList();
        var trabajadores = Enumerable.Range(0, 4).Select(i => Trabajador.DeEmpresa(empresa.Id, $"Ibertec{i}", "Apellido", GenerarDni(10 + i))).ToList();
        var tipo = new TipoDocumento("EPI", null, aplicaVencimientoAutomatico: false, 1, AmbitoAplicacion.Trabajador, requerido: RequisitoDocumental.Si);
        _dbContext.Centros.AddRange(centros);
        _dbContext.Trabajadores.AddRange(trabajadores);
        _dbContext.TiposDocumento.Add(tipo);
        await _dbContext.SaveChangesAsync();

        // La baja que propone la detección de RNT/ITA (ResolverDeteccionAusenteCommand) es este mismo borrado lógico.
        trabajadores[3].MarcarComoEliminado(Guid.NewGuid());
        centros[2].MarcarComoEliminado(Guid.NewGuid());

        var hoy = DiaDeNegocio.Hoy();
        var documento = Documento.DeTrabajador(trabajadores[0].Id, tipo.Id, hoy, VigenciaDocumento.VenceEl(hoy.AddMonths(6)));
        var documentoEliminado = Documento.DeTrabajador(trabajadores[1].Id, tipo.Id, hoy, VigenciaDocumento.VenceEl(hoy.AddMonths(6)));
        _dbContext.Documentos.AddRange(documento, documentoEliminado);
        await _dbContext.SaveChangesAsync();
        documentoEliminado.MarcarComoEliminado(Guid.NewGuid());
        await _dbContext.SaveChangesAsync();

        _dbContext.ReclamacionesDocumentales.Add(ReclamacionDocumental.ParaCliente(cliente.Id, Guid.NewGuid(), "cliente@ibertec.test", EnElMesEnCurso(0.5), [documento.Id]));
        _dbContext.ReclamacionesDocumentales.Add(ReclamacionDocumental.ParaCliente(cliente.Id, Guid.NewGuid(), "cliente@ibertec.test", EnElMesAnterior(), [documento.Id]));

        // Dos entrantes en la misma conversación: una sola solicitud.
        var conVariosEntrantes = new Conversacion("Alta de trabajadores", cliente.Id);
        conVariosEntrantes.AgregarMensaje(DireccionMensaje.Entrante, CanalConversacion.Correo, "cliente@ibertec.test", "<p>Primero</p>", EnElMesEnCurso(0.2));
        conVariosEntrantes.AgregarMensaje(DireccionMensaje.Saliente, CanalConversacion.Correo, "gestora@consultora.test", "<p>Respuesta</p>", EnElMesEnCurso(0.4));
        conVariosEntrantes.AgregarMensaje(DireccionMensaje.Entrante, CanalConversacion.Correo, "cliente@ibertec.test", "<p>Segundo</p>", EnElMesEnCurso(0.6));

        // Empezó el mes anterior: su solicitud es de otro mes aunque reciba un entrante en este.
        var deOtroMes = new Conversacion("Renovación", cliente.Id);
        deOtroMes.AgregarMensaje(DireccionMensaje.Entrante, CanalConversacion.Correo, "cliente@ibertec.test", "<p>Antiguo</p>", EnElMesAnterior());
        deOtroMes.AgregarMensaje(DireccionMensaje.Entrante, CanalConversacion.Correo, "cliente@ibertec.test", "<p>Nuevo</p>", EnElMesEnCurso(0.8));

        // Eliminada: no es demanda.
        var eliminada = new Conversacion("Spam", cliente.Id);
        eliminada.AgregarMensaje(DireccionMensaje.Entrante, CanalConversacion.Correo, "spam@spam.test", "<p>Oferta</p>", EnElMesEnCurso(0.3));
        eliminada.MarcarComoEliminado(Guid.NewGuid());

        _dbContext.Conversaciones.AddRange(conVariosEntrantes, deOtroMes, eliminada);
        await _dbContext.SaveChangesAsync();
    }

    private async Task<(Empresa Cliente, Empresa Empresa)> SembrarEmpresasAsync(string prefijo)
    {
        var cliente = Empresa.CrearComoCliente($"{prefijo} Cliente", "B12345674", false, null, null);
        var empresa = new Empresa($"{prefijo} Empresa");
        _dbContext.Empresas.AddRange(cliente, empresa);
        _dbContext.ParametrosSistema.Add(new ParametroSistema(umbralAmbarDias: 30, umbralRojoDias: 15));
        await _dbContext.SaveChangesAsync();
        return (cliente, empresa);
    }

    private static string GenerarDni(int numero)
    {
        const string letrasControl = "TRWAGMYFPDXBNJZSQVHLCKE";
        return $"{numero:D8}{letrasControl[numero % 23]}";
    }

    private sealed class TenantActualPorAmbito : ITenantActual
    {
        public Guid? TenantId => AmbitoTenantExplicito.TenantIdActual;
    }

    private sealed class TenantActualNulo : ITenantActual
    {
        public Guid? TenantId => null;
    }

    /// <summary>Directorio con un perfil por cuenta (el rol activo en el Operador CAE) y los nombres visibles dados.</summary>
    private sealed class DirectorioUsuariosFalso(Dictionary<Guid, string> perfiles, Dictionary<Guid, string> nombres)
        : IDirectorioUsuariosService
    {
        public Task<bool> EsVisibleEnTenantActualAsync(Guid usuarioId, CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task<IReadOnlyDictionary<Guid, string>> ObtenerNombresVisiblesAsync(
            IReadOnlyCollection<Guid> usuarioIds, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<Guid, string>>(
                usuarioIds.Where(nombres.ContainsKey).ToDictionary(id => id, id => nombres[id]));

        public Task<Guid?> ObtenerTenantDeUsuarioAsync(Guid usuarioId, CancellationToken cancellationToken = default) =>
            Task.FromResult<Guid?>(null);

        public Task<bool> EsCuentaActivaConRolAsync(Guid usuarioId, Guid tenantId, string rol, CancellationToken cancellationToken = default) =>
            Task.FromResult(perfiles.TryGetValue(usuarioId, out var perfil) && perfil == rol);

        public Task<IReadOnlyList<Guid>> ObtenerCuentasActivasConRolAsync(Guid tenantId, string rol, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Guid>>(perfiles.Where(p => p.Value == rol).Select(p => p.Key).ToList());
    }
}
