using CaeManager.Application.Alertas.Queries.ObtenerAlertas;
using CaeManager.Application.Clientes.Queries.ObtenerClientes;
using CaeManager.Application.Common;
using CaeManager.Application.DependencyInjection;
using CaeManager.Domain.Configuracion;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
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
/// Rediseño de listados, fase 1: la lista de Clientes empresariales sale con
/// el peor estado documental primero (<c>OrdenarPor = EstadoDocumentalPeor</c>).
/// El estado no es una columna: es el agregado de las alertas de vigencia de
/// los Trabajadores cuyo Cliente principal es cada fila, así que el orden se
/// aplica en memoria ANTES de paginar. Lo que se fija aquí es justo eso: que
/// la página 1 trae los peores aunque por razón social fueran los últimos, y
/// que el desempate dentro de un mismo estado es la razón social.
///
/// <para>
/// Las alertas se inyectan sustituyendo el manejador de
/// <see cref="ObtenerAlertasQuery"/> (cuyo cálculo tiene sus propios tests):
/// sembrar Documentos, Asignaciones y Clientes principales para obtener tres
/// estados distintos probaría otra cosa. El orden base y la paginación sí
/// corren contra PostgreSQL de verdad.
/// </para>
/// </summary>
public class ObtenerClientesOrdenPorEstadoDocumentalTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly List<AlertaDto> _alertas = [];
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
        // Registrado después de AddApplication: MediatR resuelve el último.
        servicios.AddTransient<IRequestHandler<ObtenerAlertasQuery, IReadOnlyList<AlertaDto>>>(_ => new AlertasFijas(_alertas));
        servicios.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        servicios.AddSingleton<ITenantActual>(tenantActual);
        servicios.AddSingleton<IUnitOfWork>(_dbContext);
        servicios.AddSingleton<CaeManager.Application.Tenants.ITenantsQueryContext>(_dbContext);
        servicios.AddSingleton<CaeManager.Application.Empresas.IEmpresasQueryContext>(_dbContext);
        servicios.AddSingleton<CaeManager.Application.Centros.ICentrosQueryContext>(_dbContext);
        servicios.AddSingleton<CaeManager.Application.Contactos.IContactosAgendaQueryContext>(_dbContext);
        servicios.AddSingleton<CaeManager.Application.Configuracion.IConfiguracionQueryContext>(_dbContext);
        servicios.AddSingleton<IAlcanceDatosService>(new AlcanceDatosServiceFalso());
        servicios.AddSingleton<ICurrentUserService>(new CurrentUserServiceFalso(Guid.NewGuid(), tenantOrigenId: _tenant));
        _servicios = servicios.BuildServiceProvider();
    }

    public async Task DisposeAsync()
    {
        _servicios.Dispose();
        await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);
        await _dbContext.DisposeAsync();
    }

    [Fact]
    public async Task La_primera_pagina_trae_el_peor_estado_aunque_por_razon_social_fuera_el_ultimo()
    {
        var (alCorrienteA, alCorrienteB, urgente, vencido) = await SembrarCuatroAsync();

        var pagina1 = await EjecutarAsync(pagina: 1, tamano: 2);
        var pagina2 = await EjecutarAsync(pagina: 1 + 1, tamano: 2);

        pagina1.TotalElementos.Should().Be(4);
        pagina1.Elementos.Select(c => c.Id).Should().Equal(vencido, urgente);
        pagina2.Elementos.Select(c => c.Id).Should().Equal([alCorrienteA, alCorrienteB],
            "dentro del mismo estado («Al corriente») desempata la razón social");
    }

    [Fact]
    public async Task Descendente_invierte_la_prioridad_y_deja_el_peor_al_final()
    {
        var (alCorrienteA, alCorrienteB, urgente, vencido) = await SembrarCuatroAsync();

        var resultado = await EjecutarAsync(pagina: 1, tamano: 20, descendente: true);

        resultado.Elementos.Select(c => c.Id).Should().Equal(alCorrienteA, alCorrienteB, urgente, vencido);
    }

    [Fact]
    public async Task Con_filtro_de_estado_el_orden_por_estado_se_aplica_antes_de_filtrar()
    {
        var (_, _, urgente, vencido) = await SembrarCuatroAsync();
        // Un Vencido más para el urgente: entra en «Con vencidos» junto al otro y su peor
        // estado pasa a Vencido, así que los dos empatan y decide la razón social.
        _alertas.Add(Alerta(urgente, EstadoDocumento.Vencido));

        var resultado = await _servicios.GetRequiredService<IMediator>().Send(new ObtenerClientesQuery(
            Busqueda: null, SoloCriticos: null, EstadoDocumental: EstadoDocumento.Vencido,
            OrdenarPor: nameof(ClienteListaDto.EstadoDocumentalPeor)));

        resultado.TotalElementos.Should().Be(2);
        resultado.Elementos.Select(c => c.Id).Should().Equal([urgente, vencido],
            "los dos tienen peor estado Vencido; desempata la razón social («B…» antes que «Z…»)");
    }

    /// <summary>
    /// Razón social al revés que el estado: por nombre, el vencido iría el último. Devuelve los
    /// Ids en el orden en que la razón social los pondría.
    /// </summary>
    private async Task<(Guid AlCorrienteA, Guid AlCorrienteB, Guid Urgente, Guid Vencido)> SembrarCuatroAsync()
    {
        var alCorrienteA = Empresa.CrearComoCliente("Alfa Al Corriente S.L.", "B12345674", false, null, null);
        var alCorrienteB = Empresa.CrearComoCliente("Beta Al Corriente S.L.", "B87654323", false, null, null);
        var urgente = Empresa.CrearComoCliente("Beta Urgente S.L.", "B11111119", false, null, null);
        var vencido = Empresa.CrearComoCliente("Zeta Vencido S.L.", "B22222228", false, null, null);
        _dbContext.Empresas.AddRange(alCorrienteA, alCorrienteB, urgente, vencido);
        await _dbContext.SaveChangesAsync();

        _alertas.Add(Alerta(urgente.Id, EstadoDocumento.Urgente));
        _alertas.Add(Alerta(vencido.Id, EstadoDocumento.Vencido));
        _alertas.Add(Alerta(vencido.Id, EstadoDocumento.Urgente));

        return (alCorrienteA.Id, alCorrienteB.Id, urgente.Id, vencido.Id);
    }

    private static AlertaDto Alerta(Guid clienteId, EstadoDocumento estado) =>
        new(Guid.NewGuid(), Guid.NewGuid(), "Trabajador", Guid.NewGuid(), "Tipo", null, estado, null, null, ClienteId: clienteId);

    private Task<ResultadoPaginado<ClienteListaDto>> EjecutarAsync(int pagina, int tamano, bool descendente = false) =>
        _servicios.GetRequiredService<IMediator>().Send(new ObtenerClientesQuery(
            Busqueda: null, SoloCriticos: null, Pagina: pagina, TamanoPagina: tamano,
            OrdenarPor: nameof(ClienteListaDto.EstadoDocumentalPeor), Descendente: descendente));

    private sealed class AlertasFijas(IReadOnlyList<AlertaDto> alertas) : IRequestHandler<ObtenerAlertasQuery, IReadOnlyList<AlertaDto>>
    {
        public Task<IReadOnlyList<AlertaDto>> Handle(ObtenerAlertasQuery request, CancellationToken cancellationToken) =>
            Task.FromResult(alertas);
    }
}
