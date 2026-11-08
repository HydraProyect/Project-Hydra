using CaeManager.Domain.Documentos;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Interceptors;
using CaeManager.Infrastructure.Persistence.Repositories;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests.Documentos;

/// <summary>
/// Revisión Codex de la PR #2074, ronda 4: <c>ConcurrenciaContexto.ListarActualesAsync</c>, en el que se apoyan las
/// lecturas <c>…ActualizadoAsync</c> que deciden bajo el candado de las altas con acreditación, contra PostgreSQL y con
/// dos contextos: el del circuito, que ya rastrea la fila, y el de otra operación, que la cambia y confirma. Se mide con
/// <see cref="TipoDocumento"/>, que no tiene token de concurrencia ni claves ajenas que montar; los tests de Application
/// (<c>LecturasBajoElCandadoVenLoConfirmadoTests</c>) miden que los comandos usen estas lecturas.
/// </summary>
public class LecturasActualizadasBajoElCandadoTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly TenantActualAmbiental _tenantActual = new() { TenantId = Guid.NewGuid() };
    private Guid _tipoId;

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto();
        await contexto.Database.MigrateAsync();
        var tipo = new TipoDocumento("Seguro RC lecturas actualizadas", 12, true, 1, AmbitoAplicacion.Empresa,
            requerido: RequisitoDocumental.No);
        contexto.TiposDocumento.Add(tipo);
        await contexto.SaveChangesAsync();
        _tipoId = tipo.Id;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task La_lectura_actualizada_refresca_la_instancia_rastreada_con_lo_que_otro_contexto_confirmo()
    {
        await using var circuito = CrearContexto();
        var repositorio = new TipoDocumentoRepository(circuito);
        var rastreada = await repositorio.ObtenerPorIdAsync(_tipoId);
        await ExigirDesdeOtroContextoAsync();

        // Control del instrumento: la consulta normal devuelve la misma instancia sin refrescar.
        (await repositorio.ObtenerPorIdAsync(_tipoId))!.Requerido.Should().Be(RequisitoDocumental.No);

        var actual = await repositorio.ObtenerPorIdActualizadoAsync(_tipoId);

        actual.Should().BeSameAs(rastreada);
        actual!.Requerido.Should().Be(RequisitoDocumental.Si);
        circuito.Entry(actual).State.Should().Be(EntityState.Unchanged);
    }

    [Fact]
    public async Task La_lectura_actualizada_no_pisa_los_cambios_pendientes_de_la_operacion_en_curso()
    {
        await using var circuito = CrearContexto();
        var repositorio = new TipoDocumentoRepository(circuito);
        var tipo = (await repositorio.ObtenerPorIdAsync(_tipoId))!;
        Actualizar(tipo, "Nombre de la operación en curso", tipo.Requerido);
        await ExigirDesdeOtroContextoAsync();

        var actual = await repositorio.ObtenerPorIdActualizadoAsync(_tipoId);

        actual.Should().BeSameAs(tipo);
        actual!.Nombre.Should().Be("Nombre de la operación en curso");
        circuito.Entry(actual).State.Should().Be(EntityState.Modified);
    }

    [Fact]
    public async Task La_lectura_actualizada_no_devuelve_la_fila_que_otro_contexto_borro()
    {
        await using var circuito = CrearContexto();
        var repositorio = new TipoDocumentoRepository(circuito);
        var rastreada = await repositorio.ObtenerPorIdAsync(_tipoId);
        await using (var otra = CrearContexto())
        {
            otra.TiposDocumento.Remove(await otra.TiposDocumento.SingleAsync(t => t.Id == _tipoId));
            await otra.SaveChangesAsync();
        }

        var actual = await repositorio.ObtenerPorIdActualizadoAsync(_tipoId);

        actual.Should().BeNull();
        circuito.Entry(rastreada!).State.Should().Be(EntityState.Detached);
    }

    [Fact]
    public async Task Editar_el_tipo_tras_la_lectura_actualizada_no_choca_con_el_alias_que_otro_contexto_borro()
    {
        // Revisión puente de la PR #2074, previa a la ronda 5, hallazgo 2: ReloadAsync no repone la colección Aliases.
        await using (var siembra = CrearContexto())
        {
            var tipo = await siembra.TiposDocumento.Include(t => t.Aliases).SingleAsync(t => t.Id == _tipoId);
            tipo.EstablecerAliases(["RC general", "RC patronal"]);
            await siembra.SaveChangesAsync();
        }

        await using var circuito = CrearContexto();
        var repositorio = new TipoDocumentoRepository(circuito);
        var rastreado = (await repositorio.ObtenerPorIdAsync(_tipoId))!;
        var aliasBorrado = rastreado.Aliases.Single(a => a.Texto == "RC general");
        await using (var otra = CrearContexto())
        {
            otra.Set<TipoDocumentoAlias>().Remove(
                await otra.Set<TipoDocumentoAlias>().SingleAsync(a => a.TipoDocumentoId == _tipoId && a.Texto == "RC general"));
            await otra.SaveChangesAsync();
        }

        var actual = (await repositorio.ObtenerPorIdActualizadoAsync(_tipoId))!;

        actual.Aliases.Select(a => a.Texto).Should().Equal("RC patronal");
        circuito.Entry(aliasBorrado).State.Should().Be(EntityState.Detached);
        actual.EstablecerAliases(["RC ampliado"]);
        await circuito.Invoking(c => c.SaveChangesAsync()).Should().NotThrowAsync();
        await using var comprobacion = CrearContexto();
        (await comprobacion.Set<TipoDocumentoAlias>().Where(a => a.TipoDocumentoId == _tipoId).Select(a => a.Texto).ToListAsync())
            .Should().Equal("RC ampliado");
    }

    private async Task ExigirDesdeOtroContextoAsync()
    {
        await using var otra = CrearContexto();
        var tipo = await otra.TiposDocumento.SingleAsync(t => t.Id == _tipoId);
        Actualizar(tipo, tipo.Nombre, RequisitoDocumental.Si);
        await otra.SaveChangesAsync();
    }

    private static void Actualizar(TipoDocumento tipo, string nombre, RequisitoDocumental requerido) =>
        tipo.Actualizar(nombre, tipo.VigenciaMeses, tipo.AplicaVencimientoAutomatico, tipo.Orden, requerido,
            tipo.Naturaleza, tipo.Notas, tipo.Descripcion, tipo.CriteriosValidacion, tipo.SeSolicitaA, tipo.Observaciones);

    private CaeManagerDbContext CrearContexto()
    {
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(_tenantActual))
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), _tenantActual);
    }
}
