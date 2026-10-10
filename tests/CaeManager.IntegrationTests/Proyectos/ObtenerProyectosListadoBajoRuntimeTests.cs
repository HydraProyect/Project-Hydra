using CaeManager.Application.Common;
using CaeManager.Application.Plataforma;
using CaeManager.Application.Proyectos.Queries.ObtenerProyectos;
using CaeManager.Domain.Asignaciones;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Operaciones;
using CaeManager.Domain.Proyectos;
using CaeManager.Domain.Tenants;
using CaeManager.Domain.Trabajadores;
using CaeManager.Infrastructure.Autorizacion;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Interceptors;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests.Proyectos;

/// <summary>
/// <b>El listado de Proyectos sin Cliente empresarial elegido lista solo lo que el usuario alcanza.</b>
/// <see cref="ObtenerProyectosQuery"/> dejó de exigir un Cliente empresarial y pagina, busca y filtra por
/// estado en SQL. Lo que antes acotaba «el Cliente empresarial pedido está en el alcance» ahora lo tiene
/// que acotar la consulta entera: filas, total y recuentos de la franja.
///
/// <para>
/// Contra PostgreSQL real, autenticando como <c>cae_app_runtime</c> —RLS siempre aplica— con el
/// <see cref="TenantRlsConnectionInterceptor"/> real, y con el <see cref="AlcanceDatosService"/> y el
/// handler de producción. La siembra va con el propietario de la base, por entidades y DbContext.
/// </para>
///
/// <para>
/// El alcance limitado a un Cliente empresarial se fabrica acotando la Asignación de Operación, que es
/// el único modo que admite el modelo (<c>RepartoDeCarteraPorClienteRetiradoTests</c>): ningún código de
/// producción la crea hoy, pero es el caso en el que «todos» y «los que alcanzo» dejan de coincidir.
/// </para>
/// </summary>
public class ObtenerProyectosListadoBajoRuntimeTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();

    private readonly Tenant _propietario = new("Tenant propietario de prueba");
    private readonly Tenant _otroPropietario = new("Otro Tenant propietario de prueba");

    private readonly Guid _administrador = Guid.NewGuid();
    private readonly Guid _gestorSinCartera = Guid.NewGuid();
    private readonly Guid _gestorBajoOperacionAcotada = Guid.NewGuid();

    private Guid _clienteA;
    private Guid _clienteB;
    private Guid _clienteDelOtroTenant;
    private Guid _empresaPropia;

    // Por fecha de inicio descendente, que es el orden de la lista.
    private Guid _ampliacionA;      // 01/05/2026, abierto
    private Guid _incendiosB;       // 01/04/2026, abierto
    private Guid _reformaNaveA;     // 12/03/2026, abierto, con un técnico
    private Guid _mantenimientoA;   // 10/01/2026, cerrado
    private Guid _reformaCubiertaB; // 10/06/2025, cerrado
    private Guid _proyectoDelOtroTenant;
    private Guid _tecnico;

    public async Task InitializeAsync()
    {
        var ahora = DateTime.UtcNow;

        await using (var contexto = ContextoDeSiembra(_propietario.Id))
        {
            await contexto.Database.MigrateAsync();
            contexto.Tenants.AddRange(_propietario, _otroPropietario);

            // Los roles los siembran las migraciones.
            var roles = await contexto.Roles.AsNoTracking().ToDictionaryAsync(r => r.Name!, r => r.Id);

            void Cuenta(Guid id, string rol)
            {
                contexto.Users.Add(new ApplicationUser
                {
                    Id = id,
                    TenantId = _propietario.Id,
                    UserName = $"{id:N}@caemanager.local",
                    NormalizedUserName = $"{id:N}@CAEMANAGER.LOCAL",
                    Email = $"{id:N}@caemanager.local",
                    NormalizedEmail = $"{id:N}@CAEMANAGER.LOCAL",
                    NombreCompleto = rol,
                    EmailConfirmed = true,
                    SecurityStamp = Guid.NewGuid().ToString(),
                    ConcurrencyStamp = Guid.NewGuid().ToString(),
                });
                contexto.UserRoles.Add(new IdentityUserRole<Guid> { UserId = id, RoleId = roles[rol] });
            }

            Cuenta(_administrador, Roles.Administrador);
            Cuenta(_gestorSinCartera, Roles.GestorCae);
            Cuenta(_gestorBajoOperacionAcotada, Roles.GestorCae);

            var clienteA = Empresa.CrearComoCliente("Cliente empresarial A", "B10380186", false, null, null);
            var clienteB = Empresa.CrearComoCliente("Cliente empresarial B", "B10380202", false, null, null);
            var propia = new Empresa("Empresa propia", "B10380194");
            contexto.Empresas.AddRange(clienteA, clienteB, propia);

            var centroA = new Centro(clienteA.Id, propia.Id, "Sede Sevilla");
            var centroB = new Centro(clienteB.Id, propia.Id, "Planta Bilbao");
            contexto.Centros.AddRange(centroA, centroB);

            var tecnico = Trabajador.DeEmpresa(propia.Id, "Nora", "Vidal", "12345678Z");
            contexto.Trabajadores.Add(tecnico);
            await contexto.SaveChangesAsync();

            Proyecto Nuevo(Empresa cliente, Centro centro, string nombre, DateOnly inicio, DateOnly? cierre = null)
            {
                var proyecto = Proyecto.Crear(cliente.Id, centro.Id, nombre, inicio, null, null);
                if (cierre is { } fechaCierre)
                    proyecto.Cerrar(fechaCierre);
                contexto.Proyectos.Add(proyecto);
                return proyecto;
            }

            var ampliacion = Nuevo(clienteA, centroA, "Ampliación oficinas", new DateOnly(2026, 5, 1));
            var incendios = Nuevo(clienteB, centroB, "Instalación contra incendios", new DateOnly(2026, 4, 1));
            var reformaNave = Nuevo(clienteA, centroA, "Reforma nave", new DateOnly(2026, 3, 12));
            var mantenimiento = Nuevo(clienteA, centroA, "Mantenimiento almacén", new DateOnly(2026, 1, 10), new DateOnly(2026, 2, 10));
            var reformaCubierta = Nuevo(clienteB, centroB, "Reforma cubierta", new DateOnly(2025, 6, 10), new DateOnly(2026, 2, 28));
            contexto.ProyectosTecnicos.Add(new ProyectoTecnico(reformaNave.Id, tecnico.Id, new DateOnly(2026, 3, 12)));

            // Alcance limitado al Cliente empresarial A: una operación acotada con la cartera universal de
            // un Gestor CAE colgando de ella.
            contexto.AsignacionesOperacion.Add(AsignacionOperacion.Raiz(_propietario.Id, ServicioCae.Outbound, ahora.AddDays(-1), ahora));
            var acotada = AsignacionOperacion.Interna(
                _propietario.Id, ServicioCae.Outbound, AmbitoAsignacion.DeRelacionCliente(clienteA.Id), ahora.AddDays(-1), null, ahora);
            contexto.AsignacionesOperacion.Add(acotada);
            contexto.AsignacionesCartera.Add(AsignacionCartera.Interna(
                acotada, _gestorBajoOperacionAcotada, AmbitoAsignacion.Universal, ahora.AddDays(-1), vigenciaHasta: null, ahora));

            await contexto.SaveChangesAsync();

            _clienteA = clienteA.Id;
            _clienteB = clienteB.Id;
            _empresaPropia = propia.Id;
            _ampliacionA = ampliacion.Id;
            _incendiosB = incendios.Id;
            _reformaNaveA = reformaNave.Id;
            _mantenimientoA = mantenimiento.Id;
            _reformaCubiertaB = reformaCubierta.Id;
            _tecnico = tecnico.Id;
        }

        await using (var contexto = ContextoDeSiembra(_otroPropietario.Id))
        {
            var cliente = Empresa.CrearComoCliente("Cliente empresarial del otro Tenant", "B10380186", false, null, null);
            var propia = new Empresa("Empresa propia del otro Tenant", "B10380194");
            contexto.Empresas.AddRange(cliente, propia);
            var centro = new Centro(cliente.Id, propia.Id, "Sede Sevilla");
            contexto.Centros.Add(centro);
            await contexto.SaveChangesAsync();

            // Mismo nombre de Centro y una palabra de búsqueda en común con los del Tenant propietario:
            // si RLS no acotara, la búsqueda lo traería.
            var proyecto = Proyecto.Crear(cliente.Id, centro.Id, "Reforma del otro Tenant", new DateOnly(2026, 6, 1), null, null);
            contexto.Proyectos.Add(proyecto);
            await contexto.SaveChangesAsync();

            _clienteDelOtroTenant = cliente.Id;
            _proyectoDelOtroTenant = proyecto.Id;
        }
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    private Guid[] LosCincoPorOrdenDeLaLista =>
        [_ampliacionA, _incendiosB, _reformaNaveA, _mantenimientoA, _reformaCubiertaB];

    private static Dictionary<string, int> Recuentos(int abiertos, int cerrados) => new()
    {
        [ObtenerProyectosQuery.EstadoAbiertos] = abiertos,
        [ObtenerProyectosQuery.EstadoCerrados] = cerrados
    };

    // ── Sin Cliente empresarial: todos los del Tenant propietario, paginados ──

    [Fact]
    public async Task Con_alcance_total_y_sin_Cliente_empresarial_lista_los_de_todos_por_inicio_descendente_con_su_Cliente_y_sus_tecnicos()
    {
        var resultado = await PedirAsync(_administrador, Roles.Administrador, new ObtenerProyectosQuery(ConRecuentosPorEstado: true));

        resultado.Elementos.Select(p => p.Id).Should().Equal(LosCincoPorOrdenDeLaLista);
        resultado.TotalElementos.Should().Be(5);
        resultado.RecuentosPorEstado.Should().Equal(Recuentos(abiertos: 3, cerrados: 2));

        var reformaNave = resultado.Elementos.Single(p => p.Id == _reformaNaveA);
        reformaNave.ClienteId.Should().Be(_clienteA);
        reformaNave.ClienteRazonSocial.Should().Be("Cliente empresarial A");
        reformaNave.CentroNombre.Should().Be("Sede Sevilla");
        reformaNave.EstaAbierto.Should().BeTrue();
        reformaNave.TecnicosActivos.Should().Equal(new TecnicoActivoListaDto(_tecnico, "Nora Vidal"));

        var reformaCubierta = resultado.Elementos.Single(p => p.Id == _reformaCubiertaB);
        reformaCubierta.ClienteRazonSocial.Should().Be("Cliente empresarial B");
        reformaCubierta.EstaAbierto.Should().BeFalse();
        reformaCubierta.FechaCierreReal.Should().Be(new DateOnly(2026, 2, 28));
    }

    [Fact]
    public async Task Pagina_en_SQL_sin_repetir_ni_perder_filas_y_el_total_cuenta_todas_las_paginas()
    {
        var paginas = new List<ResultadoPaginado<ProyectoListaDto>>();
        for (var pagina = 1; pagina <= 3; pagina++)
            paginas.Add(await PedirAsync(_administrador, Roles.Administrador, new ObtenerProyectosQuery(Pagina: pagina, TamanoPagina: 2)));

        paginas.Select(p => p.Elementos.Count).Should().Equal(2, 2, 1);
        paginas.SelectMany(p => p.Elementos).Select(p => p.Id).Should().Equal(LosCincoPorOrdenDeLaLista);
        paginas.Should().OnlyContain(p => p.TotalElementos == 5 && p.TotalPaginas == 3);
        // El técnico viaja con la página que trae su Proyecto y solo con ella.
        paginas[1].Elementos.Single(p => p.Id == _reformaNaveA).TecnicosActivos.Should().ContainSingle();
    }

    [Fact]
    public async Task La_busqueda_y_el_estado_se_filtran_en_SQL_y_los_recuentos_no_llevan_el_filtro_de_estado()
    {
        // Búsqueda por nombre del Proyecto, sin distinguir mayúsculas. «Reforma del otro Tenant» no entra.
        var reformas = await PedirAsync(_administrador, Roles.Administrador,
            new ObtenerProyectosQuery(Busqueda: " REFORMA ", ConRecuentosPorEstado: true));
        reformas.Elementos.Select(p => p.Id).Should().Equal(_reformaNaveA, _reformaCubiertaB);
        reformas.TotalElementos.Should().Be(2);
        reformas.RecuentosPorEstado.Should().Equal(Recuentos(abiertos: 1, cerrados: 1));

        // Búsqueda por nombre del Centro.
        var enBilbao = await PedirAsync(_administrador, Roles.Administrador, new ObtenerProyectosQuery(Busqueda: "bilbao"));
        enBilbao.Elementos.Select(p => p.Id).Should().Equal(_incendiosB, _reformaCubiertaB);

        // Estado: con página de dos, los abiertos no se pierden detrás de los cerrados ni al revés.
        var abiertos = await PedirAsync(_administrador, Roles.Administrador,
            new ObtenerProyectosQuery(SoloAbiertos: true, TamanoPagina: 2, ConRecuentosPorEstado: true));
        abiertos.Elementos.Select(p => p.Id).Should().Equal(_ampliacionA, _incendiosB);
        abiertos.TotalElementos.Should().Be(3);
        abiertos.RecuentosPorEstado.Should().Equal(Recuentos(abiertos: 3, cerrados: 2),
            "la franja dice cuántos quedarían al marcar cada estado: no se cuenta con el estado ya filtrado");

        var cerrados = await PedirAsync(_administrador, Roles.Administrador,
            new ObtenerProyectosQuery(SoloAbiertos: false, ConRecuentosPorEstado: true));
        cerrados.Elementos.Select(p => p.Id).Should().Equal(_mantenimientoA, _reformaCubiertaB);
        cerrados.TotalElementos.Should().Be(2);
        cerrados.RecuentosPorEstado.Should().Equal(Recuentos(abiertos: 3, cerrados: 2));

        // Los tres filtros a la vez.
        var cerradosDeA = await PedirAsync(_administrador, Roles.Administrador,
            new ObtenerProyectosQuery(_clienteA, SoloAbiertos: false, Busqueda: "almacén", ConRecuentosPorEstado: true));
        cerradosDeA.Elementos.Select(p => p.Id).Should().Equal(_mantenimientoA);
        cerradosDeA.RecuentosPorEstado.Should().Equal(Recuentos(abiertos: 0, cerrados: 1));
    }

    // ── Lo que solo PostgreSQL puede decir: empates, comodines y baja lógica ──

    [Fact]
    public async Task Los_Proyectos_con_la_misma_fecha_de_inicio_salen_por_Id_y_en_el_mismo_orden_en_todas_las_paginas()
    {
        var (cliente, centro) = await SembrarClienteAparteAsync();
        var inicio = new DateOnly(2026, 7, 1);
        var empatados = Enumerable.Range(1, 6)
            .Select(n => Proyecto.Crear(cliente, centro, $"Empatado {n}", inicio, null, null))
            .ToList();
        // PostgreSQL ordena un uuid por sus bytes, que es el orden de su texto en hexadecimal.
        var porId = empatados.Select(p => p.Id).OrderBy(id => id.ToString("N"), StringComparer.Ordinal).ToList();
        // Se guardan al revés, uno a uno: el orden físico de la tabla es el contrario del esperado, así que
        // la lista solo sale por Id si la consulta lo pide.
        await GuardarUnoAUnoAsync(empatados.OrderByDescending(p => p.Id.ToString("N"), StringComparer.Ordinal));

        var deUnaVez = await PedirAsync(_administrador, Roles.Administrador, new ObtenerProyectosQuery(cliente, TamanoPagina: 6));
        deUnaVez.Elementos.Select(p => p.Id).Should().Equal(porId);

        var paginas = new List<ResultadoPaginado<ProyectoListaDto>>();
        for (var pagina = 1; pagina <= 3; pagina++)
            paginas.Add(await PedirAsync(_administrador, Roles.Administrador, new ObtenerProyectosQuery(cliente, Pagina: pagina, TamanoPagina: 2)));

        paginas.SelectMany(p => p.Elementos).Select(p => p.Id).Should().Equal(porId,
            "con las seis filas empatadas, cada página tiene que cortar la misma ordenación: ni repetir ni perder");
        paginas.Should().OnlyContain(p => p.TotalElementos == 6);
    }

    [Fact]
    public async Task El_guion_bajo_y_el_porcentaje_de_la_busqueda_son_caracteres_y_no_comodines()
    {
        var (cliente, centro) = await SembrarClienteAparteAsync();
        var conGuion = Proyecto.Crear(cliente, centro, "Lote_1", new DateOnly(2026, 7, 4), null, null);
        var sinGuion = Proyecto.Crear(cliente, centro, "LoteX1", new DateOnly(2026, 7, 3), null, null);
        var conPorcentaje = Proyecto.Crear(cliente, centro, "Avance 50%hecho", new DateOnly(2026, 7, 2), null, null);
        var sinPorcentaje = Proyecto.Crear(cliente, centro, "Avance 50 no hecho", new DateOnly(2026, 7, 1), null, null);
        await GuardarUnoAUnoAsync([conGuion, sinGuion, conPorcentaje, sinPorcentaje]);

        async Task<IEnumerable<Guid>> BuscarAsync(string texto) =>
            (await PedirAsync(_administrador, Roles.Administrador, new ObtenerProyectosQuery(cliente, Busqueda: texto)))
            .Elementos.Select(p => p.Id);

        // Control positivo: están los cuatro y una búsqueda corriente los encuentra de dos en dos.
        (await BuscarAsync("lote")).Should().Equal(conGuion.Id, sinGuion.Id);
        (await BuscarAsync("avance 50")).Should().Equal(conPorcentaje.Id, sinPorcentaje.Id);

        // «_» como comodín de LIKE valdría por cualquier carácter: «e_1» traería también «LoteX1».
        (await BuscarAsync("e_1")).Should().Equal(conGuion.Id);
        (await BuscarAsync("_")).Should().Equal(conGuion.Id);
        // «%» como comodín valdría por cualquier tramo: «50%hecho» traería también «50 no hecho».
        (await BuscarAsync("50%hecho")).Should().Equal(conPorcentaje.Id);
        (await BuscarAsync("%")).Should().Equal(conPorcentaje.Id);
    }

    [Fact]
    public async Task El_Proyecto_de_un_Cliente_empresarial_dado_de_baja_no_se_lista_ni_se_cuenta()
    {
        var (cliente, centro) = await SembrarClienteAparteAsync();
        await GuardarUnoAUnoAsync([Proyecto.Crear(cliente, centro, "Obra del Cliente que se da de baja", new DateOnly(2026, 8, 1), null, null)]);

        // Control positivo: antes de la baja se lista.
        var antes = await PedirAsync(_administrador, Roles.Administrador, new ObtenerProyectosQuery(ConRecuentosPorEstado: true));
        antes.TotalElementos.Should().Be(6);
        antes.Elementos.Should().Contain(p => p.ClienteId == cliente);

        await using (var contexto = ContextoDeSiembra(_propietario.Id))
        {
            var empresa = await contexto.Empresas.SingleAsync(e => e.Id == cliente);
            empresa.MarcarComoEliminado(_administrador);
            await contexto.SaveChangesAsync();
        }

        var despues = await PedirAsync(_administrador, Roles.Administrador, new ObtenerProyectosQuery(ConRecuentosPorEstado: true));
        despues.Elementos.Select(p => p.Id).Should().Equal(LosCincoPorOrdenDeLaLista);
        despues.TotalElementos.Should().Be(5);
        despues.RecuentosPorEstado.Should().Equal(Recuentos(abiertos: 3, cerrados: 2));
        // Pedido por su Id tampoco: el join interno lo deja fuera.
        (await PedirAsync(_administrador, Roles.Administrador, new ObtenerProyectosQuery(cliente))).TotalElementos.Should().Be(0);
    }

    // ── Alcance ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Con_alcance_limitado_a_un_Cliente_empresarial_y_sin_pedir_ninguno_ni_lista_ni_cuenta_los_del_otro()
    {
        var resultado = await PedirAsync(_gestorBajoOperacionAcotada, Roles.GestorCae, new ObtenerProyectosQuery(ConRecuentosPorEstado: true));

        resultado.Elementos.Select(p => p.Id).Should().Equal(_ampliacionA, _reformaNaveA, _mantenimientoA);
        resultado.Elementos.Should().OnlyContain(p => p.ClienteId == _clienteA);
        resultado.TotalElementos.Should().Be(3, "el total es el de su alcance, no el del Tenant propietario");
        resultado.RecuentosPorEstado.Should().Equal(Recuentos(abiertos: 2, cerrados: 1),
            "la franja tampoco puede delatar con una cifra los Proyectos del Cliente empresarial B");

        // La búsqueda no abre lo que el alcance cierra: «bilbao» es el Centro del Cliente empresarial B.
        var buscando = await PedirAsync(_gestorBajoOperacionAcotada, Roles.GestorCae,
            new ObtenerProyectosQuery(Busqueda: "bilbao", ConRecuentosPorEstado: true));
        buscando.Elementos.Should().BeEmpty();
        buscando.TotalElementos.Should().Be(0);
        buscando.RecuentosPorEstado.Should().Equal(Recuentos(abiertos: 0, cerrados: 0));

        // Control positivo: con alcance total, esa misma pregunta trae los cinco y los dos de Bilbao.
        (await PedirAsync(_administrador, Roles.Administrador, new ObtenerProyectosQuery())).TotalElementos.Should().Be(5);
        (await PedirAsync(_administrador, Roles.Administrador, new ObtenerProyectosQuery(Busqueda: "bilbao"))).TotalElementos.Should().Be(2);
    }

    [Fact]
    public async Task Un_Cliente_empresarial_pedido_fuera_del_alcance_devuelve_la_lista_vacia_y_sus_recuentos_a_cero()
    {
        var resultado = await PedirAsync(_gestorBajoOperacionAcotada, Roles.GestorCae,
            new ObtenerProyectosQuery(_clienteB, ConRecuentosPorEstado: true));

        resultado.Elementos.Should().BeEmpty();
        resultado.TotalElementos.Should().Be(0);
        resultado.RecuentosPorEstado.Should().Equal(Recuentos(abiertos: 0, cerrados: 0));

        // Control positivo: el Cliente empresarial B tiene dos Proyectos y quien lo alcanza los ve.
        var comoAdministrador = await PedirAsync(_administrador, Roles.Administrador,
            new ObtenerProyectosQuery(_clienteB, ConRecuentosPorEstado: true));
        comoAdministrador.Elementos.Select(p => p.Id).Should().Equal(_incendiosB, _reformaCubiertaB);
        comoAdministrador.RecuentosPorEstado.Should().Equal(Recuentos(abiertos: 1, cerrados: 1));
    }

    [Fact]
    public async Task Un_Gestor_CAE_sin_cartera_vigente_no_lista_ningun_Proyecto()
    {
        var resultado = await PedirAsync(_gestorSinCartera, Roles.GestorCae, new ObtenerProyectosQuery(ConRecuentosPorEstado: true));

        resultado.Elementos.Should().BeEmpty("sin cartera el alcance es «ninguno», no «sin restricción»");
        resultado.TotalElementos.Should().Be(0);
        resultado.RecuentosPorEstado.Should().Equal(Recuentos(abiertos: 0, cerrados: 0));
    }

    // ── Aislamiento entre Tenants ─────────────────────────────────────────

    [Fact]
    public async Task Los_Proyectos_de_otro_Tenant_no_se_listan_ni_se_cuentan_bajo_RLS_ni_pidiendo_su_Cliente_empresarial()
    {
        await using var contexto = ContextoRuntime(_administrador, Roles.Administrador);
        var handler = new ObtenerProyectosQueryHandler(
            contexto, contexto, contexto, contexto, Alcance(contexto, _administrador, Roles.Administrador));

        // Alcance total dentro del Tenant propietario: lo único que deja fuera al otro Tenant es RLS.
        var todos = await handler.Handle(new ObtenerProyectosQuery(TamanoPagina: 100, ConRecuentosPorEstado: true), CancellationToken.None);
        todos.Elementos.Select(p => p.Id).Should().NotContain(_proyectoDelOtroTenant);
        todos.Elementos.Select(p => p.Id).Should().BeEquivalentTo(LosCincoPorOrdenDeLaLista);
        todos.TotalElementos.Should().Be(5);
        todos.RecuentosPorEstado.Should().Equal(Recuentos(abiertos: 3, cerrados: 2));

        var delOtro = await handler.Handle(
            new ObtenerProyectosQuery(_clienteDelOtroTenant, ConRecuentosPorEstado: true), CancellationToken.None);
        delOtro.Elementos.Should().BeEmpty();
        delOtro.TotalElementos.Should().Be(0);
        delOtro.RecuentosPorEstado.Should().Equal(Recuentos(abiertos: 0, cerrados: 0));

        // La fila no llega a la conexión de runtime: no es solo que el handler o los filtros de EF la descarten.
        (await contexto.Proyectos.IgnoreQueryFilters().CountAsync(p => p.Id == _proyectoDelOtroTenant))
            .Should().Be(0, "RLS acota la conexión al Tenant propietario en el que se opera");

        // Control positivo: el Proyecto del otro Tenant existe y su propio Tenant lo ve.
        await using var siembra = ContextoDeSiembra(_otroPropietario.Id);
        (await siembra.Proyectos.CountAsync(p => p.Id == _proyectoDelOtroTenant)).Should().Be(1);
    }

    // ── Arnés ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Siembra aparte, para los casos cuyas filas descuadrarían las cuentas de los demás: un tercer Cliente
    /// empresarial del Tenant propietario con su Centro. Los Proyectos los pone cada caso.
    /// </summary>
    private async Task<(Guid ClienteId, Guid CentroId)> SembrarClienteAparteAsync()
    {
        await using var contexto = ContextoDeSiembra(_propietario.Id);
        var cliente = Empresa.CrearComoCliente("Cliente empresarial aparte", "B10380210", false, null, null);
        contexto.Empresas.Add(cliente);
        var centro = new Centro(cliente.Id, _empresaPropia, "Centro aparte");
        contexto.Centros.Add(centro);
        await contexto.SaveChangesAsync();
        return (cliente.Id, centro.Id);
    }

    /// <summary>Guarda cada Proyecto en su propia escritura y en el orden recibido: el caso decide el orden físico.</summary>
    private async Task GuardarUnoAUnoAsync(IEnumerable<Proyecto> proyectos)
    {
        await using var contexto = ContextoDeSiembra(_propietario.Id);
        foreach (var proyecto in proyectos)
        {
            contexto.Proyectos.Add(proyecto);
            await contexto.SaveChangesAsync();
        }
    }

    private async Task<ResultadoPaginado<ProyectoListaDto>> PedirAsync(Guid usuarioId, string rol, ObtenerProyectosQuery consulta)
    {
        await using var contexto = ContextoRuntime(usuarioId, rol);
        return await new ObtenerProyectosQueryHandler(contexto, contexto, contexto, contexto, Alcance(contexto, usuarioId, rol))
            .Handle(consulta, CancellationToken.None);
    }

    private AlcanceDatosService Alcance(CaeManagerDbContext contexto, Guid usuarioId, string rol) =>
        new(contexto, new CurrentUserServiceFalso(usuarioId, rol, tenantOrigenId: _propietario.Id),
            new TenantActualAmbiental { TenantId = _propietario.Id }, new SesionPrivilegiadaAusente());

    /// <summary>Conexión de <c>cae_app_runtime</c> operando en el Tenant propietario: RLS aplica.</summary>
    private CaeManagerDbContext ContextoRuntime(Guid usuarioId, string rol)
    {
        var tenantActual = new TenantActualAmbiental { TenantId = _propietario.Id };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(BaseDatosPostgresDePruebas.CadenaComoRuntime(_cadenaConexion),
                npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(
                new TenantSelladoInterceptor(tenantActual),
                new TenantRlsConnectionInterceptor(
                    tenantActual, new SinTenantSeleccionado(),
                    new CurrentUserServiceFalso(usuarioId, rol, tenantOrigenId: _propietario.Id),
                    BaseDatosPostgresDePruebas.FirmanteContextoRls),
                new ConcurrenciaOptimistaInterceptor())
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
    }

    private CaeManagerDbContext ContextoDeSiembra(Guid tenantId)
    {
        var tenantActual = new TenantActualAmbiental { TenantId = tenantId };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual))
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
    }

    private sealed class SinTenantSeleccionado : IClienteActivoSeleccionado
    {
        public Guid? TenantIdSeleccionado => null;
        public Guid? AsignacionOperacionIdSeleccionada => null;
        public Guid? SesionPrivilegiadaIdSeleccionada => null;
    }
}
