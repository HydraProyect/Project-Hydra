using CaeManager.Application.Clientes.Queries.ObtenerClientes;
using CaeManager.Application.Common;
using CaeManager.Application.DependencyInjection;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Configuracion;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Subcontratas;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.IntegrationTests;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CaeManager.IntegrationTests.Clientes;

/// <summary>
/// F4-P0 (2026-08-27): <c>ObtenerClientesQuery</c> leía la tabla legacy
/// <c>Clientes</c>, sin escrituras desde F3b-Cliente (PR #279) — cualquier
/// Cliente dado de alta después de esa congelación era invisible en su
/// propio listado. Frontera de esta corrección: solo el reader pasa a leer
/// <c>Empresas</c> (<see cref="Empresa.CrearComoCliente"/>, discriminada por
/// <c>EsCritico != null</c> — mismo patrón que <c>NivelServicio != null</c>
/// distingue Subcontrata en <c>ObtenerSubcontratasQuery</c>). Estos tests
/// cubren la paridad de comportamiento (no solo que compile) contra la
/// implementación anterior: alcance de datos, discriminador de tipo y
/// enriquecido de Centros/Alertas, todo ya en espacio de <c>Empresa.Id</c>
/// desde el repunteo de FKs de F3b — sin cambio en esos lectores.
/// </summary>
public class ObtenerClientesQueryLeeEmpresasTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenant = Guid.NewGuid();
    private CaeManagerDbContext _dbContext = null!;
    private ServiceProvider _servicios = null!;

    public async Task InitializeAsync()
    {
        var tenantActual = new TenantActualAmbiental { TenantId = _tenant };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual))
            .Options;

        _dbContext = new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
        await _dbContext.Database.MigrateAsync();
        _dbContext.ParametrosSistema.Add(new ParametroSistema(umbralAmbarDias: 30, umbralRojoDias: 15));
        await _dbContext.SaveChangesAsync();

        var servicios = new ServiceCollection();
        servicios.AddApplication();
        servicios.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        servicios.AddSingleton<ITenantActual>(tenantActual);
        servicios.AddSingleton<IUnitOfWork>(_dbContext);
        servicios.AddSingleton<CaeManager.Application.Tenants.ITenantsQueryContext>(_dbContext);
        servicios.AddSingleton<CaeManager.Application.Empresas.IEmpresasQueryContext>(_dbContext);
        servicios.AddSingleton<CaeManager.Application.Centros.ICentrosQueryContext>(_dbContext);
        servicios.AddSingleton<CaeManager.Application.Contactos.IContactosAgendaQueryContext>(_dbContext);
        servicios.AddSingleton<CaeManager.Application.Trabajadores.ITrabajadoresQueryContext>(_dbContext);
        servicios.AddSingleton<CaeManager.Application.TiposDocumento.ITiposDocumentoQueryContext>(_dbContext);
        servicios.AddSingleton<CaeManager.Application.Documentos.IDocumentosQueryContext>(_dbContext);
        servicios.AddSingleton<CaeManager.Application.Asignaciones.IAsignacionesQueryContext>(_dbContext);
        servicios.AddSingleton<CaeManager.Application.Configuracion.IConfiguracionQueryContext>(_dbContext);
        _alcance = new AlcanceDatosServiceFalso();
        servicios.AddSingleton<IAlcanceDatosService>(_ => _alcance);
        servicios.AddSingleton<ICurrentUserService>(new CurrentUserServiceFalso(Guid.NewGuid(), tenantOrigenId: _tenant));
        _servicios = servicios.BuildServiceProvider();
    }

    private AlcanceDatosServiceFalso _alcance = new();

    public async Task DisposeAsync()
    {
        _servicios.Dispose();
        await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);
        await _dbContext.DisposeAsync();
    }

    [Fact]
    public async Task Un_Cliente_creado_tras_el_freeze_de_F3b_aparece_en_su_propio_listado_con_sus_datos()
    {
        var ejecutivoId = Guid.NewGuid();
        var cliente = Empresa.CrearComoCliente("Cliente Post-Freeze S.L.", "B12345674", esCritico: true, notas: "Nota de prueba", ejecutivoUsuarioId: ejecutivoId);
        _dbContext.Empresas.Add(cliente);
        await _dbContext.SaveChangesAsync();

        var resultado = await EjecutarAsync();

        var fila = resultado.Elementos.Should().ContainSingle().Subject;
        fila.Id.Should().Be(cliente.Id);
        fila.RazonSocial.Should().Be("Cliente Post-Freeze S.L.");
        fila.Cif.Should().Be("B12345674");
        fila.EsCritico.Should().BeTrue();
        fila.EjecutivoUsuarioId.Should().Be(ejecutivoId);
    }

    [Fact]
    public async Task El_listado_no_incluye_Empresas_propias_ni_Subcontratas()
    {
        var propia = new Empresa("Empresa Propia S.L.", "B87654323");
        var subcontrata = Empresa.CrearComoSubcontrata("Subcontrata S.L.", null, NivelServicioSubcontrata.Gestionada.ToString());
        var cliente = Empresa.CrearComoCliente("El Único Cliente S.L.", "B12345674", false, null, null);
        _dbContext.Empresas.AddRange(propia, subcontrata, cliente);
        await _dbContext.SaveChangesAsync();

        var resultado = await EjecutarAsync();

        resultado.Elementos.Should().ContainSingle().Which.Id.Should().Be(cliente.Id);
    }

    [Fact]
    public async Task El_alcance_de_datos_sigue_restringiendo_el_listado_a_la_cartera_visible()
    {
        var visible = Empresa.CrearComoCliente("Cliente En Cartera S.L.", "B12345674", false, null, null);
        var fueraDeCartera = Empresa.CrearComoCliente("Cliente Fuera De Cartera S.L.", "B87654323", false, null, null);
        _dbContext.Empresas.AddRange(visible, fueraDeCartera);
        await _dbContext.SaveChangesAsync();

        _alcance = new AlcanceDatosServiceFalso(clienteIds: [visible.Id]);

        var resultado = await EjecutarAsync();

        resultado.Elementos.Should().ContainSingle().Which.Id.Should().Be(visible.Id);
    }

    [Fact]
    public async Task Los_Centros_del_Cliente_se_siguen_contando_via_Empresa_Id()
    {
        var cliente = Empresa.CrearComoCliente("Cliente Con Centros S.L.", "B12345674", false, null, null);
        var empresa = new Empresa("Empresa Ejecutora S.L.", "B87654323");
        _dbContext.Empresas.AddRange(cliente, empresa);
        await _dbContext.SaveChangesAsync();

        _dbContext.Centros.Add(new Centro(cliente.Id, empresa.Id, "Centro Uno"));
        _dbContext.Centros.Add(new Centro(cliente.Id, empresa.Id, "Centro Dos"));
        await _dbContext.SaveChangesAsync();

        var resultado = await EjecutarAsync();

        resultado.Elementos.Should().ContainSingle().Which.Centros.Should().Be(2);
    }

    private async Task<ResultadoPaginado<ClienteListaDto>> EjecutarAsync()
    {
        var mediator = _servicios.GetRequiredService<IMediator>();
        return await mediator.Send(new ObtenerClientesQuery(Busqueda: null, SoloCriticos: null));
    }
}
