using CaeManager.Application.Plataforma;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Operaciones;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.Autorizacion;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Configurations;
using CaeManager.Infrastructure.Persistence.Interceptors;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Npgsql;
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

            // La cartera es siempre el Tenant entero (D-7): el alcance de un solo Cliente empresarial lo
            // acota la operación, y la cartera es universal bajo ella.
            var acotadaPropia = AsignacionOperacion.Interna(
                _tenant, ServicioCae.Outbound, AmbitoAsignacion.DeRelacionCliente(_clienteId), ahora, null, ahora);
            var acotadaAjena = AsignacionOperacion.Interna(
                _otroTenant, ServicioCae.Outbound, AmbitoAsignacion.DeRelacionCliente(_clienteDeOtroTenantId), ahora, null, ahora);
            contexto.AsignacionesOperacion.Add(acotadaPropia);
            contexto.AsignacionesOperacion.Add(acotadaAjena);

            contexto.AsignacionesCartera.Add(AsignacionCartera.Interna(
                acotadaPropia, gestorId, AmbitoAsignacion.Universal, ahora, null, ahora));
            contexto.AsignacionesCartera.Add(AsignacionCartera.Interna(
                acotadaAjena, gestorId, AmbitoAsignacion.Universal, ahora, null, ahora));

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
                AmbitoAsignacion.Universal, ahora, null, ahora));
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

    // ── Marca de principal (ADR-011 § 2.7, enmienda 2026-10-08) ───────────

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Dos_carteras_principales_vivas_bajo_la_misma_operacion_son_imposibles(bool laPrimeraSuspendida)
    {
        await using var contexto = CrearContexto(_tenant);
        var operacion = await OperacionExternaAsync(contexto);

        var primera = CarteraDe(operacion, Roles.GestorCae);
        primera.DesignarPrincipal();
        if (laPrimeraSuspendida) primera.Suspender();
        contexto.AsignacionesCartera.Add(primera);
        // Control positivo: una sola principal se guarda.
        await contexto.Invoking(c => c.SaveChangesAsync()).Should().NotThrowAsync();

        var segunda = CarteraDe(operacion, Roles.GestorCae);
        segunda.DesignarPrincipal();
        contexto.AsignacionesCartera.Add(segunda);

        (await contexto.Invoking(c => c.SaveChangesAsync()).Should().ThrowAsync<DbUpdateException>())
            .Which.InnerException.Should().BeOfType<PostgresException>()
            .Which.Should().Match<PostgresException>(e =>
                e.SqlState == PostgresErrorCodes.UniqueViolation
                && e.ConstraintName == AsignacionCarteraConfiguration.IndicePrincipalPorOperacion,
                "suspendida o vigente, la principal viva ocupa el sitio: el índice solo excluye las cerradas");
    }

    [Fact]
    public async Task Una_principal_convive_con_carteras_de_apoyo_y_cada_operacion_tiene_la_suya()
    {
        await using var contexto = CrearContexto(_tenant);
        var operacion = await OperacionExternaAsync(contexto);
        var raiz = AsignacionOperacion.Raiz(_tenant, ServicioCae.Outbound, DateTime.UtcNow, DateTime.UtcNow);
        contexto.AsignacionesOperacion.Add(raiz);

        var principal = CarteraDe(operacion, Roles.GestorCae);
        principal.DesignarPrincipal();
        var principalDeLaRaiz = AsignacionCartera.Interna(
            raiz, Guid.NewGuid(), AmbitoAsignacion.Universal, DateTime.UtcNow, null, DateTime.UtcNow);
        principalDeLaRaiz.DesignarPrincipal();

        contexto.AsignacionesCartera.AddRange(
            principal, CarteraDe(operacion, Roles.GestorCae), CarteraDe(operacion, Roles.GestorCae),
            CarteraDe(operacion, Roles.Consulta), principalDeLaRaiz);

        // El índice es parcial: sin su filtro, dos carteras cualesquiera bajo la misma operación chocarían.
        await contexto.Invoking(c => c.SaveChangesAsync()).Should().NotThrowAsync();
        (await contexto.AsignacionesCartera.CountAsync(c => c.EsPrincipal)).Should().Be(2, "una por operación");
    }

    [Fact]
    public async Task Cerrada_la_principal_otra_cartera_de_la_misma_operacion_puede_serlo()
    {
        await using var contexto = CrearContexto(_tenant);
        var operacion = await OperacionExternaAsync(contexto);

        var saliente = CarteraDe(operacion, Roles.GestorCae);
        saliente.DesignarPrincipal();
        contexto.AsignacionesCartera.Add(saliente);
        await contexto.SaveChangesAsync();

        // El índice no es diferible: primero se apaga (aquí, cerrando) y se guarda; después se enciende.
        saliente.Cerrar(MotivoCierreAsignacion.RetiradaPorElOperador, DateTime.UtcNow);
        await contexto.SaveChangesAsync();

        var entrante = CarteraDe(operacion, Roles.CoordinadorCae);
        entrante.DesignarPrincipal();
        contexto.AsignacionesCartera.Add(entrante);

        await contexto.Invoking(c => c.SaveChangesAsync()).Should().NotThrowAsync();
    }

    [Fact]
    public async Task Una_fila_cerrada_que_conservara_la_marca_no_ocupa_el_sitio_del_principal()
    {
        // El dominio apaga la marca al cerrar; el índice no depende de ello. Se fuerza la fila que el dominio
        // no produce para ver que el filtro excluye las cerradas por sí mismo.
        await using var contexto = CrearContexto(_tenant);
        var operacion = await OperacionExternaAsync(contexto);

        var cerrada = CarteraDe(operacion, Roles.GestorCae);
        cerrada.Cerrar(MotivoCierreAsignacion.RetiradaPorElOperador, DateTime.UtcNow);
        ForzarMarca(cerrada);
        var viva = CarteraDe(operacion, Roles.GestorCae);
        viva.DesignarPrincipal();
        contexto.AsignacionesCartera.AddRange(cerrada, viva);

        await contexto.Invoking(c => c.SaveChangesAsync()).Should().NotThrowAsync();
    }

    [Theory]
    [InlineData(Roles.Consulta)]
    [InlineData(Roles.Administrador)]
    [InlineData(Roles.DireccionCae)]
    public async Task Una_cartera_principal_de_un_rol_que_no_es_Gestor_CAE_ni_Coordinador_CAE_la_rechaza_la_base(string rol)
    {
        await using var contexto = CrearContexto(_tenant);
        var operacion = await OperacionExternaAsync(contexto);

        // Control positivo: la misma cartera, sin la marca, se guarda.
        contexto.AsignacionesCartera.Add(CarteraDe(operacion, rol));
        await contexto.Invoking(c => c.SaveChangesAsync()).Should().NotThrowAsync();

        // La guarda de dominio lo impide; se salta para llegar al CHECK, que es lo que se prueba aquí.
        var marcada = CarteraDe(operacion, rol);
        ForzarMarca(marcada);
        contexto.AsignacionesCartera.Add(marcada);

        (await contexto.Invoking(c => c.SaveChangesAsync()).Should().ThrowAsync<DbUpdateException>())
            .Which.InnerException.Should().BeOfType<PostgresException>()
            .Which.Should().Match<PostgresException>(e =>
                e.SqlState == PostgresErrorCodes.CheckViolation
                && e.ConstraintName == AsignacionCarteraConfiguration.RestriccionPrincipal);
    }

    [Fact]
    public async Task Una_cartera_principal_que_no_es_del_Tenant_entero_la_rechaza_la_base()
    {
        await using var contexto = CrearContexto(_tenant);
        var operacion = await OperacionExternaAsync(contexto);

        // Solo cabe como histórico cerrado (CK_AsignacionesCartera_TenantEnteroSalvoCerrada), y ni así lleva marca.
        var porCliente = CarteraDe(operacion, Roles.GestorCae);
        porCliente.Cerrar(MotivoCierreAsignacion.Reorganizada, DateTime.UtcNow);
        typeof(AsignacionResponsabilidad).GetProperty(nameof(AsignacionResponsabilidad.AmbitoRelacionClienteId))!
            .SetValue(porCliente, _clienteId);
        ForzarMarca(porCliente);
        contexto.AsignacionesCartera.Add(porCliente);

        (await contexto.Invoking(c => c.SaveChangesAsync()).Should().ThrowAsync<DbUpdateException>())
            .Which.InnerException.Should().BeOfType<PostgresException>()
            .Which.ConstraintName.Should().Be(AsignacionCarteraConfiguration.RestriccionPrincipal);
    }

    [Fact]
    public async Task Era_principal_al_cerrarse_por_cascada_solo_cabe_en_una_cartera_cerrada()
    {
        await using var contexto = CrearContexto(_tenant);
        var operacion = await OperacionExternaAsync(contexto);

        // Control positivo: la cartera principal que cierra la cascada guarda el dato, y sigue sin ser principal.
        var cerrada = CarteraDe(operacion, Roles.GestorCae);
        cerrada.DesignarPrincipal();
        cerrada.CerrarPorCascadaDeLaOperacion(MotivoCierreAsignacion.Revocada, DateTime.UtcNow);
        contexto.AsignacionesCartera.Add(cerrada);
        await contexto.Invoking(c => c.SaveChangesAsync()).Should().NotThrowAsync();
        (await contexto.AsignacionesCartera.AsNoTracking().SingleAsync(c => c.Id == cerrada.Id))
            .Should().Match<AsignacionCartera>(c => c.EraPrincipalAlCerrarsePorCascada && !c.EsPrincipal);

        // El dominio solo lo escribe al cerrar; se fuerza sobre una cartera viva para llegar al CHECK.
        var viva = CarteraDe(operacion, Roles.GestorCae);
        typeof(AsignacionCartera).GetProperty(nameof(AsignacionCartera.EraPrincipalAlCerrarsePorCascada))!.SetValue(viva, true);
        contexto.AsignacionesCartera.Add(viva);

        (await contexto.Invoking(c => c.SaveChangesAsync()).Should().ThrowAsync<DbUpdateException>())
            .Which.InnerException.Should().BeOfType<PostgresException>()
            .Which.Should().Match<PostgresException>(e =>
                e.SqlState == PostgresErrorCodes.CheckViolation
                && e.ConstraintName == AsignacionCarteraConfiguration.RestriccionEraPrincipal);
    }

    [Fact]
    public void Los_roles_que_el_dominio_deja_marcar_son_los_de_Identity_y_los_del_CHECK()
    {
        // Domain no referencia los roles de Identity y los repite como texto: aquí se atan.
        AsignacionCartera.RolesQuePuedenSerPrincipal.Should().BeEquivalentTo([Roles.GestorCae, Roles.CoordinadorCae]);
    }

    private async Task<AsignacionOperacion> OperacionExternaAsync(CaeManagerDbContext contexto)
    {
        var ahora = DateTime.UtcNow;
        var operacion = AsignacionOperacion.Externa(
            _tenant, _otroTenant, ServicioCae.Outbound, AmbitoAsignacion.Universal, ahora.AddDays(-1), null, ahora);
        contexto.AsignacionesOperacion.Add(operacion);
        await contexto.SaveChangesAsync();
        return operacion;
    }

    private static AsignacionCartera CarteraDe(AsignacionOperacion operacion, string rol) =>
        AsignacionCartera.Externa(
            operacion, Guid.NewGuid(), rol, AmbitoAsignacion.Universal, DateTime.UtcNow.AddDays(-1), null, DateTime.UtcNow);

    /// <summary>Enciende la marca sin pasar por <c>DesignarPrincipal</c>: solo para llegar a las garantías de la base.</summary>
    private static void ForzarMarca(AsignacionCartera cartera) =>
        typeof(AsignacionCartera).GetProperty(nameof(AsignacionCartera.EsPrincipal))!.SetValue(cartera, true);

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
