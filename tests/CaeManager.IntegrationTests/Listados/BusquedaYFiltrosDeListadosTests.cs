using CaeManager.Application.Centros;
using CaeManager.Application.Centros.Queries.ObtenerCentros;
using CaeManager.Application.Centros.Queries.ObtenerEmpresasDeCentrosVisibles;
using CaeManager.Application.Documentos;
using CaeManager.Application.Empresas.Queries.ObtenerEmpresas;
using CaeManager.Application.Subcontratas;
using CaeManager.Application.Subcontratas.Queries.ObtenerSubcontratas;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Configuracion;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Subcontratas;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests.Listados;

/// <summary>
/// Rediseño de listados, fase 1: lo que los buscadores y las pastillas de /empresas,
/// /centros y /subcontratas prometen, contra Postgres (el proveedor InMemory no valida
/// que el filtro se traduzca a SQL).
///
/// - /empresas busca por razón social o CIF (antes solo razón social).
/// - /centros busca por nombre, código, Cliente empresarial o Empresa (antes solo nombre)
///   y filtra por Empresa.
/// - /subcontratas filtra por nivel de servicio.
///
/// Cada caso lleva su control positivo: el mismo término o filtro que debe excluir algo
/// devuelve también lo que debe incluir, así un filtro que no casa con nada no pasa por bueno.
/// </summary>
public class BusquedaYFiltrosDeListadosTests : IAsyncLifetime
{
    private const string CifEmpresaNorte = "B10380186";
    private const string CifEmpresaSur = "B10380194";

    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenant = Guid.NewGuid();

    private Guid _empresaNorteId;
    private Guid _empresaSurId;
    private Guid _centroVigoId;

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto();
        await contexto.Database.MigrateAsync();

        if (await contexto.ParametrosSistema.SingleOrDefaultAsync() is null)
            contexto.ParametrosSistema.Add(new ParametroSistema(30, 15));

        var clienteOrion = Empresa.CrearComoCliente("Orion Cliente S.L.", "B10000016", false, null, null);
        var clientePegaso = Empresa.CrearComoCliente("Pegaso Cliente S.L.", "B10000024", false, null, null);
        var empresaNorte = new Empresa("Montajes Norte S.L.", CifEmpresaNorte);
        var empresaSur = new Empresa("Limpiezas Sur S.L.", CifEmpresaSur);
        var gestionada = Empresa.CrearComoSubcontrata("Subcontrata Gestionada S.L.", "B10380236", NivelServicioSubcontrata.Gestionada.ToString());
        var supervisada = Empresa.CrearComoSubcontrata("Subcontrata Supervisada S.L.", "B10380251", NivelServicioSubcontrata.Supervisada.ToString());
        contexto.Empresas.AddRange(clienteOrion, clientePegaso, empresaNorte, empresaSur, gestionada, supervisada);
        await contexto.SaveChangesAsync();
        _empresaNorteId = empresaNorte.Id;
        _empresaSurId = empresaSur.Id;

        // Una Empresa cuyo único Centro está dado de baja: no debe salir entre las opciones del filtro.
        var empresaEste = new Empresa("Obras Este S.L.", "B10380210");
        contexto.Empresas.Add(empresaEste);
        await contexto.SaveChangesAsync();

        var vigo = new Centro(clienteOrion.Id, empresaNorte.Id, "Almacén Vigo", "DIR-513");
        var deBaja = new Centro(clientePegaso.Id, empresaEste.Id, "Nave Teruel", "DIR-999");
        contexto.Centros.AddRange(vigo, new Centro(clientePegaso.Id, empresaSur.Id, "Planta Bilbao", "DIR-211"), deBaja);
        await contexto.SaveChangesAsync();
        _centroVigoId = vigo.Id;

