using CaeManager.Application.Plataforma;
using CaeManager.Application.Tenants;
using CaeManager.Application.Tenants.Commands.DesactivarDelegacionTenant;
using CaeManager.Application.Tenants.Commands.ReactivarDelegacionTenant;
using CaeManager.Domain.Clientes;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Operaciones;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.Autorizacion;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Operaciones;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Interceptors;
using CaeManager.Infrastructure.Persistence.Repositories;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CaeManager.IntegrationTests.Operaciones;

/// <summary>
/// Los defectos que encontró la revisión final de F1, cada uno con el escenario
/// concreto que fallaba. No son tests de "que funcione": son la prueba de que
/// cada agujero está cerrado.
/// </summary>
public class CorreccionesRevisionF1Tests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _gestorConsultora = Guid.NewGuid();
    private readonly Guid _gestorPropietario = Guid.NewGuid();
    private Guid _consultora;
    private Guid _propietario;
    private Guid _clienteId;
    private Guid _delegacionId;

    public async Task InitializeAsync()
    {
        await using var inicial = CrearContexto(Guid.NewGuid());
        await inicial.Database.MigrateAsync();

        var consultora = new Tenant("Consultora F1", PerfilVocabularioTenant.Consultora);
        var propietario = new Tenant("Propietario F1", PerfilVocabularioTenant.ClienteDirecto);
        inicial.Tenants.Add(consultora);
        inicial.Tenants.Add(propietario);
        await inicial.SaveChangesAsync();

        _consultora = consultora.Id;
        _propietario = propietario.Id;

        await using var contexto = CrearContexto(_propietario);

        contexto.Users.Add(new ApplicationUser
        {
            Id = _gestorConsultora,
            TenantId = _consultora,
            UserName = "g@consultora",
            Email = "g@consultora"
        });
        contexto.Users.Add(new ApplicationUser
        {
            Id = _gestorPropietario,
            TenantId = _propietario,
            UserName = "g@propietario",
            Email = "g@propietario"
        });

        var delegacion = new DelegacionTenant(_consultora, _propietario);
        contexto.DelegacionesTenant.Add(delegacion);
        contexto.AsignacionesOperadorDelegado.Add(
            new AsignacionOperadorDelegado(delegacion.Id, _gestorConsultora, Roles.GestorCae));
        _delegacionId = delegacion.Id;

        var cliente = Empresa.CrearComoCliente("Cliente F1", "B12345674", false, null, _gestorConsultora);
        contexto.Empresas.Add(cliente);

        contexto.AsignacionesOperacion.Add(
            AsignacionOperacion.Raiz(_propietario, ServicioCae.Outbound, DateTime.UtcNow, DateTime.UtcNow));

        await contexto.SaveChangesAsync();
        _clienteId = cliente.Id;
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    // ---------- B3: el alcance no se ensancha ----------

    [Fact]
    public async Task Un_gestor_delegado_ve_solo_sus_clientes_no_todos_los_del_tenant()
    {
        // Antes de la corrección, el backfill le daba una cartera universal y
        // el alcance la expandía a todos los clientes del tenant delegado: un
        // gestor que veía sus 3 clientes pasaba a ver los 200.
        await using (var otros = CrearContexto(_propietario))
        {
            otros.Clientes.Add(new Cliente("Cliente de otro gestor", "B10380186", false));
            otros.Clientes.Add(new Cliente("Cliente sin gestor", "B10380194", false));
            await otros.SaveChangesAsync();
        }

        await EjecutarBackfillAsync();

        await using var contexto = CrearContexto(_propietario);
        var alcance = new AlcanceDatosService(
            contexto,
            new CurrentUserServiceFalso(_gestorConsultora, Roles.GestorCae, tenantOrigenId: _consultora),
            new TenantActualAmbiental { TenantId = _propietario },
            new SesionPrivilegiadaAusente());

        var visibles = await alcance.ObtenerClienteIdsVisiblesAsync();

        visibles.Should().BeEquivalentTo([_clienteId]);
    }

    [Fact]
    public async Task El_backfill_no_emite_cartera_universal_para_un_rol_de_cartera()
    {
        await EjecutarBackfillAsync();

        await using var contexto = CrearContexto(_propietario);
        var universales = await contexto.AsignacionesCartera
            .Where(c => c.UsuarioId == _gestorConsultora && c.AmbitoRelacionClienteId == null)
            .ToListAsync();

        universales.Should().BeEmpty();
    }

    // ---------- D2: se cierran todas las carteras, no hasta la primera ----------

    [Fact]
    public async Task Reasignar_cierra_todas_las_carteras_vigentes_del_cliente_sea_cual_sea_su_orden()
    {
        // Dos carteras vigentes sobre el mismo cliente son posibles: el índice
        // único es POR OPERACIÓN, así que una interna y una externa conviven.
        // Con el `return` dentro del bucle, si la coincidente salía primero las
        // demás quedaban abiertas y su usuario conservaba el acceso.
        await EjecutarBackfillAsync();

        var ahora = DateTime.UtcNow;
        await using (var preparacion = CrearContexto(_propietario))
        {
            var raiz = await preparacion.AsignacionesOperacion
                .FirstAsync(o => o.EsRaiz && o.PropietarioTenantId == _propietario);

            preparacion.AsignacionesCartera.Add(AsignacionCartera.Interna(
                raiz, _gestorPropietario, AmbitoAsignacion.DeRelacionCliente(_clienteId),
                ahora, null, ahora));
            await preparacion.SaveChangesAsync();
        }

        await using (var contexto = CrearContexto(_propietario))
        {
            (await contexto.AsignacionesCartera
                    .CountAsync(c => c.AmbitoRelacionClienteId == _clienteId && c.Estado == EstadoAsignacion.Vigente))
                .Should().Be(2, "el escenario exige dos vigentes sobre el mismo cliente");

            var writer = new AsignacionesOperativasWriter(
                contexto, new TenantActualAmbiental { TenantId = _propietario },
                new CurrentUserServiceFalso(_gestorPropietario, Roles.Administrador));

            // Se reasigna al que YA tiene una de las dos: la otra tiene que
            // cerrarse igualmente.
            await writer.ReasignarCarteraClienteAsync(_clienteId, _gestorPropietario);
            await contexto.SaveChangesAsync();
        }

        await using var verificacion = CrearContexto(_propietario);
        var vigentes = await verificacion.AsignacionesCartera
            .Where(c => c.AmbitoRelacionClienteId == _clienteId && c.Estado == EstadoAsignacion.Vigente)
            .ToListAsync();

        vigentes.Should().ContainSingle().Which.UsuarioId.Should().Be(_gestorPropietario);
    }

    // ---------- D3: la doble escritura falla, no se degrada en silencio ----------

    [Fact]
    public async Task Reasignar_a_un_usuario_inexistente_falla_en_vez_de_dejar_la_cartera_sin_escribir()
    {
        await using var contexto = CrearContexto(_propietario);
        var writer = new AsignacionesOperativasWriter(
            contexto, new TenantActualAmbiental { TenantId = _propietario },
            new CurrentUserServiceFalso(_gestorPropietario, Roles.Administrador));

        // Antes registraba un aviso y seguía: el cliente quedaba con la
        // proyección puesta y sin cartera, y su gestor no lo veía — sin ningún
        // error en pantalla — hasta el siguiente reinicio.
        await writer.Invoking(w => w.ReasignarCarteraClienteAsync(_clienteId, Guid.NewGuid()))
            .Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Reasignar_sin_operacion_donde_colgar_la_cartera_falla()
    {
        await using var contexto = CrearContexto(_propietario);

        // El gestor de la consultora sin operación externa vigente: no hay
        // dónde colgar su cartera y el comando no puede fingir que la escribió.
        var writer = new AsignacionesOperativasWriter(
            contexto, new TenantActualAmbiental { TenantId = _propietario },
            new CurrentUserServiceFalso(_gestorPropietario, Roles.Administrador));

        await writer.Invoking(w => w.ReasignarCarteraClienteAsync(_clienteId, _gestorConsultora))
            .Should().ThrowAsync<InvalidOperationException>();
    }

    // ---------- B4: desactivar → reactivar → acceso ----------

    [Fact]
    public async Task El_ciclo_desactivar_reactivar_devuelve_el_acceso_del_operador()
    {
        await EjecutarBackfillAsync();

        // Estado inicial: el gestor delegado ve su cliente.
        (await ClientesVisiblesParaElGestorDelegadoAsync()).Should().BeEquivalentTo([_clienteId]);

        // Desactivar cierra la operación y sus carteras en cascada.
        await using (var contexto = CrearContexto(_propietario))
        {
            var handler = new DesactivarDelegacionTenantCommandHandler(
                new DelegacionTenantRepository(contexto),
                new CurrentUserServiceFalso(tenantOrigenId: _consultora),
                CrearWriter(contexto), contexto);

            var resultado = await handler.Handle(
                new DesactivarDelegacionTenantCommand(_delegacionId), CancellationToken.None);
            resultado.EsExitoso.Should().BeTrue();
        }

        (await ClientesVisiblesParaElGestorDelegadoAsync()).Should().BeEmpty("revocada la delegación no ve nada");

        // Reactivar tiene que devolver operación Y carteras. Antes solo abría
        // la operación: el operador entraba al workspace y no veía un dato.
        await using (var contexto = CrearContexto(_propietario))
        {
            // Reactivar exige ser Administrador del Cliente Delegante, no de la
            // Consultora: restaurar acceso es potestad de quien lo concede.
            // Lo que este test comprueba es otra cosa —que reactivar devuelva
            // también las carteras—, así que entra con la autoridad correcta.
            var handler = new ReactivarDelegacionTenantCommandHandler(
                new DelegacionTenantRepository(contexto),
                new AutorizacionAdministradorDe(_propietario),
                new CurrentUserServiceFalso(Guid.NewGuid()),
                CrearWriter(contexto), contexto);

            var resultado = await handler.Handle(
                new ReactivarDelegacionTenantCommand(_delegacionId), CancellationToken.None);
            resultado.EsExitoso.Should().BeTrue();
        }

        (await ClientesVisiblesParaElGestorDelegadoAsync())
            .Should().BeEquivalentTo([_clienteId], "reactivar devuelve el acceso que tenía");
    }

    // ---------- O1: revalidación al activar una programada ----------

    [Fact]
    public async Task Una_programada_que_choca_con_la_vigente_se_queda_programada_y_no_tumba_el_lote()
    {
        var ahora = DateTime.UtcNow;
        Guid programadaQueChoca;
        Guid programadaLimpia;

        await using (var contexto = CrearContexto(_propietario))
        {
            // Ya hay una raíz vigente del propietario (la creó InitializeAsync).
            // Esta programada apunta al mismo hueco del índice único.
            var choca = AsignacionOperacion.Raiz(
                _propietario, ServicioCae.Outbound, ahora.AddMinutes(-1), ahora.AddMinutes(-2));
            contexto.AsignacionesOperacion.Add(choca);

            var limpia = AsignacionOperacion.Externa(
                _propietario, _consultora, ServicioCae.Outbound, AmbitoAsignacion.Universal,
                ahora.AddMinutes(-1), null, ahora.AddMinutes(-2));
            contexto.AsignacionesOperacion.Add(limpia);

            await contexto.SaveChangesAsync();
            programadaQueChoca = choca.Id;
            programadaLimpia = limpia.Id;

            choca.Estado.Should().Be(EstadoAsignacion.Programada);
            limpia.Estado.Should().Be(EstadoAsignacion.Programada);
        }

        await EjecutarExpiracionAsync();

        await using var verificacion = CrearContexto(_propietario);

        // La que choca se queda como estaba, sin excepción que escape.
        (await verificacion.AsignacionesOperacion.SingleAsync(o => o.Id == programadaQueChoca))
            .Estado.Should().Be(EstadoAsignacion.Programada);

        // Y la que no choca se activa igualmente: un choque no puede tumbar el
        // lote entero y dejar el job reintentando cada hora sin avanzar.
        (await verificacion.AsignacionesOperacion.SingleAsync(o => o.Id == programadaLimpia))
            .Estado.Should().Be(EstadoAsignacion.Vigente);
    }

    // ---------- O2: rol efectivo determinista ----------

    [Fact]
    public async Task El_rol_efectivo_es_estable_cuando_el_usuario_tiene_varias_carteras_en_la_misma_operacion()
    {
        await EjecutarBackfillAsync();

        var ahora = DateTime.UtcNow;
        Guid operacionId;

        await using (var contexto = CrearContexto(_propietario))
        {
            // La operación externa ya existe: la creó el backfill a partir de
            // la delegación comercial. Crear otra chocaría contra el índice de
            // "una sola delegación total vigente".
            var externa = await contexto.AsignacionesOperacion
                .FirstAsync(o => !o.EsRaiz && o.PropietarioTenantId == _propietario);

            // El backfill ya dejó una cartera por cliente para este usuario.
            // Se le añade una universal: dos carteras del MISMO usuario bajo la
            // MISMA operación, que los índices permiten. Con roles distintos,
            // un FirstOrDefault sin orden elegiría uno al azar.
            contexto.AsignacionesCartera.Add(AsignacionCartera.Externa(
                externa, _gestorConsultora, Roles.Consulta, AmbitoAsignacion.Universal, ahora, null, ahora));

            await contexto.SaveChangesAsync();
            operacionId = externa.Id;

            (await contexto.AsignacionesCartera
                    .CountAsync(c => c.AsignacionOperacionId == operacionId && c.UsuarioId == _gestorConsultora))
                .Should().Be(2, "el escenario exige dos carteras del mismo usuario en la misma operación");
        }

        // Diez resoluciones seguidas, cada una con su propio contexto: el
        // resultado tiene que ser siempre el mismo, y el de la cartera
        // universal, que es la que describe el rol en el workspace.
        for (var intento = 0; intento < 10; intento++)
        {
            await using var contexto = CrearContexto(_propietario);
            var rol = await contexto.AsignacionesCartera
                .Where(c => c.AsignacionOperacionId == operacionId
                            && c.UsuarioId == _gestorConsultora
                            && c.Estado == EstadoAsignacion.Vigente)
                .OrderBy(c => c.AmbitoRelacionClienteId == null ? 0 : 1).ThenBy(c => c.Id)
                .Select(c => c.Rol)
                .FirstOrDefaultAsync();

            rol.Should().Be(Roles.Consulta);
        }
    }

    // ---------- utilidades ----------

    private async Task<IReadOnlyList<Guid>?> ClientesVisiblesParaElGestorDelegadoAsync()
    {
        await using var contexto = CrearContexto(_propietario);
        var alcance = new AlcanceDatosService(
            contexto,
            new CurrentUserServiceFalso(_gestorConsultora, Roles.GestorCae, tenantOrigenId: _consultora),
            new TenantActualAmbiental { TenantId = _propietario },
            new SesionPrivilegiadaAusente());

        return await alcance.ObtenerClienteIdsVisiblesAsync();
    }

    private AsignacionesOperativasWriter CrearWriter(CaeManagerDbContext contexto) =>
        new(contexto, new TenantActualAmbiental { TenantId = _propietario },
            new CurrentUserServiceFalso(_gestorConsultora, Roles.Administrador, tenantOrigenId: _consultora));

    private async Task EjecutarBackfillAsync()
    {
        await using var contexto = CrearContexto(_propietario);
        await CaeManager.Infrastructure.Persistence.Seed.AsignacionesOperativasBackfillSeeder
            .SeedAsync(contexto, NullLogger.Instance);
    }

    private async Task EjecutarExpiracionAsync()
    {
        await using var contexto = CrearContexto(_propietario);
        await ExpiracionAsignacionesHostedService.ProcesarParaPruebasAsync(
            contexto, NullLogger.Instance, CancellationToken.None);
    }

    private CaeManagerDbContext CrearContexto(Guid tenantId)
    {
        var tenantActual = new TenantActualAmbiental { TenantId = tenantId };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual), new ConcurrenciaOptimistaInterceptor())
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
    }

    /// <summary>
    /// Doble de <see cref="IAutorizacionDelegacionTenant"/> que responde como un
    /// <c>Administrador</c> del tenant indicado: la implementación real exige rol
    /// y pertenencia, así que solo autoriza cuando le preguntan por su tenant.
    /// </summary>
    private sealed class AutorizacionAdministradorDe(Guid tenant) : IAutorizacionDelegacionTenant
    {
        public Task<bool> PuedeGestionarDelegacionesAsync(
            Guid usuarioId, Guid tenantClienteDeleganteId, CancellationToken cancellationToken = default)
            => Task.FromResult(tenantClienteDeleganteId == tenant);
    }
}
