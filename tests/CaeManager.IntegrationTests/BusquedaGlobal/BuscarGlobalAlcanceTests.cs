using CaeManager.Application.BusquedaGlobal.Queries.BuscarGlobal;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Trabajadores;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests.BusquedaGlobal;

/// <summary>
/// El buscador global (Ctrl/Cmd+K) tiene que respetar la cartera asignada, no
/// solo el tenant. Antes de esta corrección un Gestor CAE veía por Ctrl+K
/// razones sociales, nombres y DNI de toda la organización aunque el listado
/// al que aterrizaba después sí estuviera acotado — brecha reportada por el
/// usuario (2026-08-13).
///
/// Todas las entidades del escenario comparten el prefijo "Zeta" en el nombre
/// para que un único término de búsqueda alcance las cinco categorías a la vez
/// y ninguna quede sin comprobar por accidente.
/// </summary>
public class BuscarGlobalAlcanceTests : IAsyncLifetime
{
    private const string Termino = "Zeta";

    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenant = Guid.NewGuid();

    private Guid _clienteEnCartera;
    private Guid _clienteFueraDeCartera;
    private Guid _empresaEnCartera;
    private Guid _empresaFueraDeCartera;
    private Guid _subcontrataEnCartera;
    private Guid _subcontrataFueraDeCartera;
    private Guid _centroEnCartera;
    private Guid _centroFueraDeCartera;
    private Guid _trabajadorEnCartera;
    private Guid _trabajadorFueraDeCartera;

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto();
        await contexto.Database.MigrateAsync();

        // F3c: las tres categorías del buscador viven en la MISMA tabla
        // Empresas — "Cliente" es EsCritico != null y "Subcontrata" es
        // NivelServicio != null, los mismos discriminadores que usan
        // ObtenerClientesQuery/ObtenerSubcontratasQuery. Antes de F3c estas
        // filas se sembraban en las tablas legacy, así que el test no podía
        // observar el solape entre categorías que la producción ya tenía
        // desde F3b.
        var clienteMio = Empresa.CrearComoCliente(
            "Zeta Cliente Propio S.L.", "B12345674", esCritico: false, notas: null, ejecutivoUsuarioId: null);
        var clienteAjeno = Empresa.CrearComoCliente(
            "Zeta Cliente Ajeno S.L.", "B87654323", esCritico: false, notas: null, ejecutivoUsuarioId: null);

        var empresaMia = new Empresa("Zeta Empresa Propia S.L.");
        var empresaAjena = new Empresa("Zeta Empresa Ajena S.L.");

        var subcontrataMia = Empresa.CrearComoSubcontrata("Zeta Subcontrata Propia S.L.", null, "Gestionada");
        var subcontrataAjena = Empresa.CrearComoSubcontrata("Zeta Subcontrata Ajena S.L.", null, "Gestionada");

        contexto.Empresas.AddRange(clienteMio, clienteAjeno, empresaMia, empresaAjena, subcontrataMia, subcontrataAjena);

        await contexto.SaveChangesAsync();

        // El Centro se ancla a empresaMia/empresaAjena y no a clienteMio: el
        // buscador global acota Centro por su propio Id de cartera, no por el
        // del cliente, así que el ancla concreta no cambia lo que se mide.
        var centroMio = new Centro(empresaMia.Id, empresaMia.Id, "Zeta Centro Propio");
        var centroAjeno = new Centro(empresaAjena.Id, empresaAjena.Id, "Zeta Centro Ajeno");
        contexto.Centros.AddRange(centroMio, centroAjeno);

        var trabajadorMio = Trabajador.DeEmpresa(empresaMia.Id, "Zeta", "Propio", "12345678Z");
        var trabajadorAjeno = Trabajador.DeEmpresa(empresaAjena.Id, "Zeta", "Ajeno", "87654321X");
        contexto.Trabajadores.AddRange(trabajadorMio, trabajadorAjeno);

        await contexto.SaveChangesAsync();