        deBaja.MarcarComoEliminado(Guid.NewGuid());
        await contexto.SaveChangesAsync();
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Fact]
    public async Task Empresas_busca_por_CIF_ademas_de_por_razon_social()
    {
        // El CIF entero y en minúsculas: los cuatro CIF de la siembra comparten prefijo.
        var porCif = await ObtenerEmpresasAsync(CifEmpresaNorte.ToLowerInvariant());
        var porNombre = await ObtenerEmpresasAsync("limpiezas");

        porCif.Should().Equal("Montajes Norte S.L.");
        porNombre.Should().Equal("Limpiezas Sur S.L.");
    }

    [Theory]
    [InlineData("vigo", "Almacén Vigo")]          // nombre del Centro
    [InlineData("dir-211", "Planta Bilbao")]      // código del Centro
    [InlineData("orion", "Almacén Vigo")]         // Cliente empresarial
    [InlineData("limpiezas sur", "Planta Bilbao")] // Empresa
    public async Task Centros_busca_por_nombre_codigo_Cliente_empresarial_o_Empresa(string termino, string esperado)
    {
        var resultado = await ObtenerCentrosAsync(new ObtenerCentrosQuery(termino, null));

        resultado.Should().Equal(esperado);
    }

    [Fact]
    public async Task Centros_filtra_por_Empresa()
    {
        var deNorte = await ObtenerCentrosAsync(new ObtenerCentrosQuery(null, null, EmpresaId: _empresaNorteId));
        var deSur = await ObtenerCentrosAsync(new ObtenerCentrosQuery(null, null, EmpresaId: _empresaSurId));
        var todos = await ObtenerCentrosAsync(new ObtenerCentrosQuery(null, null));

        deNorte.Should().Equal("Almacén Vigo");
        deSur.Should().Equal("Planta Bilbao");
        todos.Should().BeEquivalentTo("Almacén Vigo", "Planta Bilbao");
    }

    [Fact]
    public async Task Centros_filtra_por_Empresa_tambien_en_el_camino_con_estado()
    {
        // Ordenar por cumplimiento toma el camino que materializa antes de paginar: el filtro
        // tiene que estar aplicado antes de esa materialización, no solo en el camino en SQL.
        var deNorte = await ObtenerCentrosAsync(new ObtenerCentrosQuery(
            null, null, OrdenarPor: nameof(CentroListaDto.CumplimientoPorcentaje), EmpresaId: _empresaNorteId));

        deNorte.Should().Equal("Almacén Vigo");
    }

    /// <summary>
    /// Opciones del filtro «Empresa» de /centros: las Empresas de los Centros visibles. Ni Clientes
    /// empresariales ni Subcontratas (no trabajan como Empresa en ningún Centro), ni la Empresa de un
    /// Centro dado de baja.
    /// </summary>
    [Fact]
    public async Task Empresas_de_Centros_visibles_son_las_de_los_Centros_activos()
    {
        var opciones = await ObtenerEmpresasDeCentrosAsync(new AlcanceDatosServiceFalso());

        opciones.Should().Equal("Limpiezas Sur S.L.", "Montajes Norte S.L.");
    }

    /// <summary>
    /// Un usuario de portal (rol Cliente): alcance de gestión vacío, pero ve el Centro de Vigo. Las
    /// opciones salen de lo que ve —la Empresa de Vigo—, ni vacías (alcance de gestión) ni todas.
    /// </summary>
    [Fact]
    public async Task Empresas_de_Centros_visibles_siguen_el_alcance_de_visibilidad_y_no_el_de_gestion()
    {
        var portal = new AlcanceDatosServiceFalso(centroIds: [_centroVigoId], empresaIdsParaGestion: []);

        var opciones = await ObtenerEmpresasDeCentrosAsync(portal);

        opciones.Should().Equal("Montajes Norte S.L.");
    }

    [Theory]
    [InlineData(NivelServicioSubcontrata.Gestionada, "Subcontrata Gestionada S.L.")]
    [InlineData(NivelServicioSubcontrata.Supervisada, "Subcontrata Supervisada S.L.")]
    public async Task Subcontratas_filtra_por_nivel_de_servicio(NivelServicioSubcontrata nivel, string esperada)
    {
        var filtradas = await ObtenerSubcontratasAsync(nivel);
        var todas = await ObtenerSubcontratasAsync(null);

        filtradas.Should().Equal(esperada);
        todas.Should().BeEquivalentTo("Subcontrata Gestionada S.L.", "Subcontrata Supervisada S.L.");
    }

    private async Task<IReadOnlyList<string>> ObtenerEmpresasAsync(string busqueda)
    {
        await using var contexto = CrearContexto();
        var handler = new ObtenerEmpresasQueryHandler(
            contexto, new AlcanceDatosServiceFalso(),
            new CalculoEstadoDocumentalService(contexto, contexto),
            contexto, contexto, contexto, contexto,
            new CalculoEstadoCentroService(contexto, contexto, contexto, contexto, contexto, contexto));

        var resultado = await handler.Handle(new ObtenerEmpresasQuery(busqueda, TamanoPagina: 50), CancellationToken.None);
        return resultado.Elementos.Select(e => e.RazonSocial).ToList();
    }

    private async Task<IReadOnlyList<string>> ObtenerCentrosAsync(ObtenerCentrosQuery consulta)
    {
        await using var contexto = CrearContexto();
        var handler = new ObtenerCentrosQueryHandler(
            contexto, contexto, new AlcanceDatosServiceFalso(),
            new CalculoEstadoCentroService(contexto, contexto, contexto, contexto, contexto, contexto));

        var resultado = await handler.Handle(consulta, CancellationToken.None);
        return resultado.Elementos.Select(c => c.Nombre).ToList();
    }

    private async Task<IReadOnlyList<string>> ObtenerEmpresasDeCentrosAsync(AlcanceDatosServiceFalso alcance)
    {
        await using var contexto = CrearContexto();
        var handler = new ObtenerEmpresasDeCentrosVisiblesQueryHandler(contexto, contexto, alcance);

        var resultado = await handler.Handle(new ObtenerEmpresasDeCentrosVisiblesQuery(), CancellationToken.None);
        return resultado.Select(e => e.RazonSocial).ToList();
    }

    private async Task<IReadOnlyList<string>> ObtenerSubcontratasAsync(NivelServicioSubcontrata? nivel)
    {
        await using var contexto = CrearContexto();
        var alcance = new AlcanceDatosServiceFalso();
        var servicio = new CalculoEstadoSubcontrataService(contexto, contexto, contexto, contexto, contexto, contexto, alcance);
        var handler = new ObtenerSubcontratasQueryHandler(contexto, alcance, servicio);

        var resultado = await handler.Handle(new ObtenerSubcontratasQuery(null, NivelServicio: nivel), CancellationToken.None);
        return resultado.Elementos.Select(s => s.RazonSocial).ToList();
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
