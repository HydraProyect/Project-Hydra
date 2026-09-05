using CaeManager.Domain.Soporte;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests.Soporte;

/// <summary>
/// REC-208: <c>CK_RegistrosActividadSoporte_UnSoloAgrupador</c> es el
/// backstop en base para la invariante "exactamente uno de los dos
/// agrupadores" que <see cref="RegistroActividadSoporte"/> ya hace
/// irrepresentable desde el dominio (sus dos factorías nunca informan los
/// dos agrupadores ni ninguno). Estos tests la prueban con UPDATE directo,
/// no con la API del agregado — precisamente para demostrar que la base
/// rechaza el estado inválido <b>independientemente</b> de la validación de
/// C#, que es lo que REC-101 (el precedente de <c>Documento</c>) encontró
/// que faltaba para su propia constraint. Mismo patrón que
/// <c>CheckXorPropietarioDocumentoTests</c>.
/// </summary>
public class CheckXorAgrupadorRegistroActividadSoporteTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenant = Guid.NewGuid();
    private RegistroActividadSoporte _registroPorViaHeredada = null!;

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto();
        await contexto.Database.MigrateAsync();

        _registroPorViaHeredada = RegistroActividadSoporte.PorViaHeredada(
            Guid.NewGuid(), Guid.NewGuid(), TipoActividadSoporte.Navegacion, "/documentos");
        contexto.RegistrosActividadSoporte.Add(_registroPorViaHeredada);

        await contexto.SaveChangesAsync();
    }

    public async Task DisposeAsync() => await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Fact]
    public async Task No_se_puede_dar_los_dos_agrupadores_a_la_vez()
    {
        await using var contexto = CrearContexto();

        var excepcion = await Record.ExceptionAsync(() => contexto.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE \"RegistrosActividadSoporte\" SET \"SesionPrivilegiadaId\" = {Guid.NewGuid()} WHERE \"Id\" = {_registroPorViaHeredada.Id}"));

        excepcion.Should().NotBeNull(
            "CK_RegistrosActividadSoporte_UnSoloAgrupador debe rechazar un registro con los dos agrupadores informados");
    }

    [Fact]
    public async Task No_se_puede_dejar_un_registro_sin_ningun_agrupador()
    {
        await using var contexto = CrearContexto();

        var excepcion = await Record.ExceptionAsync(() => contexto.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE \"RegistrosActividadSoporte\" SET \"DelegacionTenantId\" = NULL WHERE \"Id\" = {_registroPorViaHeredada.Id}"));

        excepcion.Should().NotBeNull(
            "CK_RegistrosActividadSoporte_UnSoloAgrupador debe rechazar un registro sin ningún agrupador");
    }

    [Fact]
    public async Task Un_registro_por_sesion_privilegiada_se_persiste()
    {
        await using var contexto = CrearContexto();

        var registro = RegistroActividadSoporte.PorSesionPrivilegiada(
            Guid.NewGuid(), Guid.NewGuid(), TipoActividadSoporte.WorkspaceActivado);
        contexto.RegistrosActividadSoporte.Add(registro);

        var excepcion = await Record.ExceptionAsync(() => contexto.SaveChangesAsync());

        excepcion.Should().BeNull(
            "un registro con solo la sesión privilegiada informada cumple la constraint y debe persistir");
    }

    private CaeManagerDbContext CrearContexto()
    {
        var tenantActual = new TenantActualAmbiental { TenantId = _tenant };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual))
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
    }
}
