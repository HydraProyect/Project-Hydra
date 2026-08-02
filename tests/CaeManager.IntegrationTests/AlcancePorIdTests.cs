using CaeManager.Application.Clientes.Queries.ObtenerClientePorId;
using CaeManager.Application.Documentos.Queries.ObtenerDocumentoPorId;
using CaeManager.Domain.Asignaciones;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Clientes;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Trabajadores;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Seed;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests;

/// <summary>
/// Verifica el fix del Issue #18 (IDOR): las consultas `*PorId*` deben
/// respetar el alcance por cartera de <see cref="IAlcanceDatosService"/> —
/// un Id fuera de la cartera del usuario actual debe comportarse
/// exactamente igual que "no existe" (null), nunca devolver el dato ni un
/// error explícito que confirme que la fila existe. Usa SQLite real (no
/// in-memory) porque las consultas afectadas usan LINQ contra
/// IApplicationDbContext, igual criterio que MigracionesTests.
/// </summary>
public class AlcancePorIdTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private CaeManagerDbContext _dbContext = null!;

    private Cliente _clienteVisible = null!;
    private Cliente _clienteAjeno = null!;
    private Documento _documentoDeTrabajadorVisible = null!;
    private Documento _documentoDeTrabajadorAjeno = null!;
    private Guid _trabajadorVisibleId;

    public async Task InitializeAsync()
    {
        var tenantActual = new TenantActualAmbiental { TenantId = TenantSeedData.IdPorDefecto };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual))
            .Options;

        _dbContext = new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
        await _dbContext.Database.MigrateAsync();

        _clienteVisible = new Cliente("Cadena Industrial Iberia S.A.", "B12345674", esCritico: true);
        _clienteAjeno = new Cliente("Otro cliente, de otra cartera", "P1234567D", esCritico: false);
        _dbContext.Clientes.AddRange(_clienteVisible, _clienteAjeno);

        var empresa = new Empresa("Ibertec S.A.");
        _dbContext.Empresas.Add(empresa);

        var centroVisible = new Centro(_clienteVisible.Id, empresa.Id, "Planta Sevilla");
        var centroAjeno = new Centro(_clienteAjeno.Id, empresa.Id, "Planta de otra cartera");
        _dbContext.Centros.AddRange(centroVisible, centroAjeno);

        var trabajadorVisible = Trabajador.DeEmpresa(empresa.Id, "Alvaro", "Sanchez Martin", "77189989B");
        var trabajadorAjeno = Trabajador.DeEmpresa(empresa.Id, "Otro", "Trabajador Ajeno", "12345678Z");
        _dbContext.Trabajadores.AddRange(trabajadorVisible, trabajadorAjeno);
        _trabajadorVisibleId = trabajadorVisible.Id;

        _dbContext.Asignaciones.AddRange(
            new Asignacion(trabajadorVisible.Id, centroVisible.Id, DateOnly.FromDateTime(DateTime.UtcNow)),
            new Asignacion(trabajadorAjeno.Id, centroAjeno.Id, DateOnly.FromDateTime(DateTime.UtcNow)));

        await _dbContext.SaveChangesAsync();

        // Documento de vigilancia de la salud — el caso más sensible del Issue #18:
        // un DNI/Id de Trabajador fuera de cartera no debe poder leerse por Id.
        var tipoDocumentoTrabajador = await _dbContext.TiposDocumento
            .FirstAsync(t => t.AmbitoAplicacion == AmbitoAplicacion.Trabajador);

        _documentoDeTrabajadorVisible = Documento.DeTrabajador(
            trabajadorVisible.Id, tipoDocumentoTrabajador.Id, DateOnly.FromDateTime(DateTime.UtcNow), null);
        _documentoDeTrabajadorAjeno = Documento.DeTrabajador(
            trabajadorAjeno.Id, tipoDocumentoTrabajador.Id, DateOnly.FromDateTime(DateTime.UtcNow), null);
        _dbContext.Documentos.AddRange(_documentoDeTrabajadorVisible, _documentoDeTrabajadorAjeno);

        await _dbContext.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        await _dbContext.Database.EnsureDeletedAsync();
        await _dbContext.DisposeAsync();
    }

    [Fact]
    public async Task Devuelve_el_cliente_cuando_esta_dentro_de_la_cartera_visible()
    {
        var alcance = new AlcanceDatosServiceFalso(clienteIds: [_clienteVisible.Id]);
        var handler = new ObtenerClientePorIdQueryHandler(_dbContext, alcance);

        var resultado = await handler.Handle(new ObtenerClientePorIdQuery(_clienteVisible.Id), CancellationToken.None);

        resultado.Should().NotBeNull();
        resultado!.RazonSocial.Should().Be(_clienteVisible.RazonSocial);
    }

    [Fact]
    public async Task Devuelve_null_al_pedir_un_cliente_fuera_de_la_cartera_visible_aunque_exista()
    {
        // Cartera restringida a _clienteVisible únicamente — _clienteAjeno existe
        // de verdad en la base de datos, pero no debe ser legible por este usuario.
        var alcance = new AlcanceDatosServiceFalso(clienteIds: [_clienteVisible.Id]);
        var handler = new ObtenerClientePorIdQueryHandler(_dbContext, alcance);

        var resultado = await handler.Handle(new ObtenerClientePorIdQuery(_clienteAjeno.Id), CancellationToken.None);

        resultado.Should().BeNull();
    }

    [Fact]
    public async Task Un_rol_sin_restriccion_ve_cualquier_cliente()
    {
        var alcance = new AlcanceDatosServiceFalso(); // todo null = sin restricción (Administrador/DireccionCae/Consulta)
        var handler = new ObtenerClientePorIdQueryHandler(_dbContext, alcance);

        var resultado = await handler.Handle(new ObtenerClientePorIdQuery(_clienteAjeno.Id), CancellationToken.None);

        resultado.Should().NotBeNull();
    }

    [Fact]
    public async Task Devuelve_el_documento_de_un_trabajador_dentro_de_la_cartera_visible()
    {
        var alcance = new AlcanceDatosServiceFalso(trabajadorIds: [_trabajadorVisibleId]);
        var handler = new ObtenerDocumentoPorIdQueryHandler(
            _dbContext, _dbContext, _dbContext, _dbContext, _dbContext, _dbContext, _dbContext, alcance);

        var resultado = await handler.Handle(
            new ObtenerDocumentoPorIdQuery(_documentoDeTrabajadorVisible.Id), CancellationToken.None);

        resultado.Should().NotBeNull();
    }

    [Fact]
    public async Task Devuelve_null_al_pedir_el_documento_de_salud_de_un_trabajador_fuera_de_cartera()
    {
        // Este es el caso concreto del Issue #18: sin el fix, un Gestor CAE con
        // cartera restringida a un trabajador podía leer igualmente el PDF de
        // vigilancia de la salud de un trabajador de otra cartera con solo
        // conocer/adivinar el Guid del Documento.
        var alcance = new AlcanceDatosServiceFalso(trabajadorIds: [_trabajadorVisibleId]);
        var handler = new ObtenerDocumentoPorIdQueryHandler(
            _dbContext, _dbContext, _dbContext, _dbContext, _dbContext, _dbContext, _dbContext, alcance);

        var resultado = await handler.Handle(
            new ObtenerDocumentoPorIdQuery(_documentoDeTrabajadorAjeno.Id), CancellationToken.None);

        resultado.Should().BeNull();
    }

    [Fact]
    public async Task Una_cartera_vacia_no_ve_ningun_documento_de_trabajador()
    {
        // Lista vacía = cartera sin asignar todavía (p. ej. un Gestor CAE recién
        // creado) — nunca debe confundirse con "sin restricción".
        var alcance = new AlcanceDatosServiceFalso(trabajadorIds: []);
        var handler = new ObtenerDocumentoPorIdQueryHandler(
            _dbContext, _dbContext, _dbContext, _dbContext, _dbContext, _dbContext, _dbContext, alcance);

        var resultado = await handler.Handle(
            new ObtenerDocumentoPorIdQuery(_documentoDeTrabajadorVisible.Id), CancellationToken.None);

        resultado.Should().BeNull();
    }
}
