using CaeManager.Application.Plataforma;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Operaciones;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.Autorizacion;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Interceptors;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests.Operaciones;

/// <summary>
/// Las garantías que el esquema tiene que dar por sí mismo, sin depender de
/// que ningún comando se acuerde de comprobarlas: unicidad de responsable,
/// imposibilidad de apuntar a datos de otro tenant, y concurrencia optimista
/// real (no un token inerte).
/// </summary>
public class EsquemaAsignacionesOperativasTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private Guid _tenant;
    private Guid _otroTenant;
    private Guid _clienteId;
    private Guid _clienteDeOtroTenantId;

    public async Task InitializeAsync()
    {
        await using var contextoInicial = CrearContexto(Guid.NewGuid());
        await contextoInicial.Database.MigrateAsync();

        var propio = new Tenant("Propio", PerfilVocabularioTenant.ClienteDirecto);
        var ajeno = new Tenant("Ajeno", PerfilVocabularioTenant.ClienteDirecto);
        contextoInicial.Tenants.Add(propio);
        contextoInicial.Tenants.Add(ajeno);
        await contextoInicial.SaveChangesAsync();

        _tenant = propio.Id;
        _otroTenant = ajeno.Id;

        await using (var contexto = CrearContexto(_tenant))
        {
            var cliente = Empresa.CrearComoCliente("Cliente propio", "B12345674", false, null, null);
            contexto.Empresas.Add(cliente);
            await contexto.SaveChangesAsync();
            _clienteId = cliente.Id;
        }

        await using (var contextoAjeno = CrearContexto(_otroTenant))
        {
            var cliente = Empresa.CrearComoCliente("Cliente ajeno", "B58818501", false, null, null);
            contextoAjeno.Empresas.Add(cliente);
            await contextoAjeno.SaveChangesAsync();
            _clienteDeOtroTenantId = cliente.Id;
        }
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Fact]
    public async Task Dos_raices_vigentes_del_mismo_tenant_y_servicio_son_imposibles()
    {
        await using var contexto = CrearContexto(_tenant);
        var ahora = DateTime.UtcNow;

        contexto.AsignacionesOperacion.Add(
            AsignacionOperacion.Raiz(_tenant, ServicioCae.Outbound, ahora, ahora));
        await contexto.SaveChangesAsync();

        contexto.AsignacionesOperacion.Add(
            AsignacionOperacion.Raiz(_tenant, ServicioCae.Outbound, ahora, ahora));

        // El índice único parcial es el backstop: dos comandos concurrentes
        // pasan la validación de aplicación a la vez, y aquí es donde uno cae.
        await contexto.Invoking(c => c.SaveChangesAsync())
            .Should().ThrowAsync<DbUpdateException>();
    }

    [Fact]
    public async Task Dos_delegaciones_totales_vigentes_sobre_el_mismo_tenant_son_imposibles()
    {
        await using var contexto = CrearContexto(_tenant);
        var ahora = DateTime.UtcNow;

        contexto.AsignacionesOperacion.Add(AsignacionOperacion.Externa(
            _tenant, _otroTenant, ServicioCae.Outbound, AmbitoAsignacion.Universal, ahora, null, ahora));
        await contexto.SaveChangesAsync();

        // Otro operador distinto, mismo "todo": repartir exige ámbitos
        // explícitos, no dos "todo" simultáneos.
        contexto.AsignacionesOperacion.Add(AsignacionOperacion.Externa(
            _tenant, Guid.NewGuid(), ServicioCae.Outbound, AmbitoAsignacion.Universal, ahora, null, ahora));

        await contexto.Invoking(c => c.SaveChangesAsync())
            .Should().ThrowAsync<DbUpdateException>();
    }

    [Fact]
    public async Task Una_delegacion_total_convive_con_la_raiz_del_mismo_tenant()
    {
        // La raíz es el fallback del propietario, no una competidora: si
        // participara en la unicidad, delegar todo a una consultora —el caso
        // más común del negocio— sería ilegal.
        await using var contexto = CrearContexto(_tenant);
        var ahora = DateTime.UtcNow;

        contexto.AsignacionesOperacion.Add(
            AsignacionOperacion.Raiz(_tenant, ServicioCae.Outbound, ahora, ahora));
        contexto.AsignacionesOperacion.Add(AsignacionOperacion.Externa(
            _tenant, _otroTenant, ServicioCae.Outbound, AmbitoAsignacion.Universal, ahora, null, ahora));

        await contexto.Invoking(c => c.SaveChangesAsync()).Should().NotThrowAsync();
    }

    [Fact]
    public async Task Una_cerrada_deja_sitio_a_su_sustituta()
    {
        await using var contexto = CrearContexto(_tenant);
        var ahora = DateTime.UtcNow;

        var primera = AsignacionOperacion.Externa(
            _tenant, _otroTenant, ServicioCae.Outbound, AmbitoAsignacion.Universal, ahora.AddDays(-1), null, ahora);
        contexto.AsignacionesOperacion.Add(primera);
        await contexto.SaveChangesAsync();

        primera.Cerrar(MotivoCierreAsignacion.Transferida, ahora);
        contexto.AsignacionesOperacion.Add(AsignacionOperacion.Externa(
            _tenant, Guid.NewGuid(), ServicioCae.Outbound, AmbitoAsignacion.Universal, ahora, null, ahora));

        // El cambio de proveedor: cerrar la saliente y abrir la entrante. El
        // índice filtra por Estado, así que la cerrada ya no ocupa sitio.
        await contexto.Invoking(c => c.SaveChangesAsync()).Should().NotThrowAsync();
    }

    [Fact]
    public async Task Un_ambito_no_puede_apuntar_a_un_cliente_de_otro_tenant()
    {
        // La fuga cross-tenant más peligrosa de este diseño, cerrada en el
        // esquema por la FK compuesta (PropietarioTenantId, AmbitoXxxId) contra
        // la clave alternativa (TenantId, Id) del agregado — no en una
        // comprobación que alguien pueda olvidar.
        await using var contexto = CrearContexto(_tenant);
        var ahora = DateTime.UtcNow;

        contexto.AsignacionesOperacion.Add(AsignacionOperacion.Externa(
            _tenant, _otroTenant, ServicioCae.Outbound,
            AmbitoAsignacion.DeRelacionCliente(_clienteDeOtroTenantId), ahora, null, ahora));

        await contexto.Invoking(c => c.SaveChangesAsync())
            .Should().ThrowAsync<DbUpdateException>();
    }

    [Fact]
    public async Task Un_ambito_sobre_un_cliente_del_propio_tenant_se_acepta()
    {
        await using var contexto = CrearContexto(_tenant);
        var ahora = DateTime.UtcNow;

        contexto.AsignacionesOperacion.Add(AsignacionOperacion.Externa(
            _tenant, _otroTenant, ServicioCae.Outbound,
            AmbitoAsignacion.DeRelacionCliente(_clienteId), ahora, null, ahora));

        await contexto.Invoking(c => c.SaveChangesAsync()).Should().NotThrowAsync();
    }

    [Fact]
    public async Task El_token_de_concurrencia_de_las_asignaciones_no_es_inerte()
    {
        // La columna Version existe en tablas que NO heredan de EntidadBase, y
        // tanto el marcado del modelo como la renovación del valor iban por esa
        // clase base. Sin extenderlos a IVersionable, la columna estaría ahí,
        // nunca cambiaría, y el WHERE del UPDATE compararía siempre contra lo
        // mismo: cero protección, en silencio.
        var ahora = DateTime.UtcNow;
        Guid operacionId;

        await using (var contexto = CrearContexto(_tenant))
        {
            var operacion = AsignacionOperacion.Externa(
                _tenant, _otroTenant, ServicioCae.Outbound, AmbitoAsignacion.Universal, ahora, null, ahora);
            contexto.AsignacionesOperacion.Add(operacion);
            await contexto.SaveChangesAsync();
            operacionId = operacion.Id;
        }

        await using var primero = CrearContexto(_tenant);
        await using var segundo = CrearContexto(_tenant);

        var desdePrimero = await primero.AsignacionesOperacion.FirstAsync(o => o.Id == operacionId);
        var desdeSegundo = await segundo.AsignacionesOperacion.FirstAsync(o => o.Id == operacionId);

        desdePrimero.Suspender();
        await primero.SaveChangesAsync();

        desdeSegundo.Cerrar(MotivoCierreAsignacion.Revocada, ahora);

        await segundo.Invoking(c => c.SaveChangesAsync())
            .Should().ThrowAsync<DbUpdateConcurrencyException>();
    }

    [Fact]
    public async Task El_alcance_de_un_gestor_sale_de_su_cartera_y_no_cruza_de_tenant()
    {
        // Un usuario puede tener carteras en varios tenants (el suyo y los que
        // opera por delegación). Sin filtrar por el tenant activo, los clientes
        // de un workspace se colarían en otro.
        var gestorId = Guid.NewGuid();
        var ahora = DateTime.UtcNow;

        await using (var contexto = CrearContexto(_tenant))
        {
            contexto.Users.Add(new ApplicationUser
            {
                Id = gestorId,
                TenantId = _tenant,
                UserName = "gestor@propio",
                Email = "gestor@propio"
            });

            var raizPropia = AsignacionOperacion.Raiz(_tenant, ServicioCae.Outbound, ahora, ahora);
            var raizAjena = AsignacionOperacion.Raiz(_otroTenant, ServicioCae.Outbound, ahora, ahora);
            contexto.AsignacionesOperacion.Add(raizPropia);
            contexto.AsignacionesOperacion.Add(raizAjena);

            contexto.AsignacionesCartera.Add(AsignacionCartera.Interna(
                raizPropia, gestorId, AmbitoAsignacion.DeRelacionCliente(_clienteId), ahora, null, ahora));
            contexto.AsignacionesCartera.Add(AsignacionCartera.Interna(
                raizAjena, gestorId, AmbitoAsignacion.DeRelacionCliente(_clienteDeOtroTenantId), ahora, null, ahora));

            await contexto.SaveChangesAsync();
        }

        await using var contextoAlcance = CrearContexto(_tenant);
        var alcance = new AlcanceDatosService(
            contextoAlcance,
            new CurrentUserServiceFalso(gestorId, Roles.GestorCae, tenantOrigenId: _tenant),
            new TenantActualAmbiental { TenantId = _tenant },
            new SesionPrivilegiadaAusente());

        var visibles = await alcance.ObtenerClienteIdsVisiblesAsync();

        visibles.Should().BeEquivalentTo([_clienteId]);
    }

    [Fact]
    public async Task Una_cartera_bajo_una_operacion_cerrada_no_concede_alcance()
    {
        var gestorId = Guid.NewGuid();
        var ahora = DateTime.UtcNow;

        await using (var contexto = CrearContexto(_tenant))
        {
            contexto.Users.Add(new ApplicationUser
            {
                Id = gestorId,
                TenantId = _tenant,
                UserName = "gestor2@propio",
                Email = "gestor2@propio"
            });

            var externa = AsignacionOperacion.Externa(
                _tenant, _otroTenant, ServicioCae.Outbound, AmbitoAsignacion.Universal, ahora, null, ahora);
            contexto.AsignacionesOperacion.Add(externa);
            contexto.AsignacionesCartera.Add(AsignacionCartera.Externa(
                externa, gestorId, Roles.GestorCae,
                AmbitoAsignacion.DeRelacionCliente(_clienteId), ahora, null, ahora));
            await contexto.SaveChangesAsync();

            // Se cierra la operación dejando la cartera abierta a propósito:
            // reproduce el instante en que la operación caduca por fecha y el
            // cierre en cascada todavía no ha corrido.
            externa.Cerrar(MotivoCierreAsignacion.Revocada, ahora);
            await contexto.SaveChangesAsync();
        }

        await using var contextoAlcance = CrearContexto(_tenant);
        var alcance = new AlcanceDatosService(
            contextoAlcance,
            new CurrentUserServiceFalso(gestorId, Roles.GestorCae, tenantOrigenId: _tenant),
            new TenantActualAmbiental { TenantId = _tenant },
            new SesionPrivilegiadaAusente());

        (await alcance.ObtenerClienteIdsVisiblesAsync()).Should().BeEmpty();
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
}
