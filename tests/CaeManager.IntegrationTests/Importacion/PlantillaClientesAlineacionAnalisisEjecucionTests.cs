using CaeManager.Application.Importacion;
using CaeManager.Application.Importacion.Commands.EjecutarImportacion;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Empresas;
using CaeManager.Infrastructure.Importacion;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Interceptors;
using CaeManager.Infrastructure.Persistence.Repositories;
using ClosedXML.Excel;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests.Importacion;

/// <summary>
/// Reproduce contra PostgreSQL real (entidades + <c>DbContext</c>, no SQL a
/// mano) los dos defectos medidos en la Plantilla de Clientes:
///
/// 1. El análisis (<see cref="ClosedXmlPlantillaClientesService.AnalizarAsync"/>)
/// prometía altas («N se crearán») que la escritura
/// (<see cref="EjecutarImportacionCommandHandler"/>) nunca hacía — Cliente
/// exige CIF y Centro exige Empresa (Fase 10), y esta plantilla de una sola
/// columna no recoge ninguno de los dos. Cada test ejecuta el ANÁLISIS y la
/// EJECUCIÓN contra la misma base y compara lo que uno prometió con lo que
/// la otra hizo de verdad — la propiedad que un test que solo mire un lado
/// no puede observar.
///
/// 2. El análisis consideraba "existente" cualquier Empresa con ese nombre; la
/// escritura (y <c>ObtenerClientesQuery</c>) solo reconocen Cliente
/// empresarial (<c>EsCritico != null</c>). Una Empresa homónima que no es
/// Cliente (p. ej. una Subcontrata) no puede hacer que la fila se dé por
/// "ya existente".
/// </summary>
public class PlantillaClientesAlineacionAnalisisEjecucionTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly TenantActualAmbiental _tenantActual = new() { TenantId = Guid.NewGuid() };

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto();
        await contexto.Database.MigrateAsync();
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Fact]
    public async Task Fila_cuyo_Cliente_y_Centro_ya_existian_el_analisis_no_promete_nada_y_la_ejecucion_no_escribe_nada()
    {
        const string nombre = "Cliente Ya Existente S.A.";

        await using (var seed = CrearContexto())
        {
            var cliente = Empresa.CrearComoCliente(nombre, "B12345674", esCritico: false, notas: null, ejecutivoUsuarioId: null);
            seed.Empresas.Add(cliente);
            await seed.SaveChangesAsync();
            seed.Centros.Add(new Centro(cliente.Id, cliente.Id, nombre));
            await seed.SaveChangesAsync();
        }

        var plan = await AnalizarAsync(LibroConFila(nombre));

        // El análisis reconoce la fila como reutilización, no como alta.
        plan.Omitidos.Should().BeEmpty();
        var clienteCentro = plan.ClientesCentros.Should().ContainSingle().Subject;
        clienteCentro.YaExisteCliente.Should().BeTrue();
        clienteCentro.YaExisteCentro.Should().BeTrue();

        await using var contexto = CrearContexto();
        var resultado = (await ConstruirHandler(contexto).Handle(new EjecutarImportacionCommand(plan), CancellationToken.None)).Valor;

        // La ejecución coincide exactamente con lo que el análisis prometió: nada.
        resultado.ClientesCreados.Should().Be(0);
        resultado.CentrosCreados.Should().Be(0);
        resultado.Omitidos.Should().BeEmpty("el análisis ya no tiene nada que descubrir tarde: no prometió ninguna alta para esta fila");
    }

    /// <summary>
    /// El defecto reportado, extremo a extremo: antes de este fix, una fila con
    /// Cliente y Centro nuevos entraba en <c>ClientesCentros</c> con
    /// <c>YaExisteCliente</c>/<c>YaExisteCentro</c> en <c>false</c> —la pantalla
    /// la pintaba en verde como "Crear cliente"/"Crear centro" y el contador
    /// «N se crearán» la contaba dos veces— y solo al confirmar
    /// <see cref="EjecutarImportacionCommandHandler"/> la omitía. El análisis y
    /// la ejecución tienen que coincidir: la fila se omite EN EL ANÁLISIS, con
    /// el mismo motivo, y la ejecución no añade ninguna omisión nueva ni crea
    /// nada.
    /// </summary>
    [Fact]
    public async Task Fila_con_Cliente_y_Centro_nuevos_el_analisis_ya_la_omite_y_la_ejecucion_no_anade_sorpresas()
    {
        const string nombre = "Cliente Completamente Nuevo S.L.";

        var plan = await AnalizarAsync(LibroConFila(nombre));

        // El análisis ya dice la verdad: nada que crear, una omisión explícita.
        plan.ClientesCentros.Should().BeEmpty("esta plantilla nunca puede crear un Cliente o Centro nuevo (Fase 10: exigen CIF/Empresa)");
        var omitidoEnAnalisis = plan.Omitidos.Should().ContainSingle().Subject;
        omitidoEnAnalisis.Descripcion.Should().Be(nombre);
        omitidoEnAnalisis.Motivo.Should().Contain("CIF");

        await using var contexto = CrearContexto();
        var resultado = (await ConstruirHandler(contexto).Handle(new EjecutarImportacionCommand(plan), CancellationToken.None)).Valor;

        resultado.ClientesCreados.Should().Be(0);
        resultado.CentrosCreados.Should().Be(0);
        // Ni una omisión más de las que el análisis ya había anunciado: nada
        // sorprende al confirmar lo que el paso 2 ya mostró al usuario.
        resultado.Omitidos.Should().BeEquivalentTo(plan.Omitidos);

        await using var verificacion = CrearContexto();
        (await verificacion.Empresas.CountAsync(e => e.RazonSocial == nombre)).Should().Be(0);
        (await verificacion.Centros.CountAsync(c => c.Nombre == nombre)).Should().Be(0);
    }

    /// <summary>
    /// El segundo defecto: una Empresa homónima que NO es Cliente empresarial
    /// (aquí, sin <c>EsCritico</c>) no puede colar la fila como "ya existe".
    /// </summary>
    [Fact]
    public async Task Empresa_homonima_que_no_es_Cliente_empresarial_el_analisis_la_trata_como_inexistente()
    {
        const string nombre = "Subcontrata Homónima S.L.";

        await using (var seed = CrearContexto())
        {
            // Empresa "a secas": EsCritico queda NULL — no es Cliente empresarial.
            seed.Empresas.Add(new Empresa(nombre));
            await seed.SaveChangesAsync();
        }

        var plan = await AnalizarAsync(LibroConFila(nombre));

        plan.ClientesCentros.Should().BeEmpty("una Empresa homónima que no es Cliente empresarial no cuenta como 'el cliente ya existe'");
        var omitido = plan.Omitidos.Should().ContainSingle().Subject;
        omitido.Motivo.Should().Contain("Este cliente no existe todavía");

        await using var contexto = CrearContexto();
        var resultado = (await ConstruirHandler(contexto).Handle(new EjecutarImportacionCommand(plan), CancellationToken.None)).Valor;

        resultado.ClientesCreados.Should().Be(0);
        resultado.CentrosCreados.Should().Be(0);
        resultado.Omitidos.Should().BeEquivalentTo(plan.Omitidos);
    }

    private static XLWorkbook LibroConFila(string nombre)
    {
        var libro = new XLWorkbook();
        var hoja = libro.Worksheets.Add("Clientes");
        hoja.Cell(1, 1).Value = "Cliente / Centro";
        hoja.Cell(2, 1).Value = nombre;
        hoja.Cell(2, 2).Value = "N";
        return libro;
    }

    private async Task<PlanImportacionDto> AnalizarAsync(XLWorkbook libro)
    {
        await using var contexto = CrearContexto();
        var servicio = new ClosedXmlPlantillaClientesService(contexto, contexto);

        using var flujo = new MemoryStream();
        libro.SaveAs(flujo);
        flujo.Position = 0;
        return await servicio.AnalizarAsync(flujo);
    }

    private static EjecutarImportacionCommandHandler ConstruirHandler(CaeManagerDbContext contexto) =>
        new(
            new EmpresaRepository(contexto), new TrabajadorRepository(contexto), new DocumentoRepository(contexto),
            new AsignacionRepository(contexto), new OperacionImportacionRepository(contexto),
            contexto, contexto, contexto, contexto, contexto, contexto,
            new CurrentUserServiceFalso(Guid.NewGuid(), "Administrador"));

    private CaeManagerDbContext CrearContexto()
    {
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql =>
            {
                npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL");
                // Fiel a ConfiguracionDeContexto.Aplicar (producción real) — ver
                // hydra-postgres-retry-strategy-vs-transaccion-explicita.
                npgsql.EnableRetryOnFailure(maxRetryCount: 6, maxRetryDelay: TimeSpan.FromSeconds(30), errorCodesToAdd: null);
            })
            .AddInterceptors(new TenantSelladoInterceptor(_tenantActual))
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), _tenantActual);
    }
}
