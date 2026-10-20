using CaeManager.Application.Common;
using CaeManager.Application.Dashboard.Queries;
using CaeManager.Application.DependencyInjection;
using CaeManager.Application.Operaciones;
using CaeManager.Domain.Auditoria;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Common;
using CaeManager.Domain.Configuracion;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Tenants;
using CaeManager.Domain.Trabajadores;
using CaeManager.Domain.Visitas;
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
/// <c>ObtenerExtranjerosDelMesQuery</c> (exportación «Exportar extranjeros del mes» del Cuadro de dirección, decisión de
/// Chris, 2026-10-20) contra PostgreSQL real con dos Tenants beneficiarios: la Consultora (Operador CAE de origen y
/// beneficiaria de sí misma) e Ibertec, que le delega. Comprueba el recorrido por Tenant, que ninguna fila cruza de un
/// Tenant a otro (las lecturas con <c>IgnoreQueryFilters()</c> vuelven a acotar por <c>TenantId</c>; esta base conecta con
/// el propietario de las tablas, sin RLS, así que ese Where es lo único que lo impide aquí), el criterio de extranjero
/// (NIE no, pasaporte sí, la casilla manda), la unidad (una fila por Trabajador y Visita no cancelada del mes) y el
/// Gestor CAE de la asignación con su respaldo en la auditoría.
/// </summary>
public class ExtranjerosDelMesMultiTenantTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _usuario = Guid.NewGuid();
    private readonly Guid _marta = Guid.NewGuid();
    private readonly Guid _luis = Guid.NewGuid();
    private readonly DateTime _asignadaEnUtc = new(2026, 1, 5, 9, 30, 0, DateTimeKind.Utc);
    private CaeManagerDbContext _dbContext = null!;

    private Guid _tenantConsultora;
    private Guid _tenantIbertec;
    private DateOnly _mes;

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
        var hoy = DiaDeNegocio.Hoy();
        _mes = new DateOnly(hoy.Year, hoy.Month, 1).AddMonths(-1);

        await SembrarConsultoraAsync();
        await SembrarIbertecAsync();
    }

    public async Task DisposeAsync()
    {
        await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);
        await _dbContext.DisposeAsync();
    }

    [Fact]
    public async Task Recorre_cada_Tenant_beneficiario_y_cada_fila_es_de_su_Tenant()
    {
        var resultado = await PedirAsync(ConstruirServicios());

        resultado.SinCifras.Should().BeNull();
        resultado.TenantsSinAlcanceCompleto.Should().Be(0);
        resultado.Filas.Where(f => f.TenantId == _tenantConsultora).Select(f => f.NombreYApellidos)
            .Should().BeEquivalentTo(["Ana Consultora"]);
        resultado.Filas.Where(f => f.TenantId == _tenantIbertec).Select(f => f.NombreYApellidos)
            .Should().BeEquivalentTo(["Amina El Idrissi", "Amina El Idrissi", "Yusuf Demir", "Oleg Petrenko"]);

        var consultora = resultado.Filas.Single(f => f.TenantId == _tenantConsultora);
        consultora.TenantNombre.Should().Be("Consultora Demo");
        consultora.Empresa.Should().Be("Consultora Empresa");
        consultora.Cliente.Should().Be("Consultora Cliente");
        consultora.Centro.Should().Be("Consultora Centro");
        resultado.Filas.Where(f => f.TenantId == _tenantIbertec)
            .Should().OnlyContain(f => f.Cliente == "Ibertec Cliente" && f.TenantNombre == "Ibertec S.A.");
    }

    [Fact]
    public async Task El_NIE_no_cuenta_el_pasaporte_si_y_la_casilla_manda()
    {
        var nombres = (await PedirAsync(ConstruirServicios())).Filas.Select(f => f.NombreYApellidos).ToList();

        nombres.Should().NotContain("Nadia Benali", "su NIE no la hace extranjera cobrable");
        nombres.Should().Contain("Yusuf Demir", "tiene NIE pero la casilla está marcada");
        nombres.Should().NotContain("Pierre Martin", "tiene pasaporte pero la casilla está desmarcada (ciudadano de la UE)");
        nombres.Should().Contain("Amina El Idrissi");
    }

    [Fact]
    public async Task Una_fila_por_Visita_no_cancelada_del_mes_y_un_recurrente_en_dos_Centros_da_dos_filas()
    {
        var filas = (await PedirAsync(ConstruirServicios())).Filas
            .Where(f => f.NombreYApellidos == "Amina El Idrissi").ToList();

        filas.Should().HaveCount(2, "la Visita cancelada y la de otro mes no cuentan");
        filas.Select(f => f.Centro).Should().BeEquivalentTo(["Ibertec Centro A", "Ibertec Centro B"]);
        filas.Should().OnlyContain(f => f.FechaIngreso.Year == _mes.Year && f.FechaIngreso.Month == _mes.Month);

        var enA = filas.Single(f => f.Centro == "Ibertec Centro A");
        enA.FechaIngreso.Should().Be(_mes.AddDays(9));
        enA.FechaSalida.Should().Be(_mes.AddDays(11));
        enA.ContactoVisita.Should().Be("Técnico Ruiz");
        enA.Empresa.Should().Be("Ibertec Empresa");
        enA.Subcontrata.Should().BeNull();
        enA.Documento.Should().Be("AB1234567");
        enA.FechaSolicitud.Should().Be(DiaDeNegocio.De(_asignadaEnUtc));
        enA.GestorCae.Should().Be("Marta Gestora", "es quien asignó al Trabajador, no el principal de cartera");
    }

    /// <summary>
    /// Histórico congelado (decisión de Chris, 2026-10-20): la exportación lee la foto de la asignación. Corregir después el
    /// pasaporte de Amina a un NIE no saca del mes sus asignaciones ya hechas ni cambia el documento que muestran; solo la
    /// asignación nueva, hecha tras la corrección, nace sin contar.
    /// </summary>
    [Fact]
    public async Task Corregir_el_documento_a_NIE_tras_asignar_no_altera_el_mes_ya_gestionado()
    {
        using (AmbitoTenantExplicito.Establecer(_tenantIbertec))
        {
            var amina = await _dbContext.Trabajadores.SingleAsync(t => t.Nombre == "Amina");
            amina.CorregirDocumentoIdentidad("Z1234567R");
            var centroA = await _dbContext.Centros.SingleAsync(c => c.Nombre == "Ibertec Centro A");
            var trasLaCorreccion = new Visita(centroA.Id, _mes.AddDays(25), _mes.AddDays(25), null);
            _dbContext.Visitas.Add(trasLaCorreccion);
            await _dbContext.SaveChangesAsync();
            _dbContext.VisitasTrabajadores.Add(new VisitaTrabajador(trasLaCorreccion.Id, amina, _marta));
            await _dbContext.SaveChangesAsync();
        }

        var filas = (await PedirAsync(ConstruirServicios())).Filas
            .Where(f => f.NombreYApellidos == "Amina El Idrissi").ToList();

        filas.Should().HaveCount(2, "las dos asignaciones previas conservan su foto y la nueva nace con el NIE, que no cuenta");
        filas.Should().OnlyContain(f => f.Documento == "AB1234567", "el Excel muestra el documento de la foto, no el de hoy");
    }

    /// <summary>
    /// <c>ContarExtranjerosCobrablesDelMesQuery</c>, el servicio que consume la valoración del Operador CAE, sale de las
    /// mismas filas que el Excel: la unidad que se cobra es la asignación a Visita (un recurrente en dos Centros cuenta dos).
    /// </summary>
    [Fact]
    public async Task El_conteo_del_mes_cuadra_con_el_Excel()
    {
        using var servicios = ConstruirServicios();
        var mediator = servicios.GetRequiredService<IMediator>();

        var excel = await mediator.Send(new ObtenerExtranjerosDelMesQuery(_mes.Year, _mes.Month));
        var conteo = await mediator.Send(new ContarExtranjerosCobrablesDelMesQuery(_mes.Year, _mes.Month));

        conteo.SinCifras.Should().BeNull();
        conteo.TenantsSinAlcanceCompleto.Should().Be(excel.TenantsSinAlcanceCompleto);
        conteo.PorTenant.Sum(t => t.Asignaciones).Should().Be(excel.Filas.Count);
        foreach (var tenant in conteo.PorTenant)
        {
            var filasDelTenant = excel.Filas.Where(f => f.TenantId == tenant.TenantId).ToList();
            tenant.Asignaciones.Should().Be(filasDelTenant.Count);
            tenant.TrabajadoresDistintos.Should().Be(filasDelTenant.Select(f => f.TrabajadorId).Distinct().Count());
        }

        var ibertec = conteo.PorTenant.Single(t => t.TenantId == _tenantIbertec);
        ibertec.Asignaciones.Should().Be(4, "Amina en dos Centros, Yusuf y Oleg");
        ibertec.TrabajadoresDistintos.Should().Be(3);
        conteo.PorTenant.Single(t => t.TenantId == _tenantConsultora).Asignaciones.Should().Be(1);
    }

    [Fact]
    public async Task Sin_dato_en_la_asignacion_el_Gestor_CAE_y_la_fecha_salen_de_la_auditoria()
    {
        var oleg = (await PedirAsync(ConstruirServicios())).Filas.Single(f => f.NombreYApellidos == "Oleg Petrenko");

        oleg.GestorCae.Should().Be("Luis Gestor", "el Actor real del alta auditada de la asignación");
        oleg.Subcontrata.Should().Be("Ibertec Subcontrata");
        oleg.FechaSolicitud.Year.Should().BeGreaterThan(2000, "la fecha del registro de auditoría");
    }

    [Fact]
    public async Task Sin_alcance_del_Tenant_entero_el_Tenant_no_sale_y_se_cuenta()
    {
        var resultado = await PedirAsync(ConstruirServicios(alcance: new AlcanceDatosServiceFalso(clienteIds: [])));

        resultado.Filas.Should().BeEmpty();
        resultado.TenantsSinAlcanceCompleto.Should().Be(2);
    }

    [Fact]
    public async Task Un_Gestor_CAE_no_exporta()
    {
        var accion = () => PedirAsync(ConstruirServicios(rol: "GestorCae"));

        await accion.Should().ThrowAsync<UnauthorizedAccessException>();
    }

    [Fact]
    public async Task Sin_Tenant_en_el_ambito_falla_cerrada()
    {
        var accion = () => PedirAsync(ConstruirServicios(tenantActual: new TenantActualNulo()));

        await accion.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Un_mes_futuro_o_no_valido_sale_vacio_con_su_motivo()
    {
        using var servicios = ConstruirServicios();
        var mediator = servicios.GetRequiredService<IMediator>();
        var siguiente = DiaDeNegocio.Hoy().AddMonths(1);

        (await mediator.Send(new ObtenerExtranjerosDelMesQuery(siguiente.Year, siguiente.Month)))
            .SinCifras.Should().Be(CuadroDireccionSinCifras.MesFuturo);
        (await mediator.Send(new ObtenerExtranjerosDelMesQuery(2026, 13)))
            .SinCifras.Should().Be(CuadroDireccionSinCifras.MesNoValido);
    }

    private async Task<ExtranjerosDelMesDto> PedirAsync(ServiceProvider servicios)
    {
        using (servicios)
            return await servicios.GetRequiredService<IMediator>().Send(new ObtenerExtranjerosDelMesQuery(_mes.Year, _mes.Month));
    }

    private ServiceProvider ConstruirServicios(
        IAlcanceDatosService? alcance = null, string? rol = "DireccionCae", ITenantActual? tenantActual = null)
    {
        var usuario = new CurrentUserServiceFalso(_usuario, rol, tenantOrigenId: _tenantConsultora);
        var servicios = new ServiceCollection();
        servicios.AddApplication();
        servicios.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        servicios.AddSingleton(tenantActual ?? new TenantActualPorAmbito());
        servicios.AddSingleton<IUnitOfWork>(_dbContext);
        servicios.AddSingleton<CaeManager.Application.Dashboard.ICierresCuadroDireccion>(_dbContext);
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
        servicios.AddSingleton<CaeManager.Application.Auditoria.IAuditoriaQueryContext>(_dbContext);
        servicios.AddSingleton<ICatalogoIncorporacionCartera>(new CatalogoIncorporacionCartera(_dbContext, usuario));
        servicios.AddSingleton<IDirectorioUsuariosService>(new DirectorioUsuariosFalso(new()
        {
            [_marta] = "Marta Gestora",
            [_luis] = "Luis Gestor",
        }));
        servicios.AddSingleton(TimeProvider.System);
        servicios.AddSingleton(alcance ?? new AlcanceDatosServiceFalso());
        servicios.AddSingleton<ICurrentUserService>(usuario);
        return servicios.BuildServiceProvider();
    }

    private async Task SembrarConsultoraAsync()
    {
        using var ambito = AmbitoTenantExplicito.Establecer(_tenantConsultora);

        var (cliente, empresa, _) = await SembrarEmpresasAsync("Consultora");
        var centro = new Centro(cliente.Id, empresa.Id, "Consultora Centro");
        var ana = Trabajador.DeEmpresa(empresa.Id, "Ana", "Consultora", "ZX9988776");
        var visita = new Visita(centro.Id, _mes.AddDays(2), _mes.AddDays(3), null);
        _dbContext.Centros.Add(centro);
        _dbContext.Trabajadores.Add(ana);
        _dbContext.Visitas.Add(visita);
        _dbContext.VisitasTrabajadores.Add(new VisitaTrabajador(visita.Id, ana, _marta, _asignadaEnUtc));
        await _dbContext.SaveChangesAsync();
    }

    private async Task SembrarIbertecAsync()
    {
        using var ambito = AmbitoTenantExplicito.Establecer(_tenantIbertec);

        var (cliente, empresa, subcontrata) = await SembrarEmpresasAsync("Ibertec");
        var centroA = new Centro(cliente.Id, empresa.Id, "Ibertec Centro A");
        var centroB = new Centro(cliente.Id, empresa.Id, "Ibertec Centro B");

        var amina = Trabajador.DeEmpresa(empresa.Id, "Amina", "El Idrissi", "AB1234567");
        var nadia = Trabajador.DeEmpresa(empresa.Id, "Nadia", "Benali", "X1234567L");
        var yusuf = Trabajador.DeEmpresa(empresa.Id, "Yusuf", "Demir", "Y1234567X");
        yusuf.FijarMarcaExtranjeroCobrable(true);
        var pierre = Trabajador.DeEmpresa(empresa.Id, "Pierre", "Martin", "FR9876543");
        pierre.FijarMarcaExtranjeroCobrable(false);
        var oleg = Trabajador.DeSubcontrata(subcontrata.Id, "Oleg", "Petrenko", "FE1234567");

        var enA = new Visita(centroA.Id, _mes.AddDays(9), _mes.AddDays(11), null);
        enA.ActualizarContacto("Técnico Ruiz", null, null);
        var enB = new Visita(centroB.Id, _mes.AddDays(15), _mes.AddDays(15), null);
        var cancelada = new Visita(centroB.Id, _mes.AddDays(20), _mes.AddDays(20), null);
        var deOtroMes = new Visita(centroA.Id, _mes.AddMonths(-1).AddDays(9), _mes.AddMonths(-1).AddDays(9), null);

        _dbContext.Centros.AddRange(centroA, centroB);
        _dbContext.Trabajadores.AddRange(amina, nadia, yusuf, pierre, oleg);
        _dbContext.Visitas.AddRange(enA, enB, cancelada, deOtroMes);
        await _dbContext.SaveChangesAsync();

        var olegEnA = new VisitaTrabajador(enA.Id, oleg);
        _dbContext.VisitasTrabajadores.AddRange(
            new VisitaTrabajador(enA.Id, amina, _marta, _asignadaEnUtc),
            new VisitaTrabajador(enB.Id, amina, _marta),
            new VisitaTrabajador(cancelada.Id, amina, _marta),
            new VisitaTrabajador(deOtroMes.Id, amina, _marta),
            new VisitaTrabajador(enA.Id, nadia, _marta),
            new VisitaTrabajador(enB.Id, yusuf, _marta),
            new VisitaTrabajador(enB.Id, pierre, _marta),
            olegEnA);
        cancelada.Cancelar(DateTime.UtcNow, "El Cliente la anula");
        await _dbContext.SaveChangesAsync();

        // Una asignación anterior a FechaAsignacionUtc/GestionadoPorUsuarioId: solo queda su alta en la auditoría.
        await _dbContext.VisitasTrabajadores.Where(vt => vt.Id == olegEnA.Id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(vt => vt.FechaAsignacionUtc, (DateTime?)null)
                .SetProperty(vt => vt.GestionadoPorUsuarioId, (Guid?)null));
        _dbContext.RegistrosAuditoria.Add(new RegistroAuditoria(
            nameof(VisitaTrabajador), olegEnA.Id, "Creado", null, null, _luis, TipoActorAuditoria.Persona,
            actorRealUsuarioId: _luis));
        await _dbContext.SaveChangesAsync();
    }

    private async Task<(Empresa Cliente, Empresa Empresa, Empresa Subcontrata)> SembrarEmpresasAsync(string prefijo)
    {
        var cliente = Empresa.CrearComoCliente($"{prefijo} Cliente", "B12345674", false, null, null);
        var empresa = new Empresa($"{prefijo} Empresa");
        var subcontrata = new Empresa($"{prefijo} Subcontrata");
        _dbContext.Empresas.AddRange(cliente, empresa, subcontrata);
        _dbContext.ParametrosSistema.Add(new ParametroSistema(umbralAmbarDias: 30, umbralRojoDias: 15));
        await _dbContext.SaveChangesAsync();
        return (cliente, empresa, subcontrata);
    }

    private sealed class TenantActualPorAmbito : ITenantActual
    {
        public Guid? TenantId => AmbitoTenantExplicito.TenantIdActual;
    }

    private sealed class TenantActualNulo : ITenantActual
    {
        public Guid? TenantId => null;
    }

    private sealed class DirectorioUsuariosFalso(Dictionary<Guid, string> nombres) : IDirectorioUsuariosService
    {
        public Task<bool> EsVisibleEnTenantActualAsync(Guid usuarioId, CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task<IReadOnlyDictionary<Guid, string>> ObtenerNombresVisiblesAsync(
            IReadOnlyCollection<Guid> usuarioIds, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<Guid, string>>(
                usuarioIds.Where(nombres.ContainsKey).ToDictionary(id => id, id => nombres[id]));

        public Task<Guid?> ObtenerTenantDeUsuarioAsync(Guid usuarioId, CancellationToken cancellationToken = default) =>
            Task.FromResult<Guid?>(null);

        public Task<bool> EsCuentaActivaConRolAsync(Guid usuarioId, Guid tenantId, string rol, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task<IReadOnlyList<Guid>> ObtenerCuentasActivasConRolAsync(Guid tenantId, string rol, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Guid>>([]);
    }
}
