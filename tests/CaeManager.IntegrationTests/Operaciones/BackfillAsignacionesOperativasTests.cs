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
/// El backfill de F1: traslada el reparto que vive en <c>DelegacionTenant</c> a
/// las tablas de asignación, sin romper nada de lo anterior. Desde D-7
/// (2026-10-02) ya no deriva carteras de <c>Empresa.EjecutivoUsuarioId</c>: la
/// referencia de un Cliente empresarial no concede alcance.
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
        contexto.AsignacionesOperadorDelegadoConRevocadas.Add(
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

        // El operador delegado tiene rol de cartera (GestorCae), así que NO
        // recibe cartera del backfill: darle una universal le entregaría todo
        // el tenant delegado sin que nadie lo decidiera. Su cartera nace de un
        // acto explícito (D-7).
        carteras.Should().NotContain(c => c.UsuarioId == _operadorDelegado);

        // La referencia (Empresa.EjecutivoUsuarioId) de _clienteConEjecutivoId
        // es _gestorInterno, y no le concede nada: antes el backfill le abría
        // una cartera por Cliente empresarial sobre la raíz de su tenant.
        carteras.Should().NotContain(c => c.UsuarioId == _gestorInterno,
            "ser la referencia de un Cliente no concede cartera (D-7)");
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

    /// <summary>
    /// D-7: cambiar o quitar la referencia de un Cliente empresarial por la vía que sea no abre ni cierra
    /// ninguna Asignación de Cartera. Antes el backfill reconciliaba en cada arranque las carteras por
    /// Cliente empresarial contra <c>Empresa.EjecutivoUsuarioId</c>.
    /// </summary>
    [Fact]
    public async Task Cambiar_o_quitar_la_referencia_de_un_Cliente_no_crea_ni_cierra_ninguna_cartera()
    {
        await EjecutarBackfillAsync();
        await using var antes = CrearContexto(_clienteDelegante);
        var carterasAntes = await antes.AsignacionesCartera.Select(c => new { c.Id, c.Estado }).ToListAsync();

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

        await using (var contextoQuitar = CrearContexto(_clienteDelegante))
        {
            (await contextoQuitar.Empresas.FirstAsync(c => c.Id == _clienteConEjecutivoId)).AsignarEjecutivo(null);
            await contextoQuitar.SaveChangesAsync();
        }

        await EjecutarBackfillAsync();

        await using var contexto = CrearContexto(_clienteDelegante);
        (await contexto.AsignacionesCartera.Select(c => new { c.Id, c.Estado }).ToListAsync())
            .Should().BeEquivalentTo(carterasAntes, "la referencia no concede ni retira alcance");
        (await contexto.AsignacionesCartera.AnyAsync(c => c.UsuarioId == nuevoGestor)).Should().BeFalse();
        (await contexto.AsignacionesCartera.AnyAsync(c => c.AmbitoRelacionClienteId != null)).Should().BeFalse();
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
                .FirstAsync(d => d.Proposito == PropositoDelegacion.OperadorExterno);

            contextoPreparacion.AsignacionesOperadorDelegadoConRevocadas.Add(
                new AsignacionOperadorDelegado(delegacion.Id, _gestorInterno, Roles.GestorCae));
            await contextoPreparacion.SaveChangesAsync();
        }

        await EjecutarBackfillAsync();

        await using var contexto = CrearContexto(_clienteDelegante);
        var externa = await contexto.AsignacionesOperacion.FirstAsync(o => !o.EsRaiz);

        // El gestor interno no recibe cartera externa, ni ninguna otra: ser la
        // referencia de un Cliente empresarial no concede cartera (D-7).
        (await contexto.AsignacionesCartera
                .AnyAsync(c => c.AsignacionOperacionId == externa.Id && c.UsuarioId == _gestorInterno))
            .Should().BeFalse();

        (await contexto.AsignacionesCartera.AnyAsync(c => c.UsuarioId == _gestorInterno)).Should().BeFalse();
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

    /// <summary>
    /// Decisión del propietario, 2026-09-23: una cartera externa solo concede
    /// Coordinador CAE, Gestor CAE o Consulta. El backfill ni emite una cartera
    /// con Administrador o Dirección CAE desde una delegación heredada, ni deja
    /// vigente la que ya existiera de antes (reconciliación, no solo alta).
    /// </summary>
    [Theory]
    [InlineData(Roles.Administrador)]
    [InlineData(Roles.DireccionCae)]
    public async Task No_emite_y_cierra_carteras_externas_con_un_rol_de_Propiedad(string rol)
    {
        var usuario = await SembrarOperadorDelegadoAsync(rol);

        await EjecutarBackfillAsync();

        Guid heredadaId;
        await using (var contextoPreparacion = CrearContexto(_clienteDelegante))
        {
            (await contextoPreparacion.AsignacionesCartera.AnyAsync(c => c.UsuarioId == usuario))
                .Should().BeFalse("el backfill no emite una cartera externa con un rol de Propiedad");

            // Estado anterior a la decisión: la fila que el backfill de antes sí
            // habría creado.
            var externa = await contextoPreparacion.AsignacionesOperacion.FirstAsync(o => !o.EsRaiz);
            var heredada = AsignacionCartera.Externa(
                externa, usuario, rol, AmbitoAsignacion.Universal, externa.VigenciaDesde, null, DateTime.UtcNow);
            contextoPreparacion.AsignacionesCartera.Add(heredada);
            await contextoPreparacion.SaveChangesAsync();
            heredadaId = heredada.Id;
        }

        await EjecutarBackfillAsync();

        await using var contexto = CrearContexto(_clienteDelegante);
        (await contexto.AsignacionesCartera.SingleAsync(c => c.Id == heredadaId)).Estado
            .Should().Be(EstadoAsignacion.Cerrada, "la cartera heredada con rol de Propiedad se cierra al reconciliar");
        (await contexto.AsignacionesCartera.AnyAsync(c => c.UsuarioId == usuario && c.Estado == EstadoAsignacion.Vigente))
            .Should().BeFalse();
    }

    /// <summary>Control positivo: Consulta sí recibe su cartera universal.</summary>
    [Fact]
    public async Task Un_operador_delegado_Consulta_recibe_su_cartera_universal()
    {
        var usuario = await SembrarOperadorDelegadoAsync(Roles.Consulta);

        await EjecutarBackfillAsync();

        await using var contexto = CrearContexto(_clienteDelegante);
        var cartera = await contexto.AsignacionesCartera.SingleAsync(c => c.UsuarioId == usuario);
        cartera.Rol.Should().Be(Roles.Consulta);
        cartera.Ambito.EsUniversal.Should().BeTrue();
        cartera.Estado.Should().Be(EstadoAsignacion.Vigente);
    }

    private async Task<Guid> SembrarOperadorDelegadoAsync(string rol)
    {
        var usuario = Guid.NewGuid();
        await using var contexto = CrearContexto(_clienteDelegante);
        contexto.Users.Add(new ApplicationUser
        {
            Id = usuario,
            TenantId = _consultora,
            UserName = $"{rol.ToLowerInvariant()}@consultora",
            Email = $"{rol.ToLowerInvariant()}@consultora"
        });
        var delegacion = await contexto.DelegacionesTenant
            .FirstAsync(d => d.Proposito == PropositoDelegacion.OperadorExterno);
        contexto.AsignacionesOperadorDelegadoConRevocadas.Add(new AsignacionOperadorDelegado(delegacion.Id, usuario, rol));
        await contexto.SaveChangesAsync();
        return usuario;
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
