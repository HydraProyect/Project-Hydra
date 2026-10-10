using CaeManager.Application.Auditoria;
using CaeManager.Application.Clientes.Queries.ObtenerClientesParaSelector;
using CaeManager.Application.Common;
using CaeManager.Application.Documentos;
using CaeManager.Application.Gestiones.Queries.ObtenerGestiones;
using CaeManager.Application.Proyectos.Queries.ObtenerProyectos;
using CaeManager.Application.Tenants.Queries.ObtenerClientesAutorizados;
using CaeManager.Application.Vehiculos.Queries.ObtenerVehiculos;
using CaeManager.Application.Visitas.Queries.ObtenerVisitas;
using CaeManager.Domain.Auditoria;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Common;
using CaeManager.Domain.Configuracion;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Gestiones;
using CaeManager.Domain.Proyectos;
using CaeManager.Domain.Trabajadores;
using CaeManager.Domain.Vehiculos;
using CaeManager.Domain.Visitas;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Repositories;
using CaeManager.Web.Features.Gestiones;
using CaeManager.Web.Features.Gestiones.Recursos;
using CaeManager.Web.Features.Proyectos;
using CaeManager.Web.Features.Proyectos.Recursos;
using CaeManager.Web.Features.Vehiculos;
using CaeManager.Web.Features.Vehiculos.Recursos;
using CaeManager.Web.Features.Visitas;
using CaeManager.Web.Features.Visitas.Recursos;
using ClosedXML.Excel;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CaeManager.IntegrationTests.Exportacion;

