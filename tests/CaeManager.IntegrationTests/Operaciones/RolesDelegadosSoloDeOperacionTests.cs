using CaeManager.Domain.Empresas;
using CaeManager.Domain.Operaciones;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Operaciones;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Interceptors;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests.Operaciones;

/// <summary>
/// Decisión del propietario, 2026-09-23: una Asignación de Cartera o una
/// delegación solo conceden Coordinador CAE, Gestor CAE o Consulta.
/// Administrador y Dirección CAE son autoridad de Propiedad del Tenant
/// propietario (ADR-011 § 1) y <see cref="AsignacionesOperativasWriter"/> no
/// los concede por ninguno de sus dos caminos: la cartera universal
/// (<c>AbrirCarteraOperadorAsync</c>) y la cartera de un Cliente empresarial
/// (<c>ReasignarCarteraClienteAsync</c>, que lee el rol de la delegación).
///
/// <para>
/// Las filas de delegación con Administrador o Dirección CAE se siembran a mano:
/// <c>CrearAsignacionOperadorDelegadoCommandValidator</c> ya no las deja crear,
/// así que solo existen como datos heredados — justo lo que la lista blanca del
/// writer tiene que parar al leerlas.
/// </para>
/// </summary>
public class RolesDelegadosSoloDeOperacionTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _personaDelOperador = Guid.NewGuid();
    private Guid _operadorCae;   // ArcosSPA: Operador CAE externo
    private Guid _propietario;   // Refrielectric: Tenant propietario
    private Guid _clienteId;
    private Guid _delegacionId;

    public async Task InitializeAsync()
    {
        await using var inicial = CrearContexto(Guid.NewGuid());
        await inicial.Database.MigrateAsync();

        var operador = new Tenant("ArcosSPA (Operador CAE externo)", PerfilVocabularioTenant.Consultora);
        var propietario = new Tenant("Refrielectric (Tenant propietario)", PerfilVocabularioTenant.ClienteDirecto);
        inicial.Tenants.AddRange(operador, propietario);
        await inicial.SaveChangesAsync();

        _operadorCae = operador.Id;
        _propietario = propietario.Id;

        await using var contexto = CrearContexto(_propietario);

        contexto.Users.Add(new ApplicationUser
        {
            Id = _personaDelOperador,
            TenantId = _operadorCae,
            UserName = "persona@arcosspa.test",
            Email = "persona@arcosspa.test"
        });

        var delegacion = new DelegacionTenant(_operadorCae, _propietario);
        contexto.DelegacionesTenant.Add(delegacion);
        _delegacionId = delegacion.Id;

        var cliente = Empresa.CrearComoCliente("Cliente empresarial de Refrielectric", "B12345674", false, null, null);
        contexto.Empresas.Add(cliente);

        contexto.AsignacionesOperacion.Add(
            AsignacionOperacion.Raiz(_propietario, ServicioCae.Outbound, DateTime.UtcNow, DateTime.UtcNow));

        await contexto.SaveChangesAsync();
        _clienteId = cliente.Id;

        var writer = CrearWriter(contexto);
        await writer.AbrirOperacionDelegadaAsync(_propietario, _operadorCae, DateTime.UtcNow.AddDays(-1), vigenciaHasta: null);
        await contexto.SaveChangesAsync();
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Theory]
    [InlineData(Roles.Administrador)]
    [InlineData(Roles.DireccionCae)]
    public async Task La_cartera_universal_no_se_abre_con_un_rol_de_Propiedad(string rol)
    {
        await using (var contexto = CrearContexto(_propietario))
        {
            await CrearWriter(contexto)
                .Invoking(w => w.AbrirCarteraOperadorAsync(_propietario, _operadorCae, _personaDelOperador, rol))
                .Should().ThrowAsync<UnauthorizedAccessException>();

            await contexto.SaveChangesAsync();
        }

        (await CarterasDeLaPersonaAsync()).Should().BeEmpty("el rechazo no puede dejar escrita ninguna cartera");
    }

    [Fact]
    public async Task Control_positivo_la_cartera_universal_se_abre_con_Consulta()
    {
        await using (var contexto = CrearContexto(_propietario))
        {
            await CrearWriter(contexto).AbrirCarteraOperadorAsync(_propietario, _operadorCae, _personaDelOperador, Roles.Consulta);
            await contexto.SaveChangesAsync();
        }

        var carteras = await CarterasDeLaPersonaAsync();
        carteras.Should().ContainSingle().Which.Rol.Should().Be(Roles.Consulta);
    }

    [Theory]
    [InlineData(Roles.Administrador)]
    [InlineData(Roles.DireccionCae)]
    public async Task La_cartera_de_un_Cliente_empresarial_no_hereda_un_rol_de_Propiedad_de_la_delegacion(string rol)
    {
        await SembrarAsignacionDelegadaAsync(rol);

        await using (var contexto = CrearContexto(_propietario))
        {
            await CrearWriter(contexto)
                .Invoking(w => w.ReasignarCarteraClienteAsync(_clienteId, _personaDelOperador))
                .Should().ThrowAsync<UnauthorizedAccessException>();

            await contexto.SaveChangesAsync();
        }

        (await CarterasDeLaPersonaAsync()).Should().BeEmpty();
    }

    [Theory]
    [InlineData(Roles.CoordinadorCae)]
    [InlineData(Roles.GestorCae)]
    [InlineData(Roles.Consulta)]
    public async Task Control_positivo_la_cartera_de_un_Cliente_empresarial_hereda_un_rol_de_Operacion(string rol)
    {
        await SembrarAsignacionDelegadaAsync(rol);

        await using (var contexto = CrearContexto(_propietario))
        {
            await CrearWriter(contexto).ReasignarCarteraClienteAsync(_clienteId, _personaDelOperador);
            await contexto.SaveChangesAsync();
        }

        var carteras = await CarterasDeLaPersonaAsync();
        carteras.Should().ContainSingle(c => c.AmbitoRelacionClienteId == _clienteId).Which.Rol.Should().Be(rol);
    }

    /// <summary>
    /// Revisión Codex, ronda 2: una fila heredada con rol de Propiedad no
    /// impide reactivar la delegación. Se omite (no se reabre su cartera) y el
    /// operador válido de la misma delegación sí recupera la suya.
    /// </summary>
    [Theory]
    [InlineData(Roles.Administrador)]
    [InlineData(Roles.DireccionCae)]
    public async Task Reabrir_omite_la_fila_heredada_con_rol_de_Propiedad_sin_bloquear_a_las_validas(string rol)
    {
        var personaConsulta = Guid.NewGuid();
        await SembrarAsignacionDelegadaAsync(rol);
        await using (var siembra = CrearContexto(_propietario))
        {
            siembra.AsignacionesOperadorDelegadoConRevocadas.Add(new AsignacionOperadorDelegado(_delegacionId, personaConsulta, Roles.Consulta));
            await siembra.SaveChangesAsync();
        }

        await using (var contexto = CrearContexto(_propietario))
        {
            var operacion = await contexto.AsignacionesOperacion.FirstAsync(o => !o.EsRaiz);
            await CrearWriter(contexto).ReabrirCarterasDeOperadoresAsync(operacion, _delegacionId);
            await contexto.SaveChangesAsync();
        }

        (await CarterasDeLaPersonaAsync()).Should().BeEmpty("la fila con rol de Propiedad no se reabre");

        await using var lectura = CrearContexto(_propietario);
        (await lectura.AsignacionesCartera.Where(c => c.UsuarioId == personaConsulta).ToListAsync())
            .Should().ContainSingle().Which.Rol.Should().Be(Roles.Consulta);
    }

    /// <summary>
    /// P8: reactivar la delegación no resucita una asignación revocada, aunque
    /// su rol sea de Operación. Se usa un Gestor CAE revocado para que el
    /// resultado dependa de la revocación y no de la lista blanca de roles.
    /// </summary>
    [Fact]
    public async Task Reabrir_no_reactiva_una_asignacion_revocada()
    {
        var personaConsulta = Guid.NewGuid();
        await using (var siembra = CrearContexto(_propietario))
        {
            var revocada = new AsignacionOperadorDelegado(_delegacionId, _personaDelOperador, Roles.GestorCae);
            revocada.Revocar("Prueba de revocación", DateTime.UtcNow);
            siembra.AsignacionesOperadorDelegadoConRevocadas.AddRange(
                revocada, new AsignacionOperadorDelegado(_delegacionId, personaConsulta, Roles.Consulta));
            await siembra.SaveChangesAsync();
        }

        await using (var contexto = CrearContexto(_propietario))
        {
            var operacion = await contexto.AsignacionesOperacion.FirstAsync(o => !o.EsRaiz);
            await CrearWriter(contexto).ReabrirCarterasDeOperadoresAsync(operacion, _delegacionId);
            await contexto.SaveChangesAsync();
        }

        (await CarterasDeLaPersonaAsync()).Should().BeEmpty("la asignación revocada no se reabre");

        await using var lectura = CrearContexto(_propietario);
        (await lectura.AsignacionesCartera.Where(c => c.UsuarioId == personaConsulta).ToListAsync())
            .Should().ContainSingle("control positivo: la válida de la misma delegación sí se reabre");
    }

    private async Task SembrarAsignacionDelegadaAsync(string rol)
    {
        await using var contexto = CrearContexto(_propietario);
        contexto.AsignacionesOperadorDelegadoConRevocadas.Add(new AsignacionOperadorDelegado(_delegacionId, _personaDelOperador, rol));
        await contexto.SaveChangesAsync();
    }

    private async Task<List<AsignacionCartera>> CarterasDeLaPersonaAsync()
    {
        await using var contexto = CrearContexto(_propietario);
        return await contexto.AsignacionesCartera.Where(c => c.UsuarioId == _personaDelOperador).ToListAsync();
    }

    private AsignacionesOperativasWriter CrearWriter(CaeManagerDbContext contexto) =>
        new(contexto, new TenantActualAmbiental { TenantId = _propietario },
            new CurrentUserServiceFalso(Guid.NewGuid(), Roles.Administrador, tenantOrigenId: _propietario));

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
