using CaeManager.Domain.Empresas;
using CaeManager.Domain.Operaciones;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Seed;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CaeManager.IntegrationTests.Operaciones;

/// <summary>
/// El backfill de F1: traslada el reparto que hoy vive en
/// <c>DelegacionTenant</c> y <c>Cliente.EjecutivoUsuarioId</c> a las tablas de
/// asignación, sin romper nada de lo anterior.
///
/// Lo que se fija aquí no es solo que copie, sino sus tres reglas duras: es
/// <b>reconciliador</b> (no solo insert-if-missing), <b>no migra el soporte</b>
/// y <b>no confía</b> en que los datos legados cumplan el invariante
/// usuario↔operador.
/// </summary>
public class BackfillAsignacionesOperativasTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _operadorDelegado = Guid.NewGuid();
    private readonly Guid _gestorInterno = Guid.NewGuid();
    private Guid _consultora;
    private Guid _clienteDelegante;
    private Guid _clienteConEjecutivoId;

    public async Task InitializeAsync()
    {
        await using var contextoInicial = CrearContexto(Guid.NewGuid());
        await contextoInicial.Database.MigrateAsync();

        var consultora = new Tenant("Consultora", PerfilVocabularioTenant.Consultora);
        var clienteDelegante = new Tenant("Cliente Delegante", PerfilVocabularioTenant.ClienteDirecto);
        contextoInicial.Tenants.Add(consultora);
        contextoInicial.Tenants.Add(clienteDelegante);
        await contextoInicial.SaveChangesAsync();

        _consultora = consultora.Id;
        _clienteDelegante = clienteDelegante.Id;

        await using var contexto = CrearContexto(_clienteDelegante);

        contexto.Users.Add(new ApplicationUser
        {
            Id = _operadorDelegado,
            TenantId = _consultora,
            UserName = "operador@consultora",
            Email = "operador@consultora"
        });
        contexto.Users.Add(new ApplicationUser
        {
            Id = _gestorInterno,
            TenantId = _clienteDelegante,
            UserName = "gestor@cliente",
            Email = "gestor@cliente"
        });
        await contexto.SaveChangesAsync();

        var delegacion = new DelegacionTenant(_consultora, _clienteDelegante);
        contexto.DelegacionesTenant.Add(delegacion);
        contexto.AsignacionesOperadorDelegado.Add(
            new AsignacionOperadorDelegado(delegacion.Id, _operadorDelegado, Roles.GestorCae));

        // Delegación de soporte: NO debe migrar. El soporte de TALVEG no es un
        // operador CAE (ADR-011 § 8) y convertirla en operación sería
        // exactamente la falsa delegación que ese plano prohíbe.
        contexto.DelegacionesTenant.Add(DelegacionTenant.ParaSoporte(_consultora, _clienteDelegante));

        var cliente = Empresa.CrearComoCliente("Cliente con ejecutivo", "B12345674", false, null, _gestorInterno);
        contexto.Empresas.Add(cliente);
        await contexto.SaveChangesAsync();

        _clienteConEjecutivoId = cliente.Id;
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Fact]
    public async Task Migra_raices_delegaciones_comerciales_y_carteras_pero_no_el_soporte()
    {
        await EjecutarBackfillAsync();

        await using var contexto = CrearContexto(_clienteDelegante);

        var operaciones = await contexto.AsignacionesOperacion.ToListAsync();

        // Una raíz por tenant: es el ancla de las carteras internas. Se
        // comprueba sobre los tenants del test y no por conteo absoluto —
        // la migración siembra además el tenant de plataforma, que también
        // recibe la suya, y eso es correcto.
        operaciones.Where(o => o.EsRaiz).Select(o => o.PropietarioTenantId)
            .Should().Contain([_consultora, _clienteDelegante])
            .And.OnlyHaveUniqueItems();

        // Una sola operación externa: la comercial. La de soporte no migra,
        // aunque exista y esté sobre el mismo par de tenants.
        var externas = operaciones.Where(o => !o.EsRaiz).ToList();
        externas.Should().HaveCount(1);
        externas[0].PropietarioTenantId.Should().Be(_clienteDelegante);
        externas[0].OperadorTenantId.Should().Be(_consultora);
        externas[0].Ambito.EsUniversal.Should().BeTrue();
        externas[0].Estado.Should().Be(EstadoAsignacion.Vigente);

        var carteras = await contexto.AsignacionesCartera.ToListAsync();

        // El operador delegado tiene rol de cartera (GestorCae) y no es
        // ejecutivo de ningún cliente, así que NO recibe cartera: darle una
        // universal le entregaría todos los clientes del tenant delegado, más
        // de lo que tiene hoy. Sus carteras nacerán cliente a cliente.
        carteras.Should().NotContain(c => c.UsuarioId == _operadorDelegado);

        // La del ejecutivo interno: sobre la raíz de su tenant y acotada al
        // cliente concreto, que es lo que EjecutivoUsuarioId significaba.
        var interna = carteras.Single(c => c.UsuarioId == _gestorInterno);
        interna.AmbitoRelacionClienteId.Should().Be(_clienteConEjecutivoId);
        interna.Rol.Should().BeNull();
        operaciones.Single(o => o.Id == interna.AsignacionOperacionId).EsRaiz.Should().BeTrue();
    }

    [Fact]
    public async Task Es_idempotente_y_no_duplica_al_repetirse()
    {
        await EjecutarBackfillAsync();

        await using var contextoPrimero = CrearContexto(_clienteDelegante);
        var operacionesTrasElPrimero = await contextoPrimero.AsignacionesOperacion.CountAsync();
        var carterasTrasElPrimero = await contextoPrimero.AsignacionesCartera.CountAsync();

        await EjecutarBackfillAsync();
        await EjecutarBackfillAsync();

        await using var contexto = CrearContexto(_clienteDelegante);

        // Repetirlo no añade ni cierra nada: es lo que permite dejarlo puesto
        // en cada arranque hasta que la doble escritura quede establecida.
        (await contexto.AsignacionesOperacion.CountAsync()).Should().Be(operacionesTrasElPrimero);
        (await contexto.AsignacionesCartera.CountAsync()).Should().Be(carterasTrasElPrimero);
        (await contexto.AsignacionesCartera.CountAsync(c => c.Estado == EstadoAsignacion.Cerrada)).Should().Be(0);
    }

    [Fact]
    public async Task Reconcilia_un_cambio_de_ejecutivo_ocurrido_fuera_de_la_doble_escritura()
    {
        await EjecutarBackfillAsync();

        // Simula la ventana real entre el backfill y la activación de la doble
        // escritura, con despliegue rolling: alguien reasigna el cliente por la
        // vía antigua y las tablas nuevas se quedan atrás. Un seeder que solo
        // insertara dejaría esa diferencia congelada para siempre.
        var nuevoGestor = Guid.NewGuid();
        await using (var contextoCambio = CrearContexto(_clienteDelegante))
        {
            contextoCambio.Users.Add(new ApplicationUser
            {
                Id = nuevoGestor,
                TenantId = _clienteDelegante,
                UserName = "nuevo@cliente",
                Email = "nuevo@cliente"
            });
            var cliente = await contextoCambio.Empresas.FirstAsync(c => c.Id == _clienteConEjecutivoId);
            cliente.AsignarEjecutivo(nuevoGestor);
            await contextoCambio.SaveChangesAsync();
        }

        await EjecutarBackfillAsync();

        await using var contexto = CrearContexto(_clienteDelegante);
        var carterasDelCliente = await contexto.AsignacionesCartera
            .Where(c => c.AmbitoRelacionClienteId == _clienteConEjecutivoId)
            .ToListAsync();

        // Append-only: la anterior se cierra, no se edita.
        carterasDelCliente.Should().HaveCount(2);
        carterasDelCliente.Single(c => c.UsuarioId == _gestorInterno).Estado.Should().Be(EstadoAsignacion.Cerrada);
        carterasDelCliente.Single(c => c.UsuarioId == nuevoGestor).Estado.Should().Be(EstadoAsignacion.Vigente);
    }

    [Fact]
    public async Task Cierra_la_cartera_de_un_cliente_al_que_le_quitaron_el_ejecutivo()
    {
        await EjecutarBackfillAsync();

        await using (var contextoCambio = CrearContexto(_clienteDelegante))
        {
            var cliente = await contextoCambio.Empresas.FirstAsync(c => c.Id == _clienteConEjecutivoId);
            cliente.AsignarEjecutivo(null);
            await contextoCambio.SaveChangesAsync();
        }

        await EjecutarBackfillAsync();

        await using var contexto = CrearContexto(_clienteDelegante);
        var carteras = await contexto.AsignacionesCartera
            .Where(c => c.AmbitoRelacionClienteId == _clienteConEjecutivoId)
            .ToListAsync();

        carteras.Should().OnlyContain(c => c.Estado == EstadoAsignacion.Cerrada);
    }

    [Fact]
    public async Task No_migra_un_operador_delegado_cuyo_usuario_no_pertenece_a_la_consultora()
    {
        // Este caso EXISTE en datos reales: el comando de alta actual admite
        // usuarios del tenant propietario, no solo de la consultora. Migrarlo a
        // ciegas rompería la cadena "el usuario pertenece al tenant operador" y
        // le devolvería su rol de origen dentro del workspace ajeno.
        await using (var contextoPreparacion = CrearContexto(_clienteDelegante))
        {
            var delegacion = await contextoPreparacion.DelegacionesTenant
                .FirstAsync(d => d.Proposito == PropositoDelegacion.Comercial);

            contextoPreparacion.AsignacionesOperadorDelegado.Add(
                new AsignacionOperadorDelegado(delegacion.Id, _gestorInterno, Roles.GestorCae));
            await contextoPreparacion.SaveChangesAsync();
        }

        await EjecutarBackfillAsync();

        await using var contexto = CrearContexto(_clienteDelegante);
        var externa = await contexto.AsignacionesOperacion.FirstAsync(o => !o.EsRaiz);

        // El gestor interno no recibe cartera externa: su acceso sigue
        // gobernado por su cartera interna, que sí se migró.
        (await contexto.AsignacionesCartera
                .AnyAsync(c => c.AsignacionOperacionId == externa.Id && c.UsuarioId == _gestorInterno))
            .Should().BeFalse();

        (await contexto.AsignacionesCartera
                .AnyAsync(c => c.UsuarioId == _gestorInterno && c.AmbitoRelacionClienteId == _clienteConEjecutivoId))
            .Should().BeTrue();
    }

    [Fact]
    public async Task Dos_delegaciones_comerciales_activas_del_mismo_cliente_no_crashean_la_mas_nueva_queda_como_incidencia()
    {
        // Reproduce el incidente de producción del 2026-08-24 (ver
        // Project-Hydra-Negocio/tecnico/d8-vps-evidence.md): el modelo antiguo
        // nunca impidió que un cliente tuviera dos delegaciones comerciales
        // activas simultáneas hacia operadores distintos; el índice único
        // IX_AsignacionesOperacion_DelegacionTotalVigente sí lo impide, y sin
        // este chequeo el backfill intentaba crear las dos y crasheaba con
        // 23505 en el arranque de la aplicación real.
        Guid segundaConsultora;
        await using (var contextoPreparacion = CrearContexto(_clienteDelegante))
        {
            var consultora2 = new Tenant("Segunda consultora", PerfilVocabularioTenant.Consultora);
            contextoPreparacion.Tenants.Add(consultora2);
            await contextoPreparacion.SaveChangesAsync();
            segundaConsultora = consultora2.Id;

            // Más nueva que la delegación creada en InitializeAsync — el orden
            // determinista del backfill exige que sea la que quede como
            // incidencia, nunca la que se migre.
            contextoPreparacion.DelegacionesTenant.Add(new DelegacionTenant(segundaConsultora, _clienteDelegante));
            await contextoPreparacion.SaveChangesAsync();
        }

        var ejecutar = async () => await EjecutarBackfillAsync();
        await ejecutar.Should().NotThrowAsync(
            "un cliente con dos delegaciones activas es un dato del modelo antiguo que hay que reportar, no una " +
            "razón para que el arranque real de la aplicación crashee");

        await using var contexto = CrearContexto(_clienteDelegante);
        var externas = await contexto.AsignacionesOperacion
            .Where(o => !o.EsRaiz && o.PropietarioTenantId == _clienteDelegante)
            .ToListAsync();

        // Solo la más antigua (la de InitializeAsync, hacia _consultora) se
        // migra; la de segundaConsultora no debe existir como operación.
        externas.Should().ContainSingle();
        externas[0].OperadorTenantId.Should().Be(_consultora);
        externas.Should().NotContain(o => o.OperadorTenantId == segundaConsultora);
    }

    private async Task EjecutarBackfillAsync()
    {
        await using var contexto = CrearContexto(_clienteDelegante);
        await AsignacionesOperativasBackfillSeeder.SeedAsync(contexto, NullLogger.Instance);
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