/// <summary>
/// Las cuatro exportaciones del cierre de listados (<c>/vehiculos</c>, <c>/proyectos</c>,
/// <c>/visitas</c> y <c>/gestiones</c> <c>/exportar.xlsx</c>) con las consultas REALES de cada
/// listado contra PostgreSQL. Por cada una: «Exportar todo» lleva lo del Tenant y nada de otro,
/// «Exportar esta vista» lleva el subconjunto que selecciona el criterio, lo que queda fuera del
/// alcance del usuario no sale, y la descarga deja una fila en la auditoría del Tenant propietario.
///
/// <para>
/// Límite del instrumento, el mismo que <c>ExportacionTrabajadoresTests</c>: llama a cada
/// <c>ExportarAsync</c> directamente, sin el routing ni la autorización por rol de ASP.NET (eso lo
/// miden los E2E de exportación y <c>ExportacionesDeListadosDeclaranLosRolesDeSuPaginaTests</c>);
/// el aislamiento que observa es el filtro de Tenant del <c>DbContext</c>, no RLS; y el alcance es
/// el que devuelve un <c>IAlcanceDatosService</c> de prueba: se mide que la exportación lo
/// respeta por pasar por la consulta del listado, no cómo se calcula.
/// </para>
/// </summary>
public class ExportacionVehiculosProyectosVisitasGestionesTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _otroTenant = Guid.NewGuid();
    private readonly Guid _usuario = Guid.NewGuid();
    private readonly DateOnly _hoy = DiaDeNegocio.Hoy();

    private Guid _clienteNorte;
    private Guid _clienteSur;
    private Guid _centroNorte;
    private Guid _furgon;
    private Guid _gamma;

    public async Task InitializeAsync()
    {
        await using (var c = CrearContexto(_tenant))
        {
            await c.Database.MigrateAsync();
            c.ParametrosSistema.Add(new ParametroSistema(30, 15, horasAvisoVisita: 48, horasCriticasVisita: 24));

            var norte = Empresa.CrearComoCliente("Cliente Norte S.L.", "B12345674", false, null, null);
            var sur = Empresa.CrearComoCliente("Cliente Sur S.L.", "B87654323", false, null, null);
            var alfa = new Empresa("Alfa Montajes S.L.", "B10000016");
            var tipo = new TipoDocumento("Reconocimiento médico", 12, true, 1, AmbitoAplicacion.Trabajador, RequisitoDocumental.Si);
            var gamma = new Empresa("Gamma Grúas S.L.", "B10380186");
            c.Empresas.AddRange(norte, sur, alfa, gamma);
            c.TiposDocumento.Add(tipo);
            await c.SaveChangesAsync();

            var centroNorte = new Centro(norte.Id, alfa.Id, "Planta Norte");
            var centroSur = new Centro(sur.Id, alfa.Id, "Planta Sur");
            var lucia = Trabajador.DeEmpresa(alfa.Id, "Lucía", "Prieto Ramos", "12345678Z");
            var iker = Trabajador.DeEmpresa(alfa.Id, "Iker", "Sanz Olmo", "00000000T");
            var furgon = Vehiculo.DeEmpresa(alfa.Id, "Furgón Uno", "Transit", "1111AAA");
            var grua = Vehiculo.DeEmpresa(gamma.Id, "Grúa Dos", "Liebherr", "2222BBB");
            c.Centros.AddRange(centroNorte, centroSur);
            c.Trabajadores.AddRange(lucia, iker);
            c.Vehiculos.AddRange(furgon, grua);
            await c.SaveChangesAsync();

            var reforma = Proyecto.Crear(norte.Id, centroNorte.Id, "Reforma nave", _hoy.AddDays(-30), null, null);
            var cubierta = Proyecto.Crear(norte.Id, centroNorte.Id, "Cubierta nueva", _hoy.AddDays(-20), null, null);
            var ampliacion = Proyecto.Crear(sur.Id, centroSur.Id, "Ampliación almacén", _hoy.AddDays(-10), null, null);
            var visitaNorte = new Visita(centroNorte.Id, _hoy.AddDays(2), _hoy.AddDays(2), null);
            var visitaSur = new Visita(centroSur.Id, _hoy.AddDays(3), _hoy.AddDays(3), null);
            visitaSur.MarcarNotificadoCliente(true);
            var visitaPasada = new Visita(centroNorte.Id, _hoy.AddDays(-9), _hoy.AddDays(-9), null);
            c.Proyectos.AddRange(reforma, cubierta, ampliacion);
            c.Visitas.AddRange(visitaNorte, visitaSur, visitaPasada);
            c.Gestiones.AddRange(new Gestion(lucia.Id, centroNorte.Id, tipo.Id), new Gestion(iker.Id, centroSur.Id, tipo.Id));
            await c.SaveChangesAsync();

            c.ProyectosTecnicos.Add(new ProyectoTecnico(reforma.Id, lucia.Id, _hoy.AddDays(-30)));
            c.VisitasTrabajadores.AddRange(new VisitaTrabajador(visitaNorte.Id, lucia.Id), new VisitaTrabajador(visitaSur.Id, iker.Id));
            await c.SaveChangesAsync();

            (_clienteNorte, _clienteSur, _centroNorte, _furgon, _gamma) = (norte.Id, sur.Id, centroNorte.Id, furgon.Id, gamma.Id);
        }

        // Los mismos nombres que busca cada test («Furgón», «Reforma», «Planta», «Prieto»): si el
        // filtro de Tenant no actuara, el criterio los traería.
        await using (var c = CrearContexto(_otroTenant))
        {
            c.ParametrosSistema.Add(new ParametroSistema(30, 15, horasAvisoVisita: 48, horasCriticasVisita: 24));
            var cliente = Empresa.CrearComoCliente("Cliente Ajeno S.L.", "B10000024", false, null, null);
            var empresa = new Empresa("Beta Servicios S.L.", "B10000032");
            var tipo = new TipoDocumento("Reconocimiento médico", 12, true, 1, AmbitoAplicacion.Trabajador, RequisitoDocumental.Si);
            c.Empresas.AddRange(cliente, empresa);
            c.TiposDocumento.Add(tipo);
            await c.SaveChangesAsync();

            var centro = new Centro(cliente.Id, empresa.Id, "Planta Ajena");
            var marta = Trabajador.DeEmpresa(empresa.Id, "Marta", "Prieto Vega", "00000001R");
            c.Centros.Add(centro);
            c.Trabajadores.Add(marta);
            c.Vehiculos.Add(Vehiculo.DeEmpresa(empresa.Id, "Furgón Ajeno", "Vito", "9999ZZZ"));
            await c.SaveChangesAsync();

            var visita = new Visita(centro.Id, _hoy.AddDays(2), _hoy.AddDays(2), null);
            c.Proyectos.Add(Proyecto.Crear(cliente.Id, centro.Id, "Reforma ajena", _hoy.AddDays(-5), null, null));
            c.Visitas.Add(visita);
            c.Gestiones.Add(new Gestion(marta.Id, centro.Id, tipo.Id));
            await c.SaveChangesAsync();
            c.VisitasTrabajadores.Add(new VisitaTrabajador(visita.Id, marta.Id));
            await c.SaveChangesAsync();
        }
    }

    public async Task DisposeAsync() => await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    // ---- Vehículos ----

    [Fact]
    public async Task Vehiculos_todo_lleva_los_del_Tenant_y_ninguno_de_otro()
    {
        var filas = await ExportarAsync("TituloPagina", (m, t, r) => VehiculosEndpoints.ExportarAsync(m, t, r, Eco<TextosVehiculos>(), default));

        filas.Select(f => f[2]).Should().BeEquivalentTo(["1111AAA", "2222BBB"]);
        filas.SelectMany(f => f).Should().NotContain("9999ZZZ").And.NotContain("Furgón Ajeno");
        filas.Single(f => f[2] == "1111AAA").Should().StartWith(["Furgón Uno", "Transit", "1111AAA", "Alfa Montajes S.L."]);
    }

    [Fact]
    public async Task Vehiculos_esta_vista_lleva_solo_lo_que_selecciona_la_busqueda_y_deja_rastro()
    {
        var filas = await ExportarAsync("TituloPagina",
            (m, t, r) => VehiculosEndpoints.ExportarAsync(m, t, r, Eco<TextosVehiculos>(), default, q: "Furgón", orden: "Nombre"));

        filas.Should().ContainSingle().Which[2].Should().Be("1111AAA");
        var rastro = await RastroUnicoAsync(nameof(Vehiculo));
        rastro.DatosDespues.Should().Contain("\"filas\":1").And.Contain("\"busqueda\"").And.Contain("\"orden\":\"Nombre\"");
        rastro.DatosDespues.Should().NotContain("Furgón", "el rastro dice que hubo búsqueda, no la copia");
    }

    [Fact]
    public async Task Vehiculos_fuera_del_alcance_del_usuario_no_salen()
    {
        var filas = await ExportarAsync("TituloPagina",
            (m, t, r) => VehiculosEndpoints.ExportarAsync(m, t, r, Eco<TextosVehiculos>(), default),
            new AlcanceDatosServiceFalso(vehiculoIds: [_furgon]));

        filas.Should().ContainSingle().Which[2].Should().Be("1111AAA");
    }

    [Fact]
    public async Task Vehiculos_quien_tiene_que_elegir_empresa_no_exporta_ni_deja_rastro()
    {
        await using var contexto = CrearContexto(_tenant);
        var mediador = new MediadorDeListados(contexto, new AlcanceDatosServiceFalso(), _tenant, pideElegirEmpresa: true);

        var resultado = await VehiculosEndpoints.ExportarAsync(
            mediador, new TenantActualAmbiental { TenantId = _tenant }, Registro(contexto), Eco<TextosVehiculos>(), default);

        resultado.Should().BeOfType<RedirectHttpResult>().Which.Url.Should().Be("/vehiculos");
        (await ExportacionesAsync(_tenant)).Should().BeEmpty();
    }

    [Fact]
    public async Task Vehiculos_esta_vista_respeta_el_filtro_de_Empresa_y_el_orden_de_la_rejilla()
    {
        var deGamma = await ExportarAsync("TituloPagina",
            (m, t, r) => VehiculosEndpoints.ExportarAsync(m, t, r, Eco<TextosVehiculos>(), default, empresa: _gamma.ToString()));
        var ascendente = await ExportarAsync("TituloPagina",
            (m, t, r) => VehiculosEndpoints.ExportarAsync(m, t, r, Eco<TextosVehiculos>(), default, orden: "Nombre"));
        var descendente = await ExportarAsync("TituloPagina",
            (m, t, r) => VehiculosEndpoints.ExportarAsync(m, t, r, Eco<TextosVehiculos>(), default, orden: "Nombre", desc: true));

        deGamma.Should().ContainSingle().Which[2].Should().Be("2222BBB");
        ascendente.Select(f => f[0]).Should().Equal("Furgón Uno", "Grúa Dos");
        descendente.Select(f => f[0]).Should().Equal("Grúa Dos", "Furgón Uno");
    }

    // ---- Proyectos ----

    [Fact]
    public async Task Proyectos_quien_tiene_que_elegir_empresa_no_exporta_ni_deja_rastro()
    {
        await using var contexto = CrearContexto(_tenant);
        var mediador = new MediadorDeListados(contexto, new AlcanceDatosServiceFalso(), _tenant, pideElegirEmpresa: true);

        var resultado = await ProyectosEndpoints.ExportarAsync(
            mediador, new TenantActualAmbiental { TenantId = _tenant }, Registro(contexto), Eco<TextosProyectos>(), default);

        resultado.Should().BeOfType<RedirectHttpResult>().Which.Url.Should().Be("/proyectos");
        (await ExportacionesAsync(_tenant)).Should().BeEmpty();
    }

    [Fact]
    public async Task Proyectos_todo_recorre_los_Clientes_empresariales_del_Tenant_y_ninguno_de_otro()
    {
        var filas = await ExportarAsync("Proyectos", (m, t, r) => ProyectosEndpoints.ExportarAsync(m, t, r, Eco<TextosProyectos>(), default));

        filas.Select(f => f[1]).Should().BeEquivalentTo(["Reforma nave", "Cubierta nueva", "Ampliación almacén"]);
        filas.SelectMany(f => f).Should().NotContain("Reforma ajena").And.NotContain("Cliente Ajeno S.L.");
        // Técnicos: el recuento de la fila y los nombres de su ventana de contexto, sin DNI.
        var reforma = filas.Single(f => f[1] == "Reforma nave");
        reforma[0].Should().Be("Cliente Norte S.L.");
        reforma[5].Should().Be("1");
        reforma[6].Should().Be("Lucía Prieto Ramos");
        filas.SelectMany(f => f).Should().NotContain(celda => celda.Contains("12345678Z"));
    }

    [Fact]
    public async Task Proyectos_esta_vista_lleva_el_Cliente_empresarial_elegido_y_la_busqueda()
    {
        var delCliente = await ExportarAsync("Proyectos",
            (m, t, r) => ProyectosEndpoints.ExportarAsync(m, t, r, Eco<TextosProyectos>(), default, cliente: _clienteNorte.ToString()));
        var conBusqueda = await ExportarAsync("Proyectos",
            (m, t, r) => ProyectosEndpoints.ExportarAsync(m, t, r, Eco<TextosProyectos>(), default, cliente: _clienteNorte.ToString(), q: "reforma"));
        var cerrados = await ExportarAsync("Proyectos",
            (m, t, r) => ProyectosEndpoints.ExportarAsync(m, t, r, Eco<TextosProyectos>(), default, cliente: _clienteNorte.ToString(), estado: "cerrados"));

        delCliente.Select(f => f[1]).Should().BeEquivalentTo(["Reforma nave", "Cubierta nueva"]);
        conBusqueda.Should().ContainSingle().Which[1].Should().Be("Reforma nave");
        cerrados.Should().BeEmpty("los dos Proyectos de ese Cliente empresarial están abiertos");

        var rastros = await ExportacionesAsync(_tenant);
        rastros.Should().HaveCount(3).And.OnlyContain(r => r.EntidadTipo == nameof(Proyecto));
        rastros.Should().Contain(r => r.DatosDespues!.Contains("\"filas\":1") && r.DatosDespues.Contains(_clienteNorte.ToString()));
    }

    [Fact]
    public async Task Proyectos_de_un_Cliente_empresarial_fuera_del_alcance_no_salen_ni_pidiendolo_por_Id()
    {
        var alcance = new AlcanceDatosServiceFalso(clienteIds: [_clienteNorte]);

        var todo = await ExportarAsync("Proyectos",
            (m, t, r) => ProyectosEndpoints.ExportarAsync(m, t, r, Eco<TextosProyectos>(), default), alcance);
        var pedido = await ExportarAsync("Proyectos",
            (m, t, r) => ProyectosEndpoints.ExportarAsync(m, t, r, Eco<TextosProyectos>(), default, cliente: _clienteSur.ToString()), alcance);

        todo.Select(f => f[1]).Should().BeEquivalentTo(["Reforma nave", "Cubierta nueva"]);
        pedido.Should().BeEmpty();
    }

    // ---- Visitas ----

    [Fact]
    public async Task Visitas_todo_lleva_el_historial_del_Tenant_y_ninguna_de_otro()
    {
        var filas = await ExportarAsync("Visitas", (m, r) => VisitasEndpoints.ExportarAsync(m, r, Eco<TextosVisitas>(), default));

        filas.Should().HaveCount(3, "las dos futuras y la pasada: «todo» no es solo las activas");
        filas.SelectMany(f => f).Should().NotContain("Planta Ajena").And.NotContain("Marta Prieto Vega");
        // Personas: el recuento de la lista y los nombres de su ventana de contexto, sin DNI.
        var norte = filas.Single(f => f[0] == "Planta Norte" && f[5] == "1");
        norte[6].Should().Be("Lucía Prieto Ramos");
        filas.SelectMany(f => f).Should().NotContain(celda => celda.Contains("12345678Z"));
    }

    [Fact]
    public async Task Visitas_esta_vista_lleva_solo_las_activas_que_selecciona_la_busqueda_y_deja_rastro()
    {
        var activas = await ExportarAsync("Visitas",
            (m, r) => VisitasEndpoints.ExportarAsync(m, r, Eco<TextosVisitas>(), default, activas: "true"));
        var vista = await ExportarAsync("Visitas",
            (m, r) => VisitasEndpoints.ExportarAsync(m, r, Eco<TextosVisitas>(), default, q: "Planta Sur", activas: "true"));

        activas.Should().HaveCount(2, "la pasada sale de la vista de activas");
        vista.Should().ContainSingle().Which[0].Should().Be("Planta Sur");

        var rastros = await ExportacionesAsync(_tenant);
        rastros.Should().HaveCount(2).And.OnlyContain(r => r.EntidadTipo == nameof(Visita));
        rastros.Should().Contain(r => r.DatosDespues!.Contains("\"filas\":1") && r.DatosDespues.Contains("\"busqueda\""));
        rastros.Should().OnlyContain(r => !r.DatosDespues!.Contains("Planta"));
    }

    /// <summary>La selección de la franja de estado («estado», varios nombres separados por coma) recorta el Excel igual que la lista.</summary>
    [Fact]
    public async Task Visitas_esta_vista_respeta_el_estado_de_la_documentacion_que_marca_la_franja()
    {
        var porGestionar = await ExportarAsync("Visitas",
            (m, r) => VisitasEndpoints.ExportarAsync(m, r, Eco<TextosVisitas>(), default, estado: "PorGestionar,Inventado"));
        var gestionadas = await ExportarAsync("Visitas",
            (m, r) => VisitasEndpoints.ExportarAsync(m, r, Eco<TextosVisitas>(), default, estado: "Gestionada,Cancelada"));

        porGestionar.Should().HaveCount(3, "control: ninguna de las tres está gestionada ni cancelada");
        gestionadas.Should().BeEmpty();

        var rastros = await ExportacionesAsync(_tenant);
        rastros.Should().Contain(r => r.DatosDespues!.Contains("Gestionada,Cancelada"))
            .And.NotContain(r => r.DatosDespues!.Contains("Inventado"), "el rastro lleva los estados reconocidos, no el texto de la URL");
    }

    [Fact]
    public async Task Visitas_de_un_Centro_fuera_del_alcance_no_salen()
    {
        var filas = await ExportarAsync("Visitas",
            (m, r) => VisitasEndpoints.ExportarAsync(m, r, Eco<TextosVisitas>(), default),
            new AlcanceDatosServiceFalso(centroIds: [_centroNorte]));

        filas.Should().HaveCount(2).And.OnlyContain(f => f[0] == "Planta Norte");
    }

    [Fact]
    public async Task Visitas_esta_vista_respeta_el_filtro_de_notificada_y_el_orden_de_la_rejilla()
    {
        var notificadas = await ExportarAsync("Visitas",
            (m, r) => VisitasEndpoints.ExportarAsync(m, r, Eco<TextosVisitas>(), default, notificado: "si"));
        var sinNotificar = await ExportarAsync("Visitas",
            (m, r) => VisitasEndpoints.ExportarAsync(m, r, Eco<TextosVisitas>(), default, notificado: "no"));
        var ascendente = await ExportarAsync("Visitas",
            (m, r) => VisitasEndpoints.ExportarAsync(m, r, Eco<TextosVisitas>(), default, activas: "true", orden: "CentroNombre"));
        var descendente = await ExportarAsync("Visitas",
            (m, r) => VisitasEndpoints.ExportarAsync(m, r, Eco<TextosVisitas>(), default, activas: "true", orden: "CentroNombre", desc: true));

        notificadas.Should().ContainSingle().Which[0].Should().Be("Planta Sur");
        sinNotificar.Select(f => f[0]).Should().Equal("Planta Norte", "Planta Norte");
        ascendente.Select(f => f[0]).Should().Equal("Planta Norte", "Planta Sur");
        descendente.Select(f => f[0]).Should().Equal("Planta Sur", "Planta Norte");
    }

    // ---- Gestiones ----

    [Fact]
    public async Task Gestiones_esta_vista_respeta_el_orden_de_la_rejilla()
    {
        var ascendente = await ExportarAsync("Gestiones",
            (m, r) => GestionesEndpoints.ExportarAsync(m, r, Eco<TextosGestiones>(), default, orden: "CentroNombre"));
        var descendente = await ExportarAsync("Gestiones",
            (m, r) => GestionesEndpoints.ExportarAsync(m, r, Eco<TextosGestiones>(), default, orden: "CentroNombre", desc: true));

        ascendente.Select(f => f[1]).Should().Equal("Planta Norte", "Planta Sur");
        descendente.Select(f => f[1]).Should().Equal("Planta Sur", "Planta Norte");
    }

    [Fact]
    public async Task Gestiones_todo_lleva_las_del_Tenant_y_ninguna_de_otro()
    {
        var filas = await ExportarAsync("Gestiones", (m, r) => GestionesEndpoints.ExportarAsync(m, r, Eco<TextosGestiones>(), default));

        filas.Select(f => f[1]).Should().BeEquivalentTo(["Planta Norte", "Planta Sur"]);
        filas.SelectMany(f => f).Should().NotContain("Planta Ajena").And.NotContain(celda => celda.Contains("Vega"));
        filas.Should().OnlyContain(f => f[3] == "EstadoPendiente");
    }

    [Fact]
    public async Task Gestiones_esta_vista_lleva_solo_lo_que_seleccionan_la_busqueda_y_el_estado_y_deja_rastro()
    {
        var vista = await ExportarAsync("Gestiones",
            (m, r) => GestionesEndpoints.ExportarAsync(m, r, Eco<TextosGestiones>(), default, q: "Prieto"));
        var completadas = await ExportarAsync("Gestiones",
            (m, r) => GestionesEndpoints.ExportarAsync(m, r, Eco<TextosGestiones>(), default, estado: nameof(EstadoGestion.Completada)));

        vista.Should().ContainSingle().Which[1].Should().Be("Planta Norte");
        completadas.Should().BeEmpty("las dos están pendientes");

        var rastros = await ExportacionesAsync(_tenant);
        rastros.Should().HaveCount(2).And.OnlyContain(r => r.EntidadTipo == nameof(Gestion));
        rastros.Should().Contain(r => r.DatosDespues!.Contains("\"filas\":1") && r.DatosDespues.Contains("\"busqueda\""));
        rastros.Should().Contain(r => r.DatosDespues!.Contains("\"filas\":0") && r.DatosDespues.Contains("\"estado\":\"Completada\""));
        rastros.Should().OnlyContain(r => !r.DatosDespues!.Contains("Prieto"));
    }

    [Fact]
    public async Task Gestiones_de_un_Centro_fuera_del_alcance_no_salen()
    {
        var filas = await ExportarAsync("Gestiones",
            (m, r) => GestionesEndpoints.ExportarAsync(m, r, Eco<TextosGestiones>(), default),
            new AlcanceDatosServiceFalso(centroIds: [_centroNorte]));

        filas.Should().ContainSingle().Which[1].Should().Be("Planta Norte");
    }

    [Fact]
    public async Task El_otro_Tenant_exporta_lo_suyo_y_su_rastro_no_va_al_de_este()
    {
        var gestiones = await ExportarAsync("Gestiones",
            (m, r) => GestionesEndpoints.ExportarAsync(m, r, Eco<TextosGestiones>(), default, q: "Prieto"), tenant: _otroTenant);

        gestiones.Should().ContainSingle().Which[1].Should().Be("Planta Ajena");
        (await ExportacionesAsync(_otroTenant)).Should().ContainSingle().Which.TenantId.Should().Be(_otroTenant);
        (await ExportacionesAsync(_tenant)).Should().BeEmpty();
    }

    // ---- Arnés ----

    private Task<List<List<string>>> ExportarAsync(
        string hoja, Func<IMediator, IRegistroExportacionService, Task<IResult>> exportar,
        IAlcanceDatosService? alcance = null, Guid? tenant = null) =>
        ExportarAsync(hoja, (m, _, r) => exportar(m, r), alcance, tenant);

    private async Task<List<List<string>>> ExportarAsync(
        string hoja, Func<IMediator, ITenantActual, IRegistroExportacionService, Task<IResult>> exportar,
        IAlcanceDatosService? alcance = null, Guid? tenant = null)
    {
        var tenantId = tenant ?? _tenant;
        await using var contexto = CrearContexto(tenantId);
        var mediador = new MediadorDeListados(contexto, alcance ?? new AlcanceDatosServiceFalso(), tenantId);

        var resultado = await exportar(mediador, new TenantActualAmbiental { TenantId = tenantId }, Registro(contexto));

        await using var stream = resultado.Should().BeOfType<FileStreamHttpResult>().Subject.FileStream;
        using var libro = new XLWorkbook(stream);
        var usado = libro.Worksheet(hoja).RangeUsed()!;
        var columnas = usado.ColumnCount();
        // Celdas por posición, no solo las usadas: una celda vacía no puede desplazar las siguientes.
        return usado.Rows().Skip(1)
            .Select(fila => Enumerable.Range(1, columnas).Select(i => fila.Cell(i).GetString()).ToList())
            .ToList();
    }

    /// <summary>Rastro REAL, contra la misma base: lo que se mide es la fila que queda.</summary>
    private RegistroExportacionService Registro(CaeManagerDbContext contexto) => new(
        new ActorFijo(ActorAuditoria.Normal(_usuario)),
        new RegistroAccesoDatoSensibleRepository(contexto, NullLogger<RegistroAccesoDatoSensibleRepository>.Instance));

    private async Task<RegistroAuditoria> RastroUnicoAsync(string entidadTipo)
    {
        var registro = (await ExportacionesAsync(_tenant)).Should().ContainSingle().Subject;
        registro.EntidadTipo.Should().Be(entidadTipo);
        registro.TenantId.Should().Be(_tenant, "la fila va al Tenant propietario de los datos exportados");
        registro.UsuarioId.Should().Be(_usuario);
        return registro;
    }

    private async Task<List<RegistroAuditoria>> ExportacionesAsync(Guid tenant)
    {
        await using var contexto = CrearContexto(tenant);
        return await contexto.RegistrosAuditoria
            .Where(r => r.Accion == RegistroAuditoria.AccionExportacion)
            .ToListAsync();
    }

    private static IStringLocalizer<T> Eco<T>() => new LocalizadorEco<T>();

    /// <summary>Devuelve la clave: las cabeceras y los estados del libro son nombres de recurso.</summary>
    private sealed class LocalizadorEco<T> : IStringLocalizer<T>
    {
        public LocalizedString this[string name] => new(name, name);
        public LocalizedString this[string name, params object[] arguments] => new(name, name);
        public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures) => [];
    }

    private sealed class ActorFijo(ActorAuditoria actor) : IActorAuditoria
    {
        public Task<ActorAuditoria> ObtenerAsync() => Task.FromResult(actor);
        public ActorAuditoria? ObtenerSiYaEstaResuelto() => actor;
    }

    /// <summary>Los handlers reales de los cuatro listados (y el del selector de Proyectos) detrás de <see cref="IMediator"/>.</summary>
    private sealed class MediadorDeListados(
        CaeManagerDbContext c, IAlcanceDatosService alcance, Guid tenant, bool pideElegirEmpresa = false) : IMediator
    {
        public async Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            object respuesta = request switch
            {
                // Con dos Tenants autorizados y el de origen activo, la página pide elegir empresa (estado 4a).
                ObtenerClientesAutorizadosQuery => pideElegirEmpresa
                    ? (IReadOnlyList<ClienteAutorizadoDto>)
                    [
                        new ClienteAutorizadoDto(tenant, "Tenant de origen", EsOrigen: true, EsGestionadoPorOperacion: false),
                        new ClienteAutorizadoDto(Guid.NewGuid(), "Tenant de cartera", EsOrigen: false, EsGestionadoPorOperacion: true)
                    ]
                    : (IReadOnlyList<ClienteAutorizadoDto>)
                        [new ClienteAutorizadoDto(tenant, "Tenant propietario", EsOrigen: true, EsGestionadoPorOperacion: false)],
                ObtenerVehiculosQuery consulta => await new ObtenerVehiculosQueryHandler(
                    c, c, alcance, c, c, new CalculoEstadoDocumentalService(c, c, c)).Handle(consulta, cancellationToken),
                ObtenerClientesParaSelectorQuery consulta => await new ObtenerClientesParaSelectorQueryHandler(c, alcance)
                    .Handle(consulta, cancellationToken),
                ObtenerProyectosQuery consulta => await new ObtenerProyectosQueryHandler(c, c, c, alcance)
                    .Handle(consulta, cancellationToken),
                ObtenerVisitasQuery consulta => await new ObtenerVisitasQueryHandler(c, c, c, c, c, c, alcance, c)
                    .Handle(consulta, cancellationToken),
                ObtenerGestionesQuery consulta => await new ObtenerGestionesQueryHandler(c, c, c, c, alcance)
                    .Handle(consulta, cancellationToken),
                _ => throw new NotSupportedException(request.GetType().Name),
            };
            return (TResponse)respuesta;
        }

        public Task<object?> Send(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest => throw new NotSupportedException();
        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification => Task.CompletedTask;
    }

    private CaeManagerDbContext CrearContexto(Guid tenantId)
    {
        var tenantActual = new TenantActualAmbiental { TenantId = tenantId };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual))
            .Options;
        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
    }
}
