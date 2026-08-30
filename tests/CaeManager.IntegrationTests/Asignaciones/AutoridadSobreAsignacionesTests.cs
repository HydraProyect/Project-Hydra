using CaeManager.Application.Asignaciones.Commands.CrearAsignacion;
using CaeManager.Application.Asignaciones.Commands.CrearAsignaciones;
using CaeManager.Application.Asignaciones.Commands.DarDeBajaAsignacion;
using CaeManager.Application.Asignaciones.Commands.DarDeBajaAsignaciones;
using CaeManager.Domain.Asignaciones;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Trabajadores;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Repositories;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests.Asignaciones;

/// <summary>
/// «Un gestor no debe poder operar fuera de su ámbito efectivo» (decisión del
/// propietario, 2026-08-29).
///
/// <para>
/// Antes de esto los cuatro comandos de asignación comprobaban, como mucho,
/// que el centro <b>existiera</b> en el tenant. Cualquier gestor podía dar de
/// alta a un trabajador en el centro de otro con solo conocer su Id, y las
/// bajas ni siquiera comprobaban eso: cargaban la asignación por identificador
/// y la cerraban.
/// </para>
///
/// <para>
/// Los cinco tests usan el mismo montaje —dos centros, uno dentro del ámbito
/// y otro fuera— porque la propiedad es la misma en los cuatro caminos, y
/// tenerla escrita cuatro veces es lo que impide que uno de ellos se quede
/// atrás cuando alguien añada el quinto.
/// </para>
/// </summary>
public class AutoridadSobreAsignacionesTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenant = Guid.NewGuid();

    private Guid _trabajadorId;
    private Guid _trabajadorAjenoId;
    private Guid _centroEnAmbitoId;
    private Guid _centroAjenoId;
    private Guid _asignacionAjenaId;

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto();
        await contexto.Database.MigrateAsync();

        var cliente = Empresa.CrearComoCliente("Cliente de la cartera S.A.", "B12345674", false, null, null);
        var empresa = new Empresa("Contratas del Ambito S.L.", "B87654323");
        contexto.Empresas.AddRange(cliente, empresa);
        await contexto.SaveChangesAsync();

        var centroEnAmbito = new Centro(cliente.Id, empresa.Id, "Centro de mi cartera");
        var centroAjeno = new Centro(cliente.Id, empresa.Id, "Centro de otro gestor");
        contexto.Centros.AddRange(centroEnAmbito, centroAjeno);

        var trabajador = Trabajador.DeEmpresa(empresa.Id, "Ana", "Garcia", "77189989B");
        var trabajadorAjeno = Trabajador.DeEmpresa(empresa.Id, "Luis", "Perez", "12345678Z");
        contexto.Trabajadores.AddRange(trabajador, trabajadorAjeno);
        await contexto.SaveChangesAsync();

        // Una asignación ya existente en el centro ajeno: es la que los tests
        // de baja intentarán cerrar sin tener autoridad sobre ella.
        var asignacionAjena = new Asignacion(trabajador.Id, centroAjeno.Id, DateOnly.FromDateTime(DateTime.UtcNow));
        contexto.Asignaciones.Add(asignacionAjena);
        await contexto.SaveChangesAsync();

        _trabajadorId = trabajador.Id;
        _trabajadorAjenoId = trabajadorAjeno.Id;
        _centroEnAmbitoId = centroEnAmbito.Id;
        _centroAjenoId = centroAjeno.Id;
        _asignacionAjenaId = asignacionAjena.Id;
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Fact]
    public async Task No_se_puede_dar_de_alta_en_un_centro_fuera_del_ambito()
    {
        await using var contexto = CrearContexto();
        var handler = new CrearAsignacionCommandHandler(
            new AsignacionRepository(contexto), AutoridadSoloSobre(contexto, _centroEnAmbitoId), contexto);

        var resultado = await handler.Handle(
            new CrearAsignacionCommand(_trabajadorId, _centroAjenoId, DateOnly.FromDateTime(DateTime.UtcNow)),
            CancellationToken.None);

        resultado.EsFallido.Should().BeTrue("el centro no está en el árbol de autoridad de quien asigna");
        // Mismo código que un centro inexistente: no se confirma la existencia
        // de un centro ajeno diciendo «existe pero no es tuyo».
        resultado.Error.Codigo.Should().Be("Asignacion.CentroNoEncontrado");

        await using var comprobacion = CrearContexto();
        (await comprobacion.Asignaciones.CountAsync(a => a.CentroId == _centroAjenoId))
            .Should().Be(1, "no debe haberse creado ninguna asignación nueva en el centro ajeno");
    }

    [Fact]
    public async Task Se_puede_dar_de_alta_en_un_centro_del_propio_ambito()
    {
        // El control negativo: sin esto, un servicio de autoridad que dijera
        // «no» a todo pasaría los otros tests sin proteger nada.
        await using var contexto = CrearContexto();
        var handler = new CrearAsignacionCommandHandler(
            new AsignacionRepository(contexto), AutoridadSoloSobre(contexto, _centroEnAmbitoId), contexto);

        var resultado = await handler.Handle(
            new CrearAsignacionCommand(_trabajadorId, _centroEnAmbitoId, DateOnly.FromDateTime(DateTime.UtcNow)),
            CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
    }

    [Fact]
    public async Task El_lote_de_alta_descarta_los_centros_fuera_del_ambito()
    {
        await using var contexto = CrearContexto();
        var handler = new CrearAsignacionesCommandHandler(
            new AsignacionRepository(contexto), contexto,
            AutoridadSoloSobre(contexto, _centroEnAmbitoId), contexto);

        var resultado = await handler.Handle(
            new CrearAsignacionesCommand([_trabajadorId], [_centroEnAmbitoId, _centroAjenoId], DateOnly.FromDateTime(DateTime.UtcNow)),
            CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        resultado.Valor.Creadas.Should().Be(1, "solo el centro del propio ámbito entra en el lote");

        await using var comprobacion = CrearContexto();
        (await comprobacion.Asignaciones.CountAsync(a => a.CentroId == _centroAjenoId))
            .Should().Be(1, "la asignación preexistente sigue ahí, pero el lote no añadió ninguna");
    }

    [Fact]
    public async Task No_se_puede_dar_de_alta_a_un_trabajador_fuera_del_ambito()
    {
        // Auditoría Módulo 5, hallazgo crítico 6/9: antes solo se comprobaba
        // que el trabajador existiera en el tenant, no que estuviera bajo la
        // autoridad de quien asigna — "secuestro" de trabajador entre
        // carteras.
        await using var contexto = CrearContexto();
        var handler = new CrearAsignacionCommandHandler(
            new AsignacionRepository(contexto),
            AutoridadSoloSobre(contexto, [_centroEnAmbitoId], [_trabajadorId]), contexto);

        var resultado = await handler.Handle(
            new CrearAsignacionCommand(_trabajadorAjenoId, _centroEnAmbitoId, DateOnly.FromDateTime(DateTime.UtcNow)),
            CancellationToken.None);

        resultado.EsFallido.Should().BeTrue("el trabajador no está en la cartera de quien asigna");
        resultado.Error.Codigo.Should().Be("Asignacion.TrabajadorNoEncontrado");

        await using var comprobacion = CrearContexto();
        (await comprobacion.Asignaciones.CountAsync(a => a.TrabajadorId == _trabajadorAjenoId))
            .Should().Be(0, "no debe haberse creado ninguna asignación para el trabajador ajeno");
    }

    [Fact]
    public async Task El_lote_de_alta_descarta_los_trabajadores_fuera_del_ambito()
    {
        await using var contexto = CrearContexto();
        var handler = new CrearAsignacionesCommandHandler(
            new AsignacionRepository(contexto), contexto,
            AutoridadSoloSobre(contexto, [_centroEnAmbitoId], [_trabajadorId]), contexto);

        var resultado = await handler.Handle(
            new CrearAsignacionesCommand(
                [_trabajadorId, _trabajadorAjenoId], [_centroEnAmbitoId], DateOnly.FromDateTime(DateTime.UtcNow)),
            CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        resultado.Valor.Creadas.Should().Be(1, "solo el trabajador de la propia cartera entra en el lote");

        await using var comprobacion = CrearContexto();
        (await comprobacion.Asignaciones.CountAsync(a => a.TrabajadorId == _trabajadorAjenoId))
            .Should().Be(0);
    }

    [Fact]
    public async Task No_se_puede_dar_de_baja_una_asignacion_fuera_del_ambito()
    {
        await using var contexto = CrearContexto();
        var handler = new DarDeBajaAsignacionCommandHandler(
            new AsignacionRepository(contexto), AutoridadSoloSobre(contexto, _centroEnAmbitoId), contexto);

        var resultado = await handler.Handle(
            new DarDeBajaAsignacionCommand(_asignacionAjenaId, DateOnly.FromDateTime(DateTime.UtcNow)),
            CancellationToken.None);

        resultado.EsFallido.Should().BeTrue("la baja exige la misma autoridad que el alta: no es una excepción por ser reversible");
        resultado.Error.Codigo.Should().Be("Asignacion.NoEncontrada");

        await using var comprobacion = CrearContexto();
        var asignacion = await comprobacion.Asignaciones.SingleAsync(a => a.Id == _asignacionAjenaId);
        asignacion.FechaBaja.Should().BeNull("la asignación ajena debe seguir activa");
    }

    [Fact]
    public async Task El_lote_de_baja_no_cierra_asignaciones_fuera_del_ambito()
    {
        await using var contexto = CrearContexto();
        var handler = new DarDeBajaAsignacionesCommandHandler(
            new AsignacionRepository(contexto), AutoridadSoloSobre(contexto, _centroEnAmbitoId), contexto);

        var resultado = await handler.Handle(
            new DarDeBajaAsignacionesCommand([_asignacionAjenaId], DateOnly.FromDateTime(DateTime.UtcNow)),
            CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        resultado.Valor.DadasDeBaja.Should().Be(0);

        await using var comprobacion = CrearContexto();
        var asignacion = await comprobacion.Asignaciones.SingleAsync(a => a.Id == _asignacionAjenaId);
        asignacion.FechaBaja.Should().BeNull();
    }

    private static AutoridadAsignacionesServiceFalso AutoridadSoloSobre(
        CaeManagerDbContext contexto, params Guid[] centroIds) => new(contexto, centroIds);

    private static AutoridadAsignacionesServiceFalso AutoridadSoloSobre(
        CaeManagerDbContext contexto, IReadOnlyList<Guid> centroIds, IReadOnlyList<Guid> trabajadorIds) =>
        new(contexto, centroIds, trabajadorIds);

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
