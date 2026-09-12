using CaeManager.Application.Importacion;
using CaeManager.Application.Importacion.Commands.EjecutarImportacionCombinada;
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
/// Mismo contrato que <see cref="PlantillaClientesAlineacionAnalisisEjecucionTests"/>,
/// ahora sobre la Plantilla Combinada: lo que el ANÁLISIS
/// (<see cref="ClosedXmlPlantillaCombinadaService.AnalizarAsync"/>) promete al
/// usuario en el paso 2 tiene que ser exactamente lo que la ESCRITURA
/// (<see cref="EjecutarImportacionCombinadaCommandHandler"/>) hace al
/// confirmar. Cada test ejecuta los dos lados contra la misma base PostgreSQL
/// real y los compara — la propiedad que un test que solo mire un lado no
/// puede observar.
///
/// El desajuste que motiva estos tests: los dos lados usaban criterios
/// distintos para decidir qué Empresa es un <b>Cliente empresarial</b>. El
/// análisis usa <c>EsCritico != null</c> (el marcador de fila ex-Cliente, F3
/// § 2); la escritura sembraba su índice por razón social solo desde las
/// Empresas con <c>Cif != null</c>. Un Cliente empresarial sin CIF cae en la
/// diferencia entre ambos conjuntos.
/// </summary>
public class PlantillaCombinadaAlineacionAnalisisEjecucionTests : IAsyncLifetime
{
    private const string ClienteSinCif = "Cliente Sin CIF S.A.";
    private const string EmpresaProveedora = "Empresa Sur S.L.";
    private const string NombreCentro = "Centro Norte";

    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly TenantActualAmbiental _tenantActual = new() { TenantId = Guid.NewGuid() };

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto();
        await contexto.Database.MigrateAsync();
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    /// <summary>
    /// El defecto, por la hoja "Centros": el análisis resuelve el Cliente
    /// empresarial por nombre y mete la fila en el plan como centro a crear
    /// (la pantalla la pinta en verde); al confirmar, la escritura no
    /// encontraba ese nombre y descubría la omisión tarde.
    /// </summary>
    [Fact]
    public async Task Centro_de_un_Cliente_empresarial_sin_CIF_el_analisis_lo_promete_y_la_ejecucion_lo_crea()
    {
        await SembrarAsync(ClienteEmpresarialSinCif(), new Empresa(EmpresaProveedora));

        var libro = NuevoLibroBase();
        EscribirCentro(libro, ClienteSinCif, EmpresaProveedora);

        var plan = await AnalizarAsync(libro);

        plan.Omitidos.Should().BeEmpty();
        var centroDelPlan = plan.Centros.Should().ContainSingle().Subject;
        centroDelPlan.YaExiste.Should().BeFalse("el centro no existe todavía: el análisis promete crearlo");

        var resultado = await EjecutarAsync(plan);

        resultado.CentrosCreados.Should().Be(1, "la escritura tiene que cumplir lo que el análisis prometió");
        resultado.Omitidos.Should().BeEquivalentTo(plan.Omitidos, "confirmar no puede descubrir omisiones que el paso 2 no mostró");

        await using var verificacion = CrearContexto();
        var clienteId = (await verificacion.Empresas.SingleAsync(e => e.RazonSocial == ClienteSinCif)).Id;
        (await verificacion.Centros.CountAsync(c => c.Nombre == NombreCentro && c.ClienteId == clienteId)).Should().Be(1);
    }

    /// <summary>
    /// El mismo defecto por la hoja "Empresas", y aquí el descarte era
    /// totalmente silencioso: la escritura filtraba los clientes asociados que
    /// no estuvieran en su índice sin registrar nada en <c>Omitidos</c>, así
    /// que la Relación Empresarial simplemente no se creaba y ningún contador
    /// ni aviso lo decía.
    /// </summary>
    [Fact]
    public async Task Asociacion_con_un_Cliente_empresarial_sin_CIF_el_analisis_no_advierte_y_la_ejecucion_crea_la_relacion()
    {
        await SembrarAsync(ClienteEmpresarialSinCif());

        var libro = NuevoLibroBase();
        var hojaEmpresas = libro.Worksheets.Worksheet("Empresas");
        hojaEmpresas.Cell(2, 1).Value = EmpresaProveedora;
        hojaEmpresas.Cell(2, 2).Value = ClienteSinCif;

        var plan = await AnalizarAsync(libro);

        plan.Advertencias.Should().BeEmpty("el cliente existe: el análisis no omite la asociación");
        plan.Empresas.Should().ContainSingle().Which.ClientesAsociados.Should().ContainSingle().Which.Should().Be(ClienteSinCif);

        var resultado = await EjecutarAsync(plan);

        resultado.EmpresasCreadas.Should().Be(1);

        await using var verificacion = CrearContexto();
        var clienteId = (await verificacion.Empresas.SingleAsync(e => e.RazonSocial == ClienteSinCif)).Id;
        (await verificacion.RelacionesEmpresariales.CountAsync(r => r.ClienteId == clienteId && r.VigenciaHasta == null))
            .Should().Be(1, "la asociación que el análisis aceptó sin advertencia tiene que existir tras confirmar");
    }

