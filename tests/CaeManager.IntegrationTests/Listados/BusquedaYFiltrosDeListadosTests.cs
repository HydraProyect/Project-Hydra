using CaeManager.Application.Centros;
using CaeManager.Application.Centros.Queries.ObtenerCentros;
using CaeManager.Application.Centros.Queries.ObtenerOpcionesFiltroCentros;
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
/// - Con alcance restringido, ni la búsqueda por Cliente empresarial o Empresa ni las opciones de
///   sus filtros dejan ver lo que queda fuera de los Centros visibles.
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
    private Guid _almacenVigoId;

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

        var almacenVigo = new Centro(clienteOrion.Id, empresaNorte.Id, "Almacén Vigo", "DIR-513");
        contexto.Centros.AddRange(almacenVigo, new Centro(clientePegaso.Id, empresaSur.Id, "Planta Bilbao", "DIR-211"));
        await contexto.SaveChangesAsync();
        _almacenVigoId = almacenVigo.Id;
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

    [Theory]
    [InlineData("pegaso")]        // Cliente empresarial de un Centro fuera del alcance
    [InlineData("limpiezas sur")] // Empresa de un Centro fuera del alcance
    public async Task Centros_con_alcance_restringido_no_busca_fuera_de_los_Centros_visibles(string termino)
    {
        var alcance = new AlcanceDatosServiceFalso(centroIds: [_almacenVigoId]);

        var restringido = await ObtenerCentrosAsync(new ObtenerCentrosQuery(termino, null), alcance);
        var sinRestriccion = await ObtenerCentrosAsync(new ObtenerCentrosQuery(termino, null));
        var dentroDelAlcance = await ObtenerCentrosAsync(new ObtenerCentrosQuery("orion", null), alcance);

        restringido.Should().BeEmpty();
        sinRestriccion.Should().Equal("Planta Bilbao");
        dentroDelAlcance.Should().Equal("Almacén Vigo");
    }

    [Theory]
    [InlineData(CampoFiltroCentros.ClienteEmpresarial, "Orion Cliente S.L.", "Pegaso Cliente S.L.")]
    [InlineData(CampoFiltroCentros.Empresa, "Montajes Norte S.L.", "Limpiezas Sur S.L.")]
    public async Task Opciones_de_filtro_de_Centros_salen_de_los_Centros_visibles(
        CampoFiltroCentros campo, string dentroDelAlcance, string fueraDelAlcance)
    {
        var restringidas = await ObtenerOpcionesAsync(campo, new AlcanceDatosServiceFalso(centroIds: [_almacenVigoId]));
        var sinRestriccion = await ObtenerOpcionesAsync(campo, new AlcanceDatosServiceFalso());

        restringidas.Should().Equal(dentroDelAlcance);
        // Sin restricción salen las dos, y solo ellas: las Subcontratas sembradas no están en ningún Centro.
        sinRestriccion.Should().BeEquivalentTo(dentroDelAlcance, fueraDelAlcance);
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

    private async Task<IReadOnlyList<string>> ObtenerOpcionesAsync(CampoFiltroCentros campo, AlcanceDatosServiceFalso alcance)
    {
        await using var contexto = CrearContexto();
        var handler = new ObtenerOpcionesFiltroCentrosQueryHandler(contexto, contexto, alcance);

        var resultado = await handler.Handle(new ObtenerOpcionesFiltroCentrosQuery(campo), CancellationToken.None);
        return resultado.Select(o => o.RazonSocial).ToList();
    }

    private async Task<IReadOnlyList<string>> ObtenerCentrosAsync(
        ObtenerCentrosQuery consulta, AlcanceDatosServiceFalso? alcance = null)
    {
        await using var contexto = CrearContexto();
        var handler = new ObtenerCentrosQueryHandler(
            contexto, contexto, alcance ?? new AlcanceDatosServiceFalso(),
            new CalculoEstadoCentroService(contexto, contexto, contexto, contexto, contexto, contexto));

        var resultado = await handler.Handle(consulta, CancellationToken.None);
        return resultado.Elementos.Select(c => c.Nombre).ToList();
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
