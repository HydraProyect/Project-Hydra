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
/// El filtro por Estado documental del listado de Clientes empresariales se aplica en SQL, sin tope de
/// candidatos: antes se traían los 2.000 primeros por el orden pedido y se filtraba en memoria, así que un
/// Cliente empresarial que coincidía pero iba detrás en ese orden no salía en ninguna página y el total
/// contaba solo los coincidentes de esos 2.000. El agregado por Cliente sigue saliendo de las alertas
/// (<see cref="ObtenerAlertasQuery"/>, sustituido aquí como en <see cref="ObtenerClientesOrdenPorEstadoDocumentalTests"/>);
/// lo que corre contra PostgreSQL es el filtro, el recuento, el orden y la paginación.
///
/// <para>
/// El último test compara, con una siembra pequeña, el resultado nuevo con el del algoritmo anterior
/// (filtrar en memoria todo lo que pasa los demás filtros, ordenar de forma estable por estado y paginar):
/// mientras la cartera quepa en el tope antiguo, los dos tienen que dar las mismas filas, en el mismo orden
/// y con el mismo total, página a página.
/// </para>
/// </summary>
public class ObtenerClientesFiltroEstadoPaginacionEnSqlTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly List<AlertaDto> _alertas = [];
    private TenantActualAmbiental _tenantActual = null!;
    private CaeManagerDbContext _dbContext = null!;
    private readonly List<ServiceProvider> _proveedores = [];

    public async Task InitializeAsync()
    {
        _tenantActual = new TenantActualAmbiental { TenantId = _tenant };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(_tenantActual))
            .Options;

        _dbContext = new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), _tenantActual);
        await _dbContext.Database.MigrateAsync();
        _dbContext.ParametrosSistema.Add(new ParametroSistema(umbralAmbarDias: 30, umbralRojoDias: 15));
        await _dbContext.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        foreach (var proveedor in _proveedores)
            await proveedor.DisposeAsync();
        await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);
        await _dbContext.DisposeAsync();
    }

    [Fact]
    public async Task Con_mas_de_2000_Clientes_el_filtro_de_estado_encuentra_al_que_va_el_ultimo_y_el_total_es_el_real()
    {
        for (var i = 1; i <= 2001; i++)
            _dbContext.Empresas.Add(Empresa.CrearComoCliente($"Cliente {i:0000} S.L.", "", false, null, null));
        var primeroConVencido = Empresa.CrearComoCliente("Cliente 0001 Vencido S.L.", "", false, null, null);
        var ultimoConVencido = Empresa.CrearComoCliente("Zzz Vencido S.L.", "", false, null, null);
        _dbContext.Empresas.AddRange(primeroConVencido, ultimoConVencido);
        await _dbContext.SaveChangesAsync();
        _alertas.Add(Alerta(primeroConVencido.Id, EstadoDocumento.Vencido));
        _alertas.Add(Alerta(ultimoConVencido.Id, EstadoDocumento.Vencido));

        var resultado = await EjecutarAsync(new ObtenerClientesQuery(
            Busqueda: null, SoloCriticos: null, EstadosDocumentales: [EstadoDocumento.Vencido], TamanoPagina: 20));

        resultado.TotalElementos.Should().Be(2,
            "el filtro recorre la cartera entera; con el tope de 2.000 candidatos «Zzz…» quedaba fuera");
        resultado.Elementos.Select(c => c.Id).Should().Equal(primeroConVencido.Id, ultimoConVencido.Id);
    }

    [Fact]
    public async Task Al_corriente_con_mas_de_2000_Clientes_pagina_en_SQL_sin_repetir_ni_saltar_filas()
    {
        for (var i = 1; i <= 2003; i++)
            _dbContext.Empresas.Add(Empresa.CrearComoCliente($"Cliente {i:0000} S.L.", "", false, null, null));
        var conAlerta = Empresa.CrearComoCliente("Cliente 0002 Urgente S.L.", "", false, null, null);
        _dbContext.Empresas.Add(conAlerta);
        await _dbContext.SaveChangesAsync();
        _alertas.Add(Alerta(conAlerta.Id, EstadoDocumento.Urgente));

        var consulta = new ObtenerClientesQuery(
            Busqueda: null, SoloCriticos: null, EstadosDocumentales: [EstadoDocumento.Vigente], TamanoPagina: 1000);
        var vistas = new List<string>();
        for (var pagina = 1; pagina <= 3; pagina++)
        {
            var resultado = await EjecutarAsync(consulta with { Pagina = pagina });
            resultado.TotalElementos.Should().Be(2003, "Al corriente es todo el que no tiene ninguna alerta abierta");
            vistas.AddRange(resultado.Elementos.Select(c => c.RazonSocial));
        }

        vistas.Should().HaveCount(2003).And.OnlyHaveUniqueItems().And.NotContain(conAlerta.RazonSocial);
        vistas.Should().BeInAscendingOrder(StringComparer.Ordinal);
        vistas[^1].Should().Be("Cliente 2003 S.L.");
    }

    [Fact]
    public async Task El_filtro_de_estado_no_sale_del_alcance_de_cartera()
    {
        var visible = Empresa.CrearComoCliente("Alfa Vencido S.L.", "", false, null, null);
        var fueraDeCartera = Empresa.CrearComoCliente("Beta Vencido S.L.", "", false, null, null);
        var visibleAlCorriente = Empresa.CrearComoCliente("Gamma Al Corriente S.L.", "", false, null, null);
        // Sin alertas y fuera de cartera: la rama «Al corriente» del filtro es «no tiene alertas», y solo el alcance lo deja fuera.
        var fueraDeCarteraAlCorriente = Empresa.CrearComoCliente("Delta Al Corriente S.L.", "", false, null, null);
        _dbContext.Empresas.AddRange(visible, fueraDeCartera, visibleAlCorriente, fueraDeCarteraAlCorriente);
        await _dbContext.SaveChangesAsync();
        _alertas.Add(Alerta(visible.Id, EstadoDocumento.Vencido));
        _alertas.Add(Alerta(fueraDeCartera.Id, EstadoDocumento.Vencido));

        var alcance = new AlcanceDatosServiceFalso(clienteIds: [visible.Id, visibleAlCorriente.Id]);
        var conVencidos = await EjecutarAsync(new ObtenerClientesQuery(
            Busqueda: null, SoloCriticos: null, EstadosDocumentales: [EstadoDocumento.Vencido], ConRecuentosPorEstado: true), alcance);
        var alCorriente = await EjecutarAsync(new ObtenerClientesQuery(
            Busqueda: null, SoloCriticos: null, EstadosDocumentales: [EstadoDocumento.Vigente],
            OrdenarPor: nameof(ClienteListaDto.EstadoDocumentalPeor)), alcance);

        conVencidos.Elementos.Select(c => c.Id).Should().Equal(visible.Id);
        conVencidos.TotalElementos.Should().Be(1);
        conVencidos.RecuentosPorEstado![nameof(EstadoDocumento.Vencido)].Should().Be(1,
            "la cifra de la franja y el filtro cuentan lo mismo, dentro de la cartera");
        alCorriente.Elementos.Select(c => c.Id).Should().Equal(visibleAlCorriente.Id);
        alCorriente.TotalElementos.Should().Be(1, "«Delta…» no tiene alertas, pero está fuera de la cartera");
    }

    /// <summary>
    /// Resultado idéntico al del algoritmo anterior con una siembra que cabe en su tope: todas las
    /// combinaciones de filtro (uno, varios, «Al corriente» solo y combinado), los dos órdenes que tocan el
    /// estado o no (razón social y peor estado, en los dos sentidos) y varios tamaños de página, también
    /// páginas a caballo entre el tramo con alertas y el «Al corriente».
    /// </summary>
    [Fact]
    public async Task Da_las_mismas_filas_en_el_mismo_orden_que_el_filtro_en_memoria_anterior()
    {
        var sembrados = await SembrarCarteraVariadaAsync();
        EstadoDocumento[][] filtros =
        [
            [EstadoDocumento.Vencido],
            [EstadoDocumento.Faltante],
            [EstadoDocumento.Urgente, EstadoDocumento.Proximo],
            [EstadoDocumento.Vigente],
            [EstadoDocumento.Vigente, EstadoDocumento.Vencido],
            [EstadoDocumento.Proximo, EstadoDocumento.Faltante, EstadoDocumento.Vigente]
        ];
        (string? OrdenarPor, bool Descendente)[] ordenes =
        [
            (null, false),
            (nameof(ClienteListaDto.RazonSocial), true),
            (nameof(ClienteListaDto.EstadoDocumentalPeor), false),
            (nameof(ClienteListaDto.EstadoDocumentalPeor), true)
        ];

        foreach (var filtro in filtros)
            foreach (var (ordenarPor, descendente) in ordenes)
                foreach (var tamano in new[] { 2, 3, 50 })
                {
                    var esperado = ReferenciaEnMemoria(sembrados, filtro, ordenarPor, descendente);
                    var obtenido = new List<Guid>();
                    var paginas = Math.Max(1, (int)Math.Ceiling(esperado.Count / (double)tamano));
                    for (var pagina = 1; pagina <= paginas + 1; pagina++)
                    {
                        var resultado = await EjecutarAsync(new ObtenerClientesQuery(
                            Busqueda: null, SoloCriticos: null, EstadosDocumentales: filtro,
                            OrdenarPor: ordenarPor, Descendente: descendente, Pagina: pagina, TamanoPagina: tamano));
                        resultado.TotalElementos.Should().Be(esperado.Count,
                            $"filtro {string.Join(",", filtro)}, orden {ordenarPor ?? "por defecto"} {(descendente ? "desc" : "asc")}, página {pagina} de {tamano}");
                        obtenido.AddRange(resultado.Elementos.Select(c => c.Id));
                    }

                    obtenido.Should().Equal(esperado,
                        $"filtro {string.Join(",", filtro)}, orden {ordenarPor ?? "por defecto"} {(descendente ? "desc" : "asc")}, tamaño {tamano}");
                }
    }

    /// <summary>
    /// Copia del camino anterior (rama con filtro de estado de <c>ObtenerClientesQueryHandler</c> antes de este
    /// cambio), sobre la cartera sembrada: orden de SQL por la columna pedida y razón social, después orden
    /// estable por prioridad si se ordena por estado, y filtro «hay alguna alerta en un estado pedido»
    /// (Vigente = sin ninguna). Los nombres sembrados no empatan, así que el desempate por Id no interviene.
    /// </summary>
    private static List<Guid> ReferenciaEnMemoria(
        IReadOnlyList<(Guid Id, string RazonSocial, EstadoDocumento[] Alertas)> sembrados,
        EstadoDocumento[] filtro, string? ordenarPor, bool descendente)
    {
        static int Prioridad(EstadoDocumento[] alertas) => alertas.Length == 0
            ? 4
            : alertas.Min(estado => estado switch
            {
                EstadoDocumento.Vencido => 0,
                EstadoDocumento.Faltante => 1,
                EstadoDocumento.Urgente => 2,
                EstadoDocumento.Proximo => 3,
                _ => 4
            });

        var porNombre = ordenarPor == nameof(ClienteListaDto.RazonSocial) && descendente
            ? sembrados.OrderByDescending(s => s.RazonSocial, StringComparer.Ordinal).ToList()
            : sembrados.OrderBy(s => s.RazonSocial, StringComparer.Ordinal).ToList();
        if (ordenarPor == nameof(ClienteListaDto.EstadoDocumentalPeor))
            porNombre = descendente
                ? porNombre.OrderByDescending(s => Prioridad(s.Alertas)).ToList()
                : porNombre.OrderBy(s => Prioridad(s.Alertas)).ToList();

        var pideSinAlertas = filtro.Contains(EstadoDocumento.Vigente);
        return porNombre
            .Where(s => s.Alertas.Length > 0 ? s.Alertas.Intersect(filtro).Any() : pideSinAlertas)
            .Select(s => s.Id)
            .ToList();
    }

    /// <summary>
    /// Doce Clientes empresariales con nombres en mayúscula inicial y sin acentos (el orden de PostgreSQL y el
    /// ordinal coinciden), y alertas repartidas para que haya peores estados distintos, varios estados presentes
    /// a la vez y Clientes «Al corriente» intercalados por nombre entre los que tienen alertas.
    /// </summary>
    private async Task<IReadOnlyList<(Guid Id, string RazonSocial, EstadoDocumento[] Alertas)>> SembrarCarteraVariadaAsync()
    {
        (string Nombre, EstadoDocumento[] Alertas)[] cartera =
        [
            ("Aaa Corriente S.L.", []),
            ("Bbb Vencido S.L.", [EstadoDocumento.Vencido]),
            ("Ccc Proximo S.L.", [EstadoDocumento.Proximo]),
            ("Ddd Corriente S.L.", []),
            ("Eee Faltante Y Urgente S.L.", [EstadoDocumento.Faltante, EstadoDocumento.Urgente]),
            ("Fff Urgente S.L.", [EstadoDocumento.Urgente]),
            ("Ggg Vencido Y Proximo S.L.", [EstadoDocumento.Vencido, EstadoDocumento.Proximo]),
            ("Hhh Corriente S.L.", []),
            ("Iii Faltante S.L.", [EstadoDocumento.Faltante]),
            ("Jjj Vencido S.L.", [EstadoDocumento.Vencido, EstadoDocumento.Vencido]),
            ("Kkk Proximo S.L.", [EstadoDocumento.Proximo]),
            ("Lll Corriente S.L.", [])
        ];

        var sembrados = new List<(Guid, string, EstadoDocumento[])>();
        foreach (var (nombre, alertas) in cartera)
        {
            var cliente = Empresa.CrearComoCliente(nombre, "", false, null, null);
            _dbContext.Empresas.Add(cliente);
            foreach (var estado in alertas)
                _alertas.Add(Alerta(cliente.Id, estado));
            sembrados.Add((cliente.Id, nombre, alertas));
        }
        await _dbContext.SaveChangesAsync();
        return sembrados;
    }

    private static AlertaDto Alerta(Guid clienteId, EstadoDocumento estado) =>
        new(Guid.NewGuid(), Guid.NewGuid(), "Trabajador", Guid.NewGuid(), "Tipo", null, estado, null, null, ClienteId: clienteId);

    private ServiceProvider? _sinRestriccion;

    /// <summary>Un proveedor por alcance: las alertas se comparten por referencia, así que reutilizarlo no congela nada.</summary>
    private Task<ResultadoPaginado<ClienteListaDto>> EjecutarAsync(ObtenerClientesQuery consulta, IAlcanceDatosService? alcance = null) =>
        (alcance is null ? _sinRestriccion ??= Servicios(new AlcanceDatosServiceFalso()) : Servicios(alcance))
            .GetRequiredService<IMediator>().Send(consulta);

    private ServiceProvider Servicios(IAlcanceDatosService alcance)
    {
        var servicios = new ServiceCollection();
        servicios.AddApplication();
        // Registrado después de AddApplication: MediatR resuelve el último.
        servicios.AddTransient<IRequestHandler<ObtenerAlertasQuery, IReadOnlyList<AlertaDto>>>(_ => new AlertasFijas(_alertas));
        servicios.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        servicios.AddSingleton<ITenantActual>(_tenantActual);
        servicios.AddSingleton<IUnitOfWork>(_dbContext);
        servicios.AddSingleton<CaeManager.Application.Tenants.ITenantsQueryContext>(_dbContext);
        servicios.AddSingleton<CaeManager.Application.Empresas.IEmpresasQueryContext>(_dbContext);
        servicios.AddSingleton<CaeManager.Application.Proyectos.IProyectosQueryContext>(_dbContext);
        servicios.AddSingleton<CaeManager.Application.Proyectos.IRequisitosEventoQueryContext>(_dbContext);
        servicios.AddSingleton<CaeManager.Application.Centros.ICentrosQueryContext>(_dbContext);
        servicios.AddSingleton<CaeManager.Application.Contactos.IContactosAgendaQueryContext>(_dbContext);
        servicios.AddSingleton<CaeManager.Application.Configuracion.IConfiguracionQueryContext>(_dbContext);
        servicios.AddSingleton<CaeManager.Application.Documentos.IDocumentosQueryContext>(_dbContext);
        servicios.AddSingleton<CaeManager.Application.TiposDocumento.ITiposDocumentoQueryContext>(_dbContext);
        servicios.AddSingleton<CaeManager.Application.Trabajadores.ITrabajadoresQueryContext>(_dbContext);
        servicios.AddSingleton<CaeManager.Application.Asignaciones.IAsignacionesQueryContext>(_dbContext);
        servicios.AddSingleton(alcance);
        servicios.AddSingleton<ICurrentUserService>(new CurrentUserServiceFalso(Guid.NewGuid(), tenantOrigenId: _tenant));
        var proveedor = servicios.BuildServiceProvider();
        _proveedores.Add(proveedor);
        return proveedor;
    }

    private sealed class AlertasFijas(IReadOnlyList<AlertaDto> alertas) : IRequestHandler<ObtenerAlertasQuery, IReadOnlyList<AlertaDto>>
    {
        public Task<IReadOnlyList<AlertaDto>> Handle(ObtenerAlertasQuery request, CancellationToken cancellationToken) =>
            Task.FromResult(alertas);
    }
}