    /// <summary>
    /// La otra mitad de la alineación, en la dirección contraria: una Empresa
    /// que tiene CIF pero NO es Cliente empresarial (aquí una Subcontrata,
    /// <c>EsCritico == null</c>) no puede recibir centros. El análisis ya lo
    /// impedía; la escritura la aceptaba por tener CIF, así que un plan que la
    /// nombre —el handler es un punto de entrada de MediatR por sí mismo, no
    /// solo el segundo paso de esta pantalla— colgaba el centro de una
    /// contraparte que no es cliente de nadie.
    /// </summary>
    [Fact]
    public async Task Empresa_con_CIF_que_no_es_Cliente_empresarial_no_recibe_centros_ni_en_el_analisis_ni_en_la_escritura()
    {
        const string subcontrata = "Subcontrata Con CIF S.L.";

        await SembrarAsync(
            Empresa.CrearComoSubcontrata(subcontrata, "B12345674", nivelServicio: "Estándar"),
            new Empresa(EmpresaProveedora));

        var libro = NuevoLibroBase();
        EscribirCentro(libro, subcontrata, EmpresaProveedora);

        var plan = await AnalizarAsync(libro);

        plan.Centros.Should().BeEmpty("una Subcontrata homónima no es un Cliente empresarial");
        plan.Omitidos.Should().ContainSingle(o => o.Hoja == "Centros" && o.Motivo.Contains("No se encontró el cliente"));

        // El plan que el análisis nunca produciría, ejecutado igualmente: la
        // escritura tiene que aplicar el mismo criterio por su cuenta. Los
        // omitidos se vacían a propósito: así lo que se mide abajo es solo lo
        // que añade la escritura, no lo que el análisis ya había registrado.
        var planForzado = plan with
        {
            Centros = new[]
            {
                new CentroImportadoDto(NombreCentro, subcontrata, EmpresaProveedora, null, null, null, null, YaExiste: false)
            },
            Omitidos = Array.Empty<ItemImportacionDto>()
        };

        var resultado = await EjecutarAsync(planForzado);

        resultado.CentrosCreados.Should().Be(0);
        resultado.Omitidos.Should().ContainSingle(o => o.Motivo.Contains("No se encontró el cliente"));

        await using var verificacion = CrearContexto();
        (await verificacion.Centros.CountAsync(c => c.Nombre == NombreCentro)).Should().Be(0);
    }

    /// <summary>
    /// Cliente empresarial creado como tal y luego editado sin CIF
    /// (<see cref="Empresa.Actualizar"/> admite <c>cif</c> nulo y no toca
    /// <c>EsCritico</c>): queda con <c>EsCritico != null</c> y <c>Cif == null</c>.
    /// </summary>
    private static Empresa ClienteEmpresarialSinCif()
    {
        var cliente = Empresa.CrearComoCliente(ClienteSinCif, "B12345674", esCritico: false, notas: null, ejecutivoUsuarioId: null);
        cliente.Actualizar(ClienteSinCif, cif: null);
        return cliente;
    }

    private async Task SembrarAsync(params Empresa[] empresas)
    {
        await using var seed = CrearContexto();
        seed.Empresas.AddRange(empresas);
        await seed.SaveChangesAsync();
    }

    private static void EscribirCentro(XLWorkbook libro, string razonSocialCliente, string razonSocialEmpresa)
    {
        var hoja = libro.Worksheets.Worksheet("Centros");
        hoja.Cell(2, 1).Value = NombreCentro;
        hoja.Cell(2, 2).Value = razonSocialCliente;
        hoja.Cell(2, 3).Value = razonSocialEmpresa;
    }

    private static XLWorkbook NuevoLibroBase()
    {
        var libro = new XLWorkbook();
        libro.Worksheets.Add("Clientes");
        libro.Worksheets.Add("Empresas");
        libro.Worksheets.Add("Centros");
        libro.Worksheets.Add("Trabajadores");
        return libro;
    }

    private async Task<PlanImportacionCombinadaDto> AnalizarAsync(XLWorkbook libro)
    {
        await using var contexto = CrearContexto();
        var servicio = new ClosedXmlPlantillaCombinadaService(contexto, contexto, contexto);

        using var flujo = new MemoryStream();
        libro.SaveAs(flujo);
        flujo.Position = 0;
        return await servicio.AnalizarAsync(flujo);
    }

    private async Task<ResultadoImportacionCombinadaDto> EjecutarAsync(PlanImportacionCombinadaDto plan)
    {
        await using var contexto = CrearContexto();
        var handler = new EjecutarImportacionCombinadaCommandHandler(
            new EmpresaRepository(contexto),
            new RelacionEmpresarialRepository(contexto),
            new CentroRepository(contexto),
            new TrabajadorRepository(contexto),
            contexto, contexto, contexto, contexto,
            new CurrentUserServiceFalso(Guid.NewGuid(), "Administrador"));

        var resultado = await handler.Handle(
            new EjecutarImportacionCombinadaCommand(plan, ReemplazarExistentes: false), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        return resultado.Valor;
    }

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
