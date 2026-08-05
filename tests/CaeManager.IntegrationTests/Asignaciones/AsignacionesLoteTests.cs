using CaeManager.Application.Alertas;
using CaeManager.Application.Asignaciones.Commands.CrearAsignaciones;
using CaeManager.Application.Asignaciones.Commands.DarDeBajaAsignaciones;
using CaeManager.Application.Asignaciones.Queries.ObtenerDocumentosFaltantesParaAsignacion;
using CaeManager.Domain.Asignaciones;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Clientes;
using CaeManager.Domain.Documentos;
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
/// Fase B: alta y baja de Asignaciones en lote (producto cartesiano
/// Trabajadores × Centros, la misma forma que la matriz del Excel original —
/// ver <c>DATABASE.md</c>) y el preflight de documentos que le faltarían a
/// cada Trabajador en cada Centro antes de confirmar.
/// </summary>
public class AsignacionesLoteTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenant = Guid.NewGuid();

    private Guid _trabajador1Id;
    private Guid _trabajador2Id;
    private Guid _centro1Id;
    private Guid _centro2Id;
    private Guid _tipoObligatorioId;

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto();
        await contexto.Database.MigrateAsync();

        var cliente = new Cliente("Lote de Asignaciones S.A.", "B12345674", esCritico: false);
        var empresa = new Empresa("Contratas del Lote S.L.", "B87654323");
        contexto.Clientes.Add(cliente);
        contexto.Empresas.Add(empresa);
        await contexto.SaveChangesAsync();

        var centro1 = new Centro(cliente.Id, empresa.Id, "Centro Norte");
        var centro2 = new Centro(cliente.Id, empresa.Id, "Centro Sur");
        contexto.Centros.AddRange(centro1, centro2);

        var trabajador1 = Trabajador.DeEmpresa(empresa.Id, "Ana", "García", "77189989B");
        var trabajador2 = Trabajador.DeEmpresa(empresa.Id, "Luis", "Pérez", "12345678Z");
        contexto.Trabajadores.AddRange(trabajador1, trabajador2);

        var tipoObligatorio = new TipoDocumento("Apto médico", 12, true, 1, AmbitoAplicacion.Trabajador, esObligatorio: true);
        contexto.TiposDocumento.Add(tipoObligatorio);
        await contexto.SaveChangesAsync();

        // Trabajador1 ya está asignado al Centro1 — el lote debe omitir esta
        // combinación en silencio en vez de tratarla como error.
        contexto.Asignaciones.Add(new Asignacion(trabajador1.Id, centro1.Id, DateOnly.FromDateTime(DateTime.UtcNow)));
        await contexto.SaveChangesAsync();

        _trabajador1Id = trabajador1.Id;
        _trabajador2Id = trabajador2.Id;
        _centro1Id = centro1.Id;
        _centro2Id = centro2.Id;
        _tipoObligatorioId = tipoObligatorio.Id;
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Fact]
    public async Task Crea_el_producto_cartesiano_y_omite_en_silencio_lo_ya_activo()
    {
        await using var contexto = CrearContexto();
        var handler = new CrearAsignacionesCommandHandler(new AsignacionRepository(contexto), contexto, contexto, contexto, contexto);

        var resultado = await handler.Handle(
            new CrearAsignacionesCommand(
                [_trabajador1Id, _trabajador2Id], [_centro1Id, _centro2Id], DateOnly.FromDateTime(DateTime.UtcNow)),
            CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        // 2 trabajadores x 2 centros = 4 combinaciones; una ya estaba activa
        // (trabajador1, centro1) → 3 nuevas, 1 omitida.
        resultado.Valor.Creadas.Should().Be(3);
        resultado.Valor.YaActivas.Should().Be(1);
        resultado.Valor.Errores.Should().BeEmpty();

        var totalActivas = await contexto.Asignaciones.CountAsync(a => a.FechaBaja == null);
        totalActivas.Should().Be(4);
    }

    [Fact]
    public async Task Un_id_de_trabajador_inexistente_se_reporta_como_error_sin_bloquear_el_resto()
    {
        await using var contexto = CrearContexto();
        var handler = new CrearAsignacionesCommandHandler(new AsignacionRepository(contexto), contexto, contexto, contexto, contexto);

        var resultado = await handler.Handle(
            new CrearAsignacionesCommand(
                [_trabajador2Id, Guid.NewGuid()], [_centro2Id], DateOnly.FromDateTime(DateTime.UtcNow)),
            CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        resultado.Valor.Creadas.Should().Be(1);
        resultado.Valor.Errores.Should().ContainSingle(e => e.Contains("trabajador"));
    }

    [Fact]
    public async Task Dar_de_baja_en_lote_reporta_un_id_inexistente_sin_bloquear_el_resto()
    {
        Guid asignacion1Id;
        Guid asignacion2Id;

        await using (var contexto = CrearContexto())
        {
            var creacion = new CrearAsignacionesCommandHandler(new AsignacionRepository(contexto), contexto, contexto, contexto, contexto);
            await creacion.Handle(
                new CrearAsignacionesCommand([_trabajador2Id], [_centro1Id, _centro2Id], DateOnly.FromDateTime(DateTime.UtcNow)),
                CancellationToken.None);

            var activas = await contexto.Asignaciones
                .Where(a => a.TrabajadorId == _trabajador2Id && a.FechaBaja == null)
                .Select(a => a.Id)
                .ToListAsync();
            asignacion1Id = activas[0];
            asignacion2Id = activas[1];
        }

        await using var contextoBaja = CrearContexto();
        var handlerBaja = new DarDeBajaAsignacionesCommandHandler(new AsignacionRepository(contextoBaja), contextoBaja);

        var resultado = await handlerBaja.Handle(
            new DarDeBajaAsignacionesCommand([asignacion1Id, asignacion2Id, Guid.NewGuid()], DateOnly.FromDateTime(DateTime.UtcNow)),
            CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        resultado.Valor.DadasDeBaja.Should().Be(2);
        resultado.Valor.Errores.Should().ContainSingle();
    }

    [Fact]
    public async Task El_preflight_detecta_lo_que_le_faltaria_a_cada_trabajador_en_cada_centro()
    {
        await using var contexto = CrearContexto();
        var servicio = new DocumentosFaltantesService(contexto, contexto);
        var handler = new ObtenerDocumentosFaltantesParaAsignacionQueryHandler(contexto, contexto, servicio);

        // Ninguno de los dos trabajadores tiene el Apto médico todavía —
        // asignarlos a los dos centros debería avisar de 2 x 2 = 4 huecos.
        var faltantes = await handler.Handle(
            new ObtenerDocumentosFaltantesParaAsignacionQuery([_trabajador1Id, _trabajador2Id], [_centro1Id, _centro2Id]),
            CancellationToken.None);

        faltantes.Should().HaveCount(4);
        faltantes.Should().OnlyContain(f => f.TipoDocumentoId == _tipoObligatorioId);
    }

    [Fact]
    public async Task El_preflight_no_avisa_de_un_documento_que_el_trabajador_ya_tiene()
    {
        await using (var contexto = CrearContexto())
        {
            contexto.Documentos.Add(Documento.DeTrabajador(
                _trabajador1Id, _tipoObligatorioId, DateOnly.FromDateTime(DateTime.UtcNow), DateOnly.FromDateTime(DateTime.UtcNow).AddYears(1)));
            await contexto.SaveChangesAsync();
        }

        await using var contexto2 = CrearContexto();
        var servicio = new DocumentosFaltantesService(contexto2, contexto2);
        var handler = new ObtenerDocumentosFaltantesParaAsignacionQueryHandler(contexto2, contexto2, servicio);

        var faltantes = await handler.Handle(
            new ObtenerDocumentosFaltantesParaAsignacionQuery([_trabajador1Id], [_centro1Id]),
            CancellationToken.None);

        faltantes.Should().BeEmpty();
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
