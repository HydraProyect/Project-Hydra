using CaeManager.Domain.Integraciones;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Repositories;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests.Integraciones;

/// <summary>
/// Guarda transitoria del alta de buzones compartidos contra PostgreSQL real
/// (<see cref="ReclamacionBuzonIntegracion.CuentaParaGuardaTransitoria"/> y la restricción de exclusión
/// <see cref="ReclamacionBuzonIntegracionRepository.RestriccionGuardaTransitoria"/>): solo la base de datos la hace
/// cumplir entre Tenants propietarios, así que un doble en memoria no la prueba. Mismo montaje que
/// <see cref="ReclamacionBuzonIntegracionRepositoryGuardaUnicidadTests"/>.
/// <para>
/// Una reclamación nueva de Microsoft 365 nace con identidad, así que la heredada (anterior a la columna
/// <c>IdentidadBuzon</c>) se simula con un UPDATE que vacía su identidad y, según el caso, su
/// <c>FueraDeGuardaTransitoriaUtc</c>.
/// </para>
/// </summary>
public class ReclamacionBuzonIntegracionRepositoryGuardaTransitoriaTests : IAsyncLifetime
{
    private const string Directorio = "11111111-2222-3333-4444-555555555555";

    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenantHeredado = Guid.NewGuid();
    private readonly Guid _tenantNuevo = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        await using var dbContext = CrearContexto(_tenantHeredado);
        await dbContext.Database.MigrateAsync();
    }

    public async Task DisposeAsync() =>
        await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    private CaeManagerDbContext CrearContexto(Guid? tenantId)
    {
        var tenantActual = new TenantActualAmbiental { TenantId = tenantId };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual))
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
    }

    /// <summary>
    /// Reclama un buzón propio en el Tenant heredado y lo deja como una fila anterior a la columna de identidad:
    /// <c>IdentidadBuzon</c> a NULL y <c>FueraDeGuardaTransitoriaUtc</c> a NULL (cuenta) o fijado (ya no cuenta).
    /// </summary>
    private async Task<Guid> SembrarHeredadaAsync(string buzonEmail, bool fueraDeGuarda)
    {
        Guid reclamacionId;
        await using (var contexto = CrearContexto(_tenantHeredado))
        {
            var conexion = new ConexionIntegracion(buzonEmail, $"Buzón {buzonEmail}");
            conexion.FijarIdentidadBuzon($"inbox-{buzonEmail}");
            contexto.ConexionesIntegracion.Add(conexion);
            var reclamacion = new ReclamacionBuzonIntegracion(
                buzonEmail, $"inbox-{buzonEmail}", _tenantHeredado, conexion.Id, DateTime.UtcNow);
            var repositorio = new ReclamacionBuzonIntegracionRepository(contexto);
            repositorio.Reclamar(reclamacion);
            (await repositorio.GuardarCambiosSiBuzonLibreAsync()).Should().Be(ResultadoReclamacionBuzon.Guardada);
            reclamacionId = reclamacion.Id;
        }

        DateTime? fueraDeGuardaUtc = fueraDeGuarda ? DateTime.UtcNow : null;
        await using (var contextoGlobal = CrearContexto(null))
        {
            var filas = await contextoGlobal.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE public."ReclamacionesBuzonIntegracion"
                SET "IdentidadBuzon" = NULL, "FueraDeGuardaTransitoriaUtc" = {fueraDeGuardaUtc}
                WHERE "Id" = {reclamacionId}
                """);
            filas.Should().Be(1, "la fila heredada tiene que existir para que el caso pruebe algo");
        }

        await using (var contextoVerificacion = CrearContexto(null))
        {
            var heredada = await contextoVerificacion.ReclamacionesBuzonIntegracion.SingleAsync(r => r.Id == reclamacionId);
            heredada.IdentidadBuzon.Should().BeNull();
            heredada.CuentaParaGuardaTransitoria.Should().Be(!fueraDeGuarda, "el UPDATE deja la fila en el estado del caso");
        }

        return reclamacionId;
    }

    private async Task<(ResultadoReclamacionBuzon Resultado, Guid ConexionId)> DarDeAltaEnTenantNuevoAsync(
        string buzonEmail, string identidad, bool compartido)
    {
        await using var contexto = CrearContexto(_tenantNuevo);
        var conexion = compartido
            ? ConexionIntegracion.CrearBuzonCompartido(buzonEmail, $"Buzón {buzonEmail}", Directorio, Guid.NewGuid())
            : new ConexionIntegracion(buzonEmail, $"Buzón {buzonEmail}");
        conexion.FijarIdentidadBuzon(identidad);
        contexto.ConexionesIntegracion.Add(conexion);
        var repositorio = new ReclamacionBuzonIntegracionRepository(contexto);
        repositorio.Reclamar(new ReclamacionBuzonIntegracion(
            buzonEmail, identidad, _tenantNuevo, conexion.Id, DateTime.UtcNow, esBuzonCompartido: compartido));

        return (await repositorio.GuardarCambiosSiBuzonLibreAsync(), conexion.Id);
    }

    private async Task<bool> ConexionPersistidaAsync(Guid conexionId)
    {
        await using var contexto = CrearContexto(_tenantNuevo);
        return await contexto.ConexionesIntegracion.AnyAsync(c => c.Id == conexionId);
    }

    [Fact]
    public async Task Con_una_heredada_que_cuenta_el_alta_compartida_queda_en_pausa_sin_dejar_filas()
    {
        await SembrarHeredadaAsync("heredado@operador.test", fueraDeGuarda: false);

        var (resultado, conexionId) = await DarDeAltaEnTenantNuevoAsync(
            "compartido@operador.test", "inbox-compartido", compartido: true);

        resultado.Should().Be(ResultadoReclamacionBuzon.AltaCompartidaEnPausa,
            "una heredada sin identidad podría ser un alias de este buzón, aunque sea de otro Tenant propietario");
        (await ConexionPersistidaAsync(conexionId)).Should().BeFalse("el alta rechazada no deja la conexión a medio guardar");
    }

    [Fact]
    public async Task Sin_heredadas_que_cuenten_el_alta_compartida_prospera()
    {
        var (resultado, conexionId) = await DarDeAltaEnTenantNuevoAsync(
            "compartido@operador.test", "inbox-compartido", compartido: true);

        resultado.Should().Be(ResultadoReclamacionBuzon.Guardada);
        (await ConexionPersistidaAsync(conexionId)).Should().BeTrue();
    }

    [Fact]
    public async Task Una_heredada_fuera_de_la_guarda_no_bloquea_el_alta_compartida()
    {
        await SembrarHeredadaAsync("heredado@operador.test", fueraDeGuarda: true);

        var (resultado, _) = await DarDeAltaEnTenantNuevoAsync(
            "compartido@operador.test", "inbox-compartido", compartido: true);

        resultado.Should().Be(ResultadoReclamacionBuzon.Guardada,
            "FueraDeGuardaTransitoriaUtc saca la fila del predicado de la restricción de exclusión");
    }

    [Fact]
    public async Task Con_una_heredada_que_cuenta_el_alta_de_un_buzon_propio_no_se_bloquea()
    {
        await SembrarHeredadaAsync("heredado@operador.test", fueraDeGuarda: false);

        var (resultado, conexionId) = await DarDeAltaEnTenantNuevoAsync(
            "propio@tenant.com", "inbox-propio", compartido: false);

        resultado.Should().Be(ResultadoReclamacionBuzon.Guardada,
            "la guarda solo pausa los buzones compartidos; un buzón propio con identidad queda fuera de su predicado");
        (await ConexionPersistidaAsync(conexionId)).Should().BeTrue();
    }
}
