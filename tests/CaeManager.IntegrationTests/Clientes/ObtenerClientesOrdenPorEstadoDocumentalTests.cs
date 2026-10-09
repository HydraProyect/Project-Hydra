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
    /// Dentro de un filtro, el orden por estado contradice a la razón social: «Con pendientes» deja
    /// pasar a los dos, pero el que además tiene un Vencido («Z…») va antes que el que solo tiene
    /// Faltantes («A…»). Sin el orden por estado saldrían por nombre, al revés. Vencido pesa más que
    /// Faltante desde el 2026-10-09 (orden fijado el 2026-10-03: Bloqueante, Vencido, Faltante).
    /// </summary>
    [Fact]
    public async Task Con_filtro_de_faltantes_el_que_ademas_tiene_un_vencido_va_primero_aunque_su_nombre_vaya_detras()
    {
        var soloFaltante = Empresa.CrearComoCliente("Aaa Solo Faltante S.L.", "", false, null, null);
        var faltanteYVencido = Empresa.CrearComoCliente("Zzz Faltante y Vencido S.L.", "", false, null, null);
        _dbContext.Empresas.AddRange(soloFaltante, faltanteYVencido);
        await _dbContext.SaveChangesAsync();
        _alertas.Add(Alerta(soloFaltante.Id, EstadoDocumento.Faltante));
        _alertas.Add(Alerta(faltanteYVencido.Id, EstadoDocumento.Vencido));
        _alertas.Add(Alerta(faltanteYVencido.Id, EstadoDocumento.Faltante));

        var resultado = await _servicios.GetRequiredService<IMediator>().Send(new ObtenerClientesQuery(
            Busqueda: null, SoloCriticos: null, EstadoDocumental: EstadoDocumento.Faltante,
            OrdenarPor: nameof(ClienteListaDto.EstadoDocumentalPeor)));

        resultado.TotalElementos.Should().Be(2);
        resultado.Elementos.Select(c => c.Id).Should().Equal([faltanteYVencido.Id, soloFaltante.Id],
            "Vencido pesa más que Faltante, aunque por razón social «Z…» vaya detrás de «A…»");
        resultado.Elementos[0].EstadoDocumentalPeor.Should().Be(EstadoDocumento.Vencido);
    }

    /// <summary>
    /// Los cinco estados, sembrados con nombres en orden alfabético inverso a su prioridad:
    /// la lista sale Vencido → Faltante → Urgente → Próximo → Al corriente.
    /// </summary>
    [Fact]
    public async Task El_orden_por_estado_es_Vencido_Faltante_Urgente_Proximo_y_Al_corriente()
    {
        var alCorriente = Empresa.CrearComoCliente("A Al Corriente S.L.", "", false, null, null);
        var proximo = Empresa.CrearComoCliente("B Próximo S.L.", "", false, null, null);
        var urgente = Empresa.CrearComoCliente("C Urgente S.L.", "", false, null, null);
        var faltante = Empresa.CrearComoCliente("D Faltante S.L.", "", false, null, null);
        var vencido = Empresa.CrearComoCliente("E Vencido S.L.", "", false, null, null);
        _dbContext.Empresas.AddRange(alCorriente, proximo, urgente, vencido, faltante);
        await _dbContext.SaveChangesAsync();
        _alertas.Add(Alerta(proximo.Id, EstadoDocumento.Proximo));
        _alertas.Add(Alerta(urgente.Id, EstadoDocumento.Urgente));
        _alertas.Add(Alerta(vencido.Id, EstadoDocumento.Vencido));
        _alertas.Add(Alerta(faltante.Id, EstadoDocumento.Faltante));

        var resultado = await EjecutarAsync(pagina: 1, tamano: 20);

        resultado.Elementos.Select(c => c.Id).Should().Equal(vencido.Id, faltante.Id, urgente.Id, proximo.Id, alCorriente.Id);
    }

    /// <summary>
    /// Los que tienen alertas se ordenan en memoria y los «Al corriente» se paginan en SQL: una
    /// página que cae a caballo entre los dos tramos tiene que coserlos sin repetir ni saltar
    /// filas, en los dos sentidos.
    /// </summary>
    [Fact]
    public async Task Una_pagina_a_caballo_entre_los_que_tienen_alertas_y_los_al_corriente_no_repite_ni_salta_filas()
    {
        var (alCorrienteA, alCorrienteB, urgente, vencido) = await SembrarCuatroAsync();

        var ascendente = new List<Guid>();
        var descendente = new List<Guid>();
        for (var pagina = 1; pagina <= 2; pagina++)
        {
            ascendente.AddRange((await EjecutarAsync(pagina, tamano: 3)).Elementos.Select(c => c.Id));
            descendente.AddRange((await EjecutarAsync(pagina, tamano: 3, descendente: true)).Elementos.Select(c => c.Id));
        }

        ascendente.Should().Equal(vencido, urgente, alCorrienteA, alCorrienteB);
        descendente.Should().Equal(alCorrienteA, alCorrienteB, urgente, vencido);
        (await EjecutarAsync(pagina: 3, tamano: 3)).Elementos.Should().BeEmpty("no hay tercera página");
    }

    /// <summary>
    /// El orden por estado es el de por defecto de la lista, así que no puede tener tope: con más
    /// de 2000 Clientes empresariales el total es el real, y uno con un Faltante cuyo nombre va
    /// el último por razón social (el candidato 2002) sale el primero. Antes se ordenaban solo los
    /// 2000 primeros por nombre: este no aparecía en ninguna página y el total decía 2000.
    /// </summary>
    [Fact]
    public async Task Con_mas_de_2000_Clientes_el_orden_por_estado_cubre_la_cartera_entera_y_el_total_es_el_real()
    {
        for (var i = 1; i <= 2001; i++)
            _dbContext.Empresas.Add(Empresa.CrearComoCliente($"Cliente {i:0000} S.L.", "", false, null, null));
        var ultimoPorNombre = Empresa.CrearComoCliente("Zzz Faltante S.L.", "", false, null, null);
        _dbContext.Empresas.Add(ultimoPorNombre);
        await _dbContext.SaveChangesAsync();
        _alertas.Add(Alerta(ultimoPorNombre.Id, EstadoDocumento.Faltante));

        var primera = await EjecutarAsync(pagina: 1, tamano: 20);
        var ultima = await EjecutarAsync(pagina: 101, tamano: 20);

        primera.TotalElementos.Should().Be(2002);
        primera.Elementos[0].Id.Should().Be(ultimoPorNombre.Id);
        primera.Elementos.Skip(1).Select(c => c.RazonSocial).Should().StartWith(["Cliente 0001 S.L.", "Cliente 0002 S.L."]);
        ultima.Elementos.Select(c => c.RazonSocial).Should().Equal(["Cliente 2000 S.L.", "Cliente 2001 S.L."],
            "la página 101 de 20 son las filas 2001 y 2002: las dos últimas por nombre");
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
