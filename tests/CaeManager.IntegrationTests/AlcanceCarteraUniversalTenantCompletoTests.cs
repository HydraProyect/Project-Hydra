using CaeManager.Application.Plataforma;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Operaciones;
using CaeManager.Domain.Trabajadores;
using CaeManager.Domain.Vehiculos;
using CaeManager.Infrastructure.Autorizacion;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests;

/// <summary>
/// Decisión del propietario 2026-09-23: la Asignación de Cartera de un Gestor CAE es sobre el Tenant
/// entero, y con ella vigente el Gestor CAE ve y gestiona todas las ramas operativas de ese Tenant
/// —Clientes empresariales, Centros, Empresas, Subcontratas, Trabajadores y Vehículos— estén o no
/// unidas entre sí por un Centro, una Relación Empresarial o una Asignación.
///
/// Es autoridad de Operación, no de Propiedad: el alcance total del Tenant sigue sin concederse
/// (<see cref="AlcanceDatosService.TieneAccesoTotalAsync"/> = false) y las listas son explícitas, no
/// <c>null</c>. Sin cartera vigente, alcance cero; otro Tenant, nunca; el rol Cliente, sin cambios.
///
/// Mide <see cref="AlcanceDatosService"/> contra PostgreSQL real, con el filtro global de Tenant del
/// <see cref="CaeManagerDbContext"/>; la RLS no cambia en este incremento y no se prueba aquí.
/// </summary>
public class AlcanceCarteraUniversalTenantCompletoTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _otroTenant = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto(_tenant);
        await contexto.Database.MigrateAsync();
    }

    public async Task DisposeAsync() => await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    /// <summary>
    /// Un Tenant con piezas sueltas: nada de lo que sigue está unido a un Cliente empresarial por un
    /// Centro, una Relación Empresarial ni una Asignación, salvo lo que el nombre indique.
    /// </summary>
    private sealed record Escenario(
        Guid Cliente, Guid Propia, Guid OtraPropia, Guid Subcontrata,
        Guid CentroDelCliente, Guid CentroSinClienteEmpresarial,
        Guid TrabajadorPropio, Guid TrabajadorDeSubcontrata,
        Guid VehiculoPropio, Guid VehiculoDeSubcontrata);

    private async Task<Escenario> SembrarAsync(Guid tenant, string sufijo)
    {
        await using var contexto = CrearContexto(tenant);
        var cliente = Empresa.CrearComoCliente($"Cliente empresarial {sufijo}", "B10380186", false, null, null);
        var propia = new Empresa($"Empresa propia {sufijo}", "B10380194");
        var otraPropia = new Empresa($"Otra Empresa propia {sufijo}", "B10380202");
        var subcontrata = Empresa.CrearComoSubcontrata($"Subcontrata {sufijo}", null, "Estandar");
        contexto.Empresas.AddRange(cliente, propia, otraPropia, subcontrata);
        await contexto.SaveChangesAsync();

        var centroDelCliente = new Centro(cliente.Id, propia.Id, $"Centro del Cliente {sufijo}");
        // Centro cuyo «cliente» es una Empresa sin marca de Cliente empresarial (EsCritico null).
        var centroSinCliente = new Centro(otraPropia.Id, otraPropia.Id, $"Centro sin Cliente empresarial {sufijo}");
        contexto.Centros.AddRange(centroDelCliente, centroSinCliente);
        var trabajadorPropio = Trabajador.DeEmpresa(propia.Id, "Nora", "Vidal", "12345678Z");
        var trabajadorSub = Trabajador.DeSubcontrata(subcontrata.Id, "Leo", "Mas", "11111111H");
        contexto.Trabajadores.AddRange(trabajadorPropio, trabajadorSub);
        var vehiculoPropio = Vehiculo.DeEmpresa(propia.Id, "Furgoneta", "Modelo", $"1234ABC{sufijo}");
        var vehiculoSub = Vehiculo.DeSubcontrata(subcontrata.Id, "Camión", "Modelo", $"5678DEF{sufijo}");
        contexto.Vehiculos.AddRange(vehiculoPropio, vehiculoSub);
        await contexto.SaveChangesAsync();

        return new Escenario(cliente.Id, propia.Id, otraPropia.Id, subcontrata.Id,
            centroDelCliente.Id, centroSinCliente.Id, trabajadorPropio.Id, trabajadorSub.Id,
            vehiculoPropio.Id, vehiculoSub.Id);
    }

    private async Task OtorgarCarteraAsync(Guid tenant, Guid usuarioId, AmbitoAsignacion ambito,
        DateTime? vigenciaHasta = null)
    {
        await using var contexto = CrearContexto(tenant);
        var ahora = DateTime.UtcNow;
        var raiz = AsignacionOperacion.Raiz(tenant, ServicioCae.Outbound, ahora.AddDays(-2), ahora);
        contexto.AsignacionesOperacion.Add(raiz);
        contexto.AsignacionesCartera.Add(AsignacionCartera.Interna(raiz, usuarioId, ambito, ahora.AddDays(-1), vigenciaHasta, ahora));
        await contexto.SaveChangesAsync();
    }

    private async Task AltaUsuarioAsync(Guid tenant, Guid usuarioId, Guid? clienteId = null, Guid? coordinadorUsuarioId = null,
        bool desactivado = false)
    {
        await using var contexto = CrearContexto(tenant);
        var usuario = new ApplicationUser
        {
            Id = usuarioId,
            UserName = $"u-{usuarioId:N}@ejemplo.test",
            Email = $"u-{usuarioId:N}@ejemplo.test",
            ClienteId = clienteId,
            CoordinadorUsuarioId = coordinadorUsuarioId,
            TenantId = tenant
        };
        if (desactivado) usuario.Desactivar();
        contexto.Users.Add(usuario);
        await contexto.SaveChangesAsync();
    }

    private sealed record Alcance(
        bool AccesoTotal,
        IReadOnlyList<Guid>? Clientes, IReadOnlyList<Guid>? Centros, IReadOnlyList<Guid>? CentrosGestion,
        IReadOnlyList<Guid>? Empresas, IReadOnlyList<Guid>? EmpresasGestion,
        IReadOnlyList<Guid>? Subcontratas, IReadOnlyList<Guid>? SubcontratasGestion,
        IReadOnlyList<Guid>? Trabajadores, IReadOnlyList<Guid>? Vehiculos);

    private async Task<Alcance> MedirAsync(Guid usuarioId, string rol, Guid tenant, Guid? gestorDeLente = null)
    {
        await using var contexto = CrearContexto(tenant);
        var s = new AlcanceDatosService(contexto, new CurrentUserServiceFalso(usuarioId, rol, tenantOrigenId: tenant),
            new TenantActualAmbiental { TenantId = tenant }, new SesionPrivilegiadaAusente(),
            gestorDeLente is { } g ? new LenteGestorFija(g) : null);
        return new Alcance(
            await s.TieneAccesoTotalAsync(),
            await s.ObtenerClienteIdsVisiblesAsync(), await s.ObtenerCentroIdsVisiblesAsync(), await s.ObtenerCentroIdsParaGestionAsync(),
            await s.ObtenerEmpresaIdsVisiblesAsync(), await s.ObtenerEmpresaIdsParaGestionAsync(),
            await s.ObtenerSubcontrataIdsVisiblesAsync(), await s.ObtenerSubcontrataIdsParaGestionAsync(),
            await s.ObtenerTrabajadorIdsVisiblesAsync(), await s.ObtenerVehiculoIdsVisiblesAsync());
    }

    private static void DebeAlcanzarTodoElTenant(Alcance a, Escenario e)
    {
        // AssertionScope: una rama que falle no oculta a las demás (la medición es rama por rama).
        using var _ = new AssertionScope();
        a.AccesoTotal.Should().BeFalse("la cartera es autoridad de Operación, no el alcance total de un rol de Propiedad");
        a.Clientes.Should().NotBeNull().And.Contain(e.Cliente)
            .And.NotContain([e.Propia, e.Subcontrata], "la lista de Clientes sigue siendo la de Clientes empresariales");
        a.Centros.Should().NotBeNull().And.Contain([e.CentroDelCliente, e.CentroSinClienteEmpresarial]);
        a.CentrosGestion.Should().NotBeNull().And.Contain([e.CentroDelCliente, e.CentroSinClienteEmpresarial]);
        a.Empresas.Should().NotBeNull().And.Contain([e.Propia, e.OtraPropia]);
        a.EmpresasGestion.Should().NotBeNull().And.Contain([e.Propia, e.OtraPropia]);
        a.Subcontratas.Should().NotBeNull().And.Contain(e.Subcontrata, "una Subcontrata sin Relación Empresarial también es del Tenant");
        // Los Commands de Subcontrata cargan la Empresa sin comprobar su tipo: si esta lista trajera
        // la Empresa propia o un Cliente empresarial, se podrían tratar como Subcontrata.
        a.SubcontratasGestion.Should().NotBeNull().And.Contain(e.Subcontrata)
            .And.NotContain([e.Propia, e.OtraPropia, e.Cliente], "solo las Empresas que son Subcontrata");
        a.Trabajadores.Should().NotBeNull().And.Contain([e.TrabajadorPropio, e.TrabajadorDeSubcontrata],
            "un Trabajador de Subcontrata sin Asignación también es del Tenant");
        a.Vehiculos.Should().NotBeNull().And.Contain([e.VehiculoPropio, e.VehiculoDeSubcontrata]);
    }

    private static void NoDebeAlcanzarNada(Alcance a)
    {
        a.AccesoTotal.Should().BeFalse();
        foreach (var lista in new[] { a.Clientes, a.Centros, a.CentrosGestion, a.Empresas, a.EmpresasGestion,
                     a.Subcontratas, a.SubcontratasGestion, a.Trabajadores, a.Vehiculos })
            lista.Should().NotBeNull("sin cartera el alcance es [] (falla cerrado), no null").And.BeEmpty();
    }

    [Fact]
    public async Task Gestor_con_cartera_universal_vigente_alcanza_todas_las_ramas_del_Tenant()
    {
        var e = await SembrarAsync(_tenant, "A");
        var gestor = Guid.NewGuid();
        await OtorgarCarteraAsync(_tenant, gestor, AmbitoAsignacion.Universal);

        DebeAlcanzarTodoElTenant(await MedirAsync(gestor, "GestorCae", _tenant), e);
    }

    [Fact]
    public async Task Gestor_con_cartera_universal_alcanza_el_Tenant_aunque_no_tenga_ningun_Cliente_empresarial()
    {
        // Un Tenant Outbound recién incorporado puede tener solo su Empresa propia y Subcontratas.
        // Que la lista de Clientes salga vacía no puede cortar las demás ramas.
        Guid propia, subcontrata, centro, trabajadorPropio, trabajadorSub, vehiculoSub;
        await using (var contexto = CrearContexto(_tenant))
        {
            var p = new Empresa("Empresa propia sin Clientes", "B10380194");
            var s = Empresa.CrearComoSubcontrata("Subcontrata sin Clientes", null, "Estandar");
            contexto.Empresas.AddRange(p, s);
            await contexto.SaveChangesAsync();
            var c = new Centro(p.Id, p.Id, "Centro propio");
            var tp = Trabajador.DeEmpresa(p.Id, "Nora", "Vidal", "12345678Z");
            var ts = Trabajador.DeSubcontrata(s.Id, "Leo", "Mas", "11111111H");
            var vs = Vehiculo.DeSubcontrata(s.Id, "Camión", "Modelo", "5678DEF");
            contexto.Centros.Add(c);
            contexto.Trabajadores.AddRange(tp, ts);
            contexto.Vehiculos.Add(vs);
            await contexto.SaveChangesAsync();
            (propia, subcontrata, centro, trabajadorPropio, trabajadorSub, vehiculoSub) = (p.Id, s.Id, c.Id, tp.Id, ts.Id, vs.Id);
        }
        var gestor = Guid.NewGuid();
        await OtorgarCarteraAsync(_tenant, gestor, AmbitoAsignacion.Universal);

        var a = await MedirAsync(gestor, "GestorCae", _tenant);

        using var _ = new AssertionScope();
        a.Clientes.Should().NotBeNull().And.BeEmpty("el Tenant no tiene ningún Cliente empresarial");
        a.Centros.Should().NotBeNull().And.Contain(centro);
        a.CentrosGestion.Should().NotBeNull().And.Contain(centro);
        a.Empresas.Should().NotBeNull().And.Contain(propia);
        a.EmpresasGestion.Should().NotBeNull().And.Contain(propia);
        a.Subcontratas.Should().NotBeNull().And.Contain(subcontrata);
        a.SubcontratasGestion.Should().NotBeNull().And.Contain(subcontrata);
        a.Trabajadores.Should().NotBeNull().And.Contain([trabajadorPropio, trabajadorSub]);
        a.Vehiculos.Should().NotBeNull().And.Contain(vehiculoSub);
    }

    [Fact]
    public async Task Coordinador_con_un_Gestor_con_cartera_universal_alcanza_todas_las_ramas_del_Tenant()
    {
        var e = await SembrarAsync(_tenant, "A");
        var coordinador = Guid.NewGuid();
        var gestor = Guid.NewGuid();
        await AltaUsuarioAsync(_tenant, coordinador);
        await AltaUsuarioAsync(_tenant, gestor, coordinadorUsuarioId: coordinador);
        await OtorgarCarteraAsync(_tenant, gestor, AmbitoAsignacion.Universal);

        DebeAlcanzarTodoElTenant(await MedirAsync(coordinador, "CoordinadorCae", _tenant), e);
    }

    [Fact]
    public async Task Coordinador_sigue_heredando_la_cartera_universal_de_un_Gestor_desactivado()
    {
        // Decisión del propietario, opción C (2026-09-24): desactivar a un Gestor CAE no cierra sus
        // Asignaciones de Cartera y su Coordinador CAE las sigue heredando, por continuidad del servicio.
        var e = await SembrarAsync(_tenant, "A");
        var coordinador = Guid.NewGuid();
        var gestor = Guid.NewGuid();
        await AltaUsuarioAsync(_tenant, coordinador);
        await AltaUsuarioAsync(_tenant, gestor, coordinadorUsuarioId: coordinador, desactivado: true);
        await OtorgarCarteraAsync(_tenant, gestor, AmbitoAsignacion.Universal);

        DebeAlcanzarTodoElTenant(await MedirAsync(coordinador, "CoordinadorCae", _tenant), e);
    }

    [Fact]
    public async Task Gestor_sin_cartera_o_con_la_cartera_universal_caducada_no_alcanza_nada()
    {
        await SembrarAsync(_tenant, "A");
        var caducado = Guid.NewGuid();
        await OtorgarCarteraAsync(_tenant, caducado, AmbitoAsignacion.Universal, vigenciaHasta: DateTime.UtcNow.AddMinutes(-1));

        NoDebeAlcanzarNada(await MedirAsync(Guid.NewGuid(), "GestorCae", _tenant));
        NoDebeAlcanzarNada(await MedirAsync(caducado, "GestorCae", _tenant));
    }

    [Fact]
    public async Task La_cartera_universal_en_un_Tenant_no_alcanza_nada_de_otro_Tenant()
    {
        var a = await SembrarAsync(_tenant, "A");
        var b = await SembrarAsync(_otroTenant, "B");
        var gestor = Guid.NewGuid();
        await OtorgarCarteraAsync(_tenant, gestor, AmbitoAsignacion.Universal);

        var enA = await MedirAsync(gestor, "GestorCae", _tenant);
        DebeAlcanzarTodoElTenant(enA, a);
        var todasEnA = new[] { enA.Clientes, enA.Centros, enA.Empresas, enA.Subcontratas, enA.Trabajadores, enA.Vehiculos }
            .SelectMany(l => l!).ToHashSet();
        todasEnA.Should().NotContain([b.Cliente, b.Propia, b.OtraPropia, b.Subcontrata, b.CentroDelCliente,
            b.CentroSinClienteEmpresarial, b.TrabajadorPropio, b.TrabajadorDeSubcontrata, b.VehiculoPropio, b.VehiculoDeSubcontrata]);

        // El mismo Gestor CAE, situado en el otro Tenant sin cartera allí: alcance cero.
        NoDebeAlcanzarNada(await MedirAsync(gestor, "GestorCae", _otroTenant));
    }

    [Fact]
    public async Task El_rol_Cliente_no_gana_el_Tenant_y_no_gestiona_aunque_exista_una_cartera_universal()
    {
        var e = await SembrarAsync(_tenant, "A");
        var usuarioPortal = Guid.NewGuid();
        await AltaUsuarioAsync(_tenant, usuarioPortal, clienteId: e.Cliente);
        // Una cartera universal a nombre del propio usuario de portal no le cambia nada: el rol decide.
        await OtorgarCarteraAsync(_tenant, usuarioPortal, AmbitoAsignacion.Universal);

        var a = await MedirAsync(usuarioPortal, "Cliente", _tenant);

        a.Clientes.Should().Equal([e.Cliente], "control positivo: el portal ve su Cliente empresarial");
        a.Centros.Should().Contain(e.CentroDelCliente).And.NotContain(e.CentroSinClienteEmpresarial);
        a.Trabajadores.Should().NotContain([e.TrabajadorPropio, e.TrabajadorDeSubcontrata]);
        a.Subcontratas.Should().NotContain(e.Subcontrata);
        a.CentrosGestion.Should().BeEmpty();
        a.EmpresasGestion.Should().BeEmpty();
        a.SubcontratasGestion.Should().BeEmpty();
    }

    [Fact]
    public async Task Consulta_sigue_con_alcance_total_de_lectura()
    {
        await SembrarAsync(_tenant, "A");

        var a = await MedirAsync(Guid.NewGuid(), "Consulta", _tenant);

        a.AccesoTotal.Should().BeTrue();
        a.Trabajadores.Should().BeNull();
    }

    [Fact]
    public async Task La_lente_de_demo_de_un_Gestor_con_cartera_universal_muestra_el_Tenant_entero_sin_acceso_total()
    {
        var e = await SembrarAsync(_tenant, "A");
        var gestor = Guid.NewGuid();
        await OtorgarCarteraAsync(_tenant, gestor, AmbitoAsignacion.Universal);

        DebeAlcanzarTodoElTenant(await MedirAsync(Guid.NewGuid(), "Administrador", _tenant, gestorDeLente: gestor), e);
    }

    [Fact]
    public async Task La_lente_de_demo_de_un_Gestor_con_cartera_por_Cliente_sigue_estrechando_a_ese_Cliente()
    {
        var e = await SembrarAsync(_tenant, "A");
        var gestor = Guid.NewGuid();
        await OtorgarCarteraAsync(_tenant, gestor, AmbitoAsignacion.DeRelacionCliente(e.Cliente));

        var a = await MedirAsync(Guid.NewGuid(), "Administrador", _tenant, gestorDeLente: gestor);

        a.AccesoTotal.Should().BeFalse();
        a.Clientes.Should().Equal([e.Cliente]);
        a.Centros.Should().Contain(e.CentroDelCliente).And.NotContain(e.CentroSinClienteEmpresarial,
            "una cartera por Cliente empresarial (reparto que retira el incremento 2) no gana el Tenant entero");
        a.Subcontratas.Should().NotContain(e.Subcontrata);
        a.Trabajadores.Should().Contain(e.TrabajadorPropio).And.NotContain(e.TrabajadorDeSubcontrata);
    }

    [Fact]
    public async Task Una_cartera_universal_bajo_una_operacion_acotada_a_un_Cliente_empresarial_solo_da_ese_Cliente()
    {
        var e = await SembrarAsync(_tenant, "A");
        var gestor = Guid.NewGuid();
        await OtorgarCarteraBajoOperacionAcotadaAsync(_tenant, gestor, operacionSobre: e.Cliente, AmbitoAsignacion.Universal);

        var a = await MedirAsync(gestor, "GestorCae", _tenant);

        using var _ = new AssertionScope();
        a.AccesoTotal.Should().BeFalse();
        a.Clientes.Should().Equal([e.Cliente], "el ámbito efectivo es la intersección con el de la operación que la ampara");
        a.Centros.Should().Contain(e.CentroDelCliente).And.NotContain(e.CentroSinClienteEmpresarial,
            "una operación acotada nunca concede el Tenant entero");
        a.Subcontratas.Should().NotContain(e.Subcontrata);
        a.Trabajadores.Should().NotContain(e.TrabajadorDeSubcontrata);
        a.Vehiculos.Should().NotContain(e.VehiculoDeSubcontrata);
    }

    [Fact]
    public async Task Una_cartera_de_otro_Cliente_bajo_una_operacion_acotada_no_da_nada()
    {
        var e = await SembrarAsync(_tenant, "A");
        var gestor = Guid.NewGuid();
        await OtorgarCarteraBajoOperacionAcotadaAsync(_tenant, gestor, operacionSobre: e.Cliente,
            AmbitoAsignacion.DeRelacionCliente(e.Propia));

        NoDebeAlcanzarNada(await MedirAsync(gestor, "GestorCae", _tenant));
    }

    /// <summary>
    /// Una operación interna acotada a un Cliente empresarial (AsignacionOperacion.Interna) con una
    /// cartera colgada de ella. Hoy ningún productor la crea, pero el dominio la admite.
    /// </summary>
    private async Task OtorgarCarteraBajoOperacionAcotadaAsync(Guid tenant, Guid usuarioId, Guid operacionSobre,
        AmbitoAsignacion ambitoCartera)
    {
        await using var contexto = CrearContexto(tenant);
        var ahora = DateTime.UtcNow;
        var acotada = AsignacionOperacion.Interna(tenant, ServicioCae.Outbound,
            AmbitoAsignacion.DeRelacionCliente(operacionSobre), ahora.AddDays(-2), vigenciaHasta: null, ahora);
        contexto.AsignacionesOperacion.Add(acotada);
        contexto.AsignacionesCartera.Add(AsignacionCartera.Interna(acotada, usuarioId, ambitoCartera, ahora.AddDays(-1), null, ahora));
        await contexto.SaveChangesAsync();
    }

    private sealed class LenteGestorFija(Guid gestorUsuarioId) : CaeManager.Application.VistaDemo.IVistaDemoActual
    {
        public Task<bool> EstaDisponibleAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task<CaeManager.Application.VistaDemo.VistaDemoEfectiva?> ObtenerEfectivaAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<CaeManager.Application.VistaDemo.VistaDemoEfectiva?>(
                new(CaeManager.Application.VistaDemo.VistaDemo.GestorCae, gestorUsuarioId));

        public Task<IReadOnlyList<CaeManager.Application.VistaDemo.GestorDeVistaDemo>> ObtenerGestoresElegiblesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<CaeManager.Application.VistaDemo.GestorDeVistaDemo>>([]);

        public Task<IReadOnlyList<Guid>?> ObtenerTenantIdsAcotadosAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Guid>?>(null);
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
