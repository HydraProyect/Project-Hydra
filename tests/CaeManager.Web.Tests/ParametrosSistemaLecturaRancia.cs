using CaeManager.Application.Configuracion;
using CaeManager.Domain.Configuracion;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.Web.Tests;

/// <summary>
/// MJ-05 (HYPOTHESIS del plan): el contexto de un circuito de Blazor vive todo el circuito y
/// <c>ParametrosSistema</c> se lee con seguimiento. Si EF devolviera la instancia ya rastreada sin refrescar sus
/// valores, un circuito largo no vería un cambio de umbrales hecho por otro usuario. Dos contextos sobre la misma
/// base: «circuito» (el que lee) y «otro usuario» (el que cambia los umbrales).
/// </summary>
public class ParametrosSistemaLecturaRancia : IAsyncLifetime
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly SqliteConnection _conexion = new("Data Source=:memory:");
    private CaeManagerDbContext _circuito = null!;
    private CaeManagerDbContext _otroUsuario = null!;

    private CaeManagerDbContext NuevoContexto() => new(
        new DbContextOptionsBuilder<CaeManagerDbContext>().UseSqlite(_conexion).Options,
        new EphemeralDataProtectionProvider(),
        new TenantActualAmbiental { TenantId = _tenantId });

    public async Task InitializeAsync()
    {
        await _conexion.OpenAsync();
        _circuito = NuevoContexto();
        _otroUsuario = NuevoContexto();

        // Solo la tabla que se prueba: el resto del modelo es de PostgreSQL.
        var guion = _circuito.Database.GenerateCreateScript();
        var inicio = guion.IndexOf("CREATE TABLE \"ParametrosSistema\"", StringComparison.Ordinal);
        inicio.Should().BeGreaterThanOrEqualTo(0, "el guion generado debe incluir la tabla de parámetros");
        var fin = guion.IndexOf(");", inicio, StringComparison.Ordinal) + 2;
        await _circuito.Database.ExecuteSqlRawAsync(guion[inicio..fin]);

        // El sello de Tenant lo pone un interceptor que aquí no se registra: se fija a mano.
        var parametro = new ParametroSistema(30, 15);
        typeof(Domain.Common.EntidadConTenant).GetProperty(nameof(ParametroSistema.TenantId))!.SetValue(parametro, _tenantId);
        _otroUsuario.ParametrosSistema.Add(parametro);
        await _otroUsuario.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        await _circuito.DisposeAsync();
        await _otroUsuario.DisposeAsync();
        await _conexion.DisposeAsync();
    }

    [Fact]
    public async Task Un_circuito_largo_ve_el_cambio_de_umbrales_hecho_por_otro_usuario()
    {
        IConfiguracionQueryContext lector = _circuito;
        var antes = await lector.ParametrosSistema.SingleAsync();
        antes.UmbralAmbarDias.Should().Be(30);

        var otro = await _otroUsuario.ParametrosSistema.SingleAsync();
        otro.Actualizar(60, 20);
        await _otroUsuario.SaveChangesAsync();

        var despues = await lector.ParametrosSistema.SingleAsync();

        despues.UmbralAmbarDias.Should().Be(60, "el umbral cambió en la base; el circuito no puede seguir viendo el valor anterior");
        despues.UmbralRojoDias.Should().Be(20);
    }

    [Fact]
    public async Task Una_escritura_del_propio_circuito_se_ve_despues_en_la_lectura_sin_seguimiento()
    {
        var repositorio = new CaeManager.Infrastructure.Persistence.Repositories.ParametroSistemaRepository(_circuito);
        var propio = await repositorio.ObtenerAsync();
        propio.Actualizar(45, 10);
        await _circuito.SaveChangesAsync();

        IConfiguracionQueryContext lector = _circuito;
        var leido = await lector.ParametrosSistema.SingleAsync();

        leido.UmbralAmbarDias.Should().Be(45);
        leido.UmbralRojoDias.Should().Be(10);
    }

    [Fact]
    public async Task Guardar_unos_valores_iguales_a_los_que_el_circuito_tenia_rastreados_actualiza_la_base()
    {
        var repositorio = new CaeManager.Infrastructure.Persistence.Repositories.ParametroSistemaRepository(_circuito);
        (await repositorio.ObtenerAsync()).UmbralAmbarDias.Should().Be(30);

        var otro = await _otroUsuario.ParametrosSistema.SingleAsync();
        otro.Actualizar(60, 20);
        await _otroUsuario.SaveChangesAsync();

        // El Administrador del circuito vuelve a poner 30/15: iguales a su copia rastreada, distintos de la base.
        var enComando = await repositorio.ObtenerAsync();
        enComando.Actualizar(30, 15);
        await _circuito.SaveChangesAsync();

        await using var verificacion = NuevoContexto();
        var enBase = await verificacion.ParametrosSistema.SingleAsync();
        enBase.UmbralAmbarDias.Should().Be(30, "sin refrescar la copia rastreada, EF no ve cambio y no emite el UPDATE");
        enBase.UmbralRojoDias.Should().Be(15);
    }
}