        _clienteEnCartera = clienteMio.Id;
        _clienteFueraDeCartera = clienteAjeno.Id;
        _empresaEnCartera = empresaMia.Id;
        _empresaFueraDeCartera = empresaAjena.Id;
        _subcontrataEnCartera = subcontrataMia.Id;
        _subcontrataFueraDeCartera = subcontrataAjena.Id;
        _centroEnCartera = centroMio.Id;
        _centroFueraDeCartera = centroAjeno.Id;
        _trabajadorEnCartera = trabajadorMio.Id;
        _trabajadorFueraDeCartera = trabajadorAjeno.Id;
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Fact]
    public async Task Un_gestor_con_cartera_no_ve_por_el_buscador_nada_de_fuera_de_ella()
    {
        var resultado = await BuscarAsync(new AlcanceDatosServiceFalso(
            clienteIds: [_clienteEnCartera],
            centroIds: [_centroEnCartera],
            empresaIds: [_empresaEnCartera],
            subcontrataIds: [_subcontrataEnCartera],
            trabajadorIds: [_trabajadorEnCartera]));

        resultado.Clientes.Should().ContainSingle().Which.Id.Should().Be(_clienteEnCartera);
        resultado.Empresas.Should().ContainSingle().Which.Id.Should().Be(_empresaEnCartera);
        resultado.Subcontratas.Should().ContainSingle().Which.Id.Should().Be(_subcontrataEnCartera);
        resultado.Centros.Should().ContainSingle().Which.Id.Should().Be(_centroEnCartera);
        resultado.Trabajadores.Should().ContainSingle().Which.Id.Should().Be(_trabajadorEnCartera);

        var todosLosIds = resultado.Clientes
            .Concat(resultado.Empresas).Concat(resultado.Subcontratas)
            .Concat(resultado.Centros).Concat(resultado.Trabajadores)
            .Select(i => i.Id);

        todosLosIds.Should().NotContain(new[]
        {
            _clienteFueraDeCartera, _empresaFueraDeCartera, _subcontrataFueraDeCartera,
            _centroFueraDeCartera, _trabajadorFueraDeCartera
        });
    }

    [Fact]
    public async Task Una_cartera_vacia_no_devuelve_nada()
    {
        // Contrato de IAlcanceDatosService: lista vacía ≠ null. Es un usuario
        // con cartera todavía sin asignar, no un administrador.
        var resultado = await BuscarAsync(new AlcanceDatosServiceFalso(
            clienteIds: [], centroIds: [], empresaIds: [], subcontrataIds: [], trabajadorIds: []));

        resultado.TieneResultados.Should().BeFalse();
    }

    [Fact]
    public async Task Sin_restriccion_de_cartera_se_sigue_viendo_todo()
    {
        var resultado = await BuscarAsync(new AlcanceDatosServiceFalso());

        resultado.Clientes.Should().HaveCount(2);
        resultado.Subcontratas.Should().HaveCount(2);

        // La categoría "Empresas" no filtra por discriminador — igual que el
        // listado /empresas al que enlaza, que tampoco lo hace. Tras F3c eso
        // se ve: las seis filas sembradas son Empresas, y la categoría las
        // devuelve todas hasta su límite de 5 por categoría. No es un cambio
        // de comportamiento de F3c: en producción, toda contraparte es una
        // fila de Empresas desde F3b — este test es el primero que puede
        // observarlo. Que Cliente y Subcontrata aparezcan además bajo
        // "Empresas" es una pregunta de producto abierta, no una regresión.
        resultado.Empresas.Should().HaveCount(5);
        resultado.Empresas.Select(e => e.Id).Should().Contain([_empresaEnCartera, _empresaFueraDeCartera]);
        resultado.Centros.Should().HaveCount(2);
        resultado.Trabajadores.Should().HaveCount(2);
    }

    private async Task<ResultadoBusquedaGlobalDto> BuscarAsync(AlcanceDatosServiceFalso alcance)
    {
        await using var contexto = CrearContexto();
        var handler = new BuscarGlobalQueryHandler(contexto, contexto, contexto, contexto, contexto, contexto, contexto, alcance);

        return await handler.Handle(new BuscarGlobalQuery(Termino), CancellationToken.None);
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
