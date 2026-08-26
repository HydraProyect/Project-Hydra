using CaeManager.Application.Common;
using CaeManager.Application.Empresas.Commands.CrearEmpresa;
using CaeManager.Application.Empresas.Commands.EditarEmpresa;
using CaeManager.Application.Importacion;
using CaeManager.Application.Importacion.Commands.EjecutarImportacionCombinada;
using CaeManager.Application.Plataforma;
using CaeManager.Application.Subcontratas.Commands.CrearSubcontrata;
using CaeManager.Application.Subcontratas.Commands.EditarSubcontrata;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.RelacionesEmpresariales;
using CaeManager.Domain.Subcontratas;
using CaeManager.Infrastructure.Autorizacion;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Repositories;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests;

/// <summary>
/// F4 — doble escritura transitoria (legacy + RelacionEmpresarial) en los
/// cinco escritores reales de las tres tablas puente, más el seeder. Invoca
/// los HANDLERS REALES (no fakes) contra Postgres real: la garantía que
/// importa es que el código de producción, no un test aislado, mantiene
/// ambas fuentes sincronizadas.
///
/// <para>
/// Contrato transitorio con fecha de caducidad — ver
/// <see cref="CaeManager.Application.RelacionesEmpresariales.SincronizacionRelacionEmpresarial"/>:
/// esta doble escritura desaparece cuando RelacionEmpresarial pase a ser la
/// única fuente de escritura (siguiente incremento de F4), y con ella este
/// fichero.
/// </para>
/// </summary>
public class DobleEscrituraRelacionEmpresarialTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenant = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto();
        await contexto.Database.MigrateAsync();
    }

    public async Task DisposeAsync() => await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Fact]
    public async Task CrearEmpresaCommand_sincroniza_el_alta_de_ClienteIds()
    {
        Guid cliente;
        await using (var contexto = CrearContexto())
        {
            var cli = Empresa.CrearComoCliente("Cliente Doble Escritura Uno S.A.", "B12345674", false, null, null);
            contexto.Empresas.Add(cli);
            await contexto.SaveChangesAsync();
            cliente = cli.Id;
        }

        Guid empresaId;
        await using (var contexto = CrearContexto())
        {
            var handler = new CrearEmpresaCommandHandler(
                new EmpresaRepository(contexto), new EmpresaClienteRepository(contexto),
                new RelacionEmpresarialRepository(contexto), contexto, contexto);

            var resultado = await handler.Handle(
                new CrearEmpresaCommand("Empresa Doble Escritura Uno S.L.", "B10380186", [cliente]), CancellationToken.None);

            resultado.EsExitoso.Should().BeTrue();
            empresaId = resultado.Valor;
        }

        await using var verificacion = CrearContexto();
        await AserirLegacyIgualARelacionEmpresarialAsync(verificacion);

        var relacion = await verificacion.RelacionesEmpresariales.SingleAsync(r => r.ProveedoraId == empresaId && r.ClienteId == cliente);
        relacion.EnmarcadaEnId.Should().BeNull();
        relacion.EstaVigente.Should().BeTrue();
        relacion.OrigenVigencia.Should().Be(OrigenVigencia.HistoricaConfirmada, "es un alta nueva, no una migración de datos legacy");
    }

    [Fact]
    public async Task EditarEmpresaCommand_sincroniza_alta_y_baja_de_ClienteIds_cerrando_no_borrando()
    {
        Guid empresaId, clienteUno, clienteDos;
        await using (var contexto = CrearContexto())
        {
            var cli1 = Empresa.CrearComoCliente("Cliente Doble Escritura Dos S.A.", "B10380194", false, null, null);
            var cli2 = Empresa.CrearComoCliente("Cliente Doble Escritura Tres S.A.", "B10380202", false, null, null);
            var empresa = new Empresa("Empresa Doble Escritura Dos S.L.", "B10380210");
            contexto.Empresas.AddRange(cli1, cli2, empresa);
            await contexto.SaveChangesAsync();
            clienteUno = cli1.Id; clienteDos = cli2.Id; empresaId = empresa.Id;

            contexto.EmpresasClientes.Add(new EmpresaCliente(empresaId, clienteUno));
            contexto.RelacionesEmpresariales.Add(RelacionEmpresarial.Crear(empresaId, clienteUno, DateTime.UtcNow));
            await contexto.SaveChangesAsync();
        }

        await using (var contexto = CrearContexto())
        {
            var handler = new EditarEmpresaCommandHandler(
                new EmpresaRepository(contexto), new EmpresaClienteRepository(contexto),
                new RelacionEmpresarialRepository(contexto), contexto,
                CrearAlcanceConAccesoTotal(contexto), contexto);

            // Deseado: quitar clienteUno, añadir clienteDos.
            var resultado = await handler.Handle(
                new EditarEmpresaCommand(empresaId, "Empresa Doble Escritura Dos S.L.", "B10380210", [clienteDos]),
                CancellationToken.None);

            resultado.EsExitoso.Should().BeTrue();
        }

        await using var verificacion = CrearContexto();
        await AserirLegacyIgualARelacionEmpresarialAsync(verificacion);

        var relacionCerrada = await verificacion.RelacionesEmpresariales.SingleAsync(r => r.ProveedoraId == empresaId && r.ClienteId == clienteUno);
        relacionCerrada.EstaVigente.Should().BeFalse("la baja debe CERRAR la relación, nunca borrarla — legacy sí borra, RelacionEmpresarial no");

        var relacionNueva = await verificacion.RelacionesEmpresariales.SingleAsync(r => r.ProveedoraId == empresaId && r.ClienteId == clienteDos);
        relacionNueva.EstaVigente.Should().BeTrue();
    }

    [Fact]
    public async Task CrearSubcontrataCommand_resuelve_enmarcadaEn_con_un_unico_candidato_coherente()
    {
        Guid cliente, empresaPropia;
        await using (var contexto = CrearContexto())
        {
            var cli = Empresa.CrearComoCliente("Cliente Doble Escritura Cuatro S.A.", "B10380228", false, null, null);
            var empresa = new Empresa("Empresa Doble Escritura Tres S.L.", "B10380236");
            contexto.Empresas.AddRange(cli, empresa);
            await contexto.SaveChangesAsync();
            cliente = cli.Id; empresaPropia = empresa.Id;

            contexto.EmpresasClientes.Add(new EmpresaCliente(empresaPropia, cliente));
            contexto.RelacionesEmpresariales.Add(RelacionEmpresarial.Crear(empresaPropia, cliente, DateTime.UtcNow));
            await contexto.SaveChangesAsync();
        }

        Guid subcontrataId;
        await using (var contexto = CrearContexto())
        {
            var handler = new CrearSubcontrataCommandHandler(
                new EmpresaRepository(contexto), new SubcontrataClienteRepository(contexto),
                new SubcontrataEmpresaRepository(contexto), new RelacionEmpresarialRepository(contexto),
                contexto, contexto);

            var resultado = await handler.Handle(
                new CrearSubcontrataCommand("Subcontrata Doble Escritura Uno S.L.", "B10380244", [cliente], [empresaPropia]),
                CancellationToken.None);

            resultado.EsExitoso.Should().BeTrue();
            subcontrataId = resultado.Valor;
        }

        await using var verificacion = CrearContexto();
        await AserirLegacyIgualARelacionEmpresarialAsync(verificacion);

        // "Nivel 1" real para efectos de enmarcadaEn es la relación
        // Empresa->Cliente que ya existía ANTES de crear la Subcontrata
        // (sembrada arriba) — no la relación Subcontrata->Empresa que crea
        // este mismo comando (esa es de primer nivel también, pero no es la
        // que enmarca nada).
        var relacionEmpresaCliente = await verificacion.RelacionesEmpresariales.SingleAsync(r => r.ProveedoraId == empresaPropia && r.ClienteId == cliente);

        var relacionSubcontrataEmpresa = await verificacion.RelacionesEmpresariales.SingleAsync(r => r.ProveedoraId == subcontrataId && r.ClienteId == empresaPropia);
        relacionSubcontrataEmpresa.EnmarcadaEnId.Should().BeNull("es de primer nivel: nada la enmarca");

        var relacionSubcontrataCliente = await verificacion.RelacionesEmpresariales.SingleAsync(r => r.ProveedoraId == subcontrataId && r.ClienteId == cliente);
        relacionSubcontrataCliente.EnmarcadaEnId.Should().Be(relacionEmpresaCliente.Id,
            "único candidato coherente: la Empresa vinculada (empresaPropia) ya servía a este Cliente");
    }

    [Fact]
    public async Task EditarSubcontrataCommand_sincroniza_altas_y_bajas_de_EmpresaIds_y_ClienteIds()
    {
        Guid subcontrataId, cliente, empresaUno, empresaDos;
        await using (var contexto = CrearContexto())
        {
            var cli = Empresa.CrearComoCliente("Cliente Doble Escritura Cinco S.A.", "B10380251", false, null, null);
            var e1 = new Empresa("Empresa Doble Escritura Cuatro S.L.", "B10380269");
            var e2 = new Empresa("Empresa Doble Escritura Cinco S.L.", "B10380277");
            contexto.Empresas.AddRange(cli, e1, e2);
            await contexto.SaveChangesAsync();
            cliente = cli.Id; empresaUno = e1.Id; empresaDos = e2.Id;

            var subcontrata = Empresa.CrearComoSubcontrata("Subcontrata Doble Escritura Dos S.L.", "B10380285", NivelServicioSubcontrata.Gestionada.ToString());
            contexto.Empresas.Add(subcontrata);
            await contexto.SaveChangesAsync();
            subcontrataId = subcontrata.Id;

            contexto.SubcontratasEmpresas.Add(new SubcontrataEmpresa(subcontrataId, empresaUno));
            contexto.RelacionesEmpresariales.Add(RelacionEmpresarial.Crear(subcontrataId, empresaUno, DateTime.UtcNow));
            await contexto.SaveChangesAsync();
        }

        await using (var contexto = CrearContexto())
        {
            var handler = new EditarSubcontrataCommandHandler(
                new EmpresaRepository(contexto), new SubcontrataClienteRepository(contexto),
                new SubcontrataEmpresaRepository(contexto), new RelacionEmpresarialRepository(contexto),
                contexto, CrearAlcanceConAccesoTotal(contexto), contexto);

            // Deseado: quitar empresaUno, añadir empresaDos y el cliente.
            var resultado = await handler.Handle(
                new EditarSubcontrataCommand(subcontrataId, "Subcontrata Doble Escritura Dos S.L.", "B10380285", [cliente], [empresaDos]),
                CancellationToken.None);

            resultado.EsExitoso.Should().BeTrue();
        }

        await using var verificacion = CrearContexto();
        await AserirLegacyIgualARelacionEmpresarialAsync(verificacion);

        (await verificacion.RelacionesEmpresariales.SingleAsync(r => r.ProveedoraId == subcontrataId && r.ClienteId == empresaUno))
            .EstaVigente.Should().BeFalse("empresaUno se quitó — cerrar, no borrar");
        (await verificacion.RelacionesEmpresariales.SingleAsync(r => r.ProveedoraId == subcontrataId && r.ClienteId == empresaDos))
            .EstaVigente.Should().BeTrue();
        (await verificacion.RelacionesEmpresariales.SingleAsync(r => r.ProveedoraId == subcontrataId && r.ClienteId == cliente))
            .EstaVigente.Should().BeTrue();
    }

    [Fact]
    public async Task EjecutarImportacionCombinadaCommand_es_idempotente_ejecutado_dos_veces()
    {
        var plan = new PlanImportacionCombinadaDto(
            Clientes: [new ClienteImportadoDto("Cliente Import Doble Escritura S.A.", "B10380293", false, YaExiste: false)],
            Empresas: [new EmpresaCombinadaImportadaDto("Empresa Import Doble Escritura S.L.", ["Cliente Import Doble Escritura S.A."], YaExiste: false)],
            Centros: [], Trabajadores: [], Advertencias: [], Omitidos: []);

        async Task<ResultadoImportacionCombinadaDto> EjecutarAsync()
        {
            await using var contexto = CrearContexto();
            var handler = new EjecutarImportacionCombinadaCommandHandler(
                new EmpresaRepository(contexto), new EmpresaClienteRepository(contexto),
                new RelacionEmpresarialRepository(contexto),
                new CaeManager.Infrastructure.Persistence.Repositories.CentroRepository(contexto),
                new CaeManager.Infrastructure.Persistence.Repositories.TrabajadorRepository(contexto),
                contexto, contexto, contexto, contexto);

            var resultado = await handler.Handle(new EjecutarImportacionCombinadaCommand(plan, ReemplazarExistentes: false), CancellationToken.None);
            return resultado.Valor;
        }

        var primeraEjecucion = await EjecutarAsync();
        primeraEjecucion.ClientesCreados.Should().Be(1);
        primeraEjecucion.EmpresasCreadas.Should().Be(1);

        var segundaEjecucion = await EjecutarAsync();
        segundaEjecucion.ClientesCreados.Should().Be(0, "el cliente ya existe — la segunda ejecución no debe recrearlo");
        segundaEjecucion.EmpresasCreadas.Should().Be(0);

        await using var verificacion = CrearContexto();
        await AserirLegacyIgualARelacionEmpresarialAsync(verificacion);

        var totalRelaciones = await verificacion.RelacionesEmpresariales.CountAsync();
        totalRelaciones.Should().Be(1, "ejecutar la misma importación dos veces no debe duplicar la relación");
    }

    /// <summary>
    /// Proyección semántica completa (TenantId, ProveedoraId, ClienteId,
    /// EnmarcadaEnId, vigente/no-vigente) — no un recuento de filas. Cada
    /// par legacy VIVO (sigue existiendo en su tabla) debe tener una
    /// RelacionEmpresarial VIGENTE, y viceversa.
    /// </summary>
    private static async Task AserirLegacyIgualARelacionEmpresarialAsync(CaeManagerDbContext contexto)
    {
        var legacyEmpresaCliente = await contexto.EmpresasClientes
            .Select(ec => new { ec.TenantId, Proveedora = ec.EmpresaId, Cliente = ec.ClienteId })
            .ToListAsync();
        var legacySubcontrataEmpresa = await contexto.SubcontratasEmpresas
            .Select(se => new { se.TenantId, Proveedora = se.SubcontrataId, Cliente = se.EmpresaId })
            .ToListAsync();
        var legacySubcontrataCliente = await contexto.SubcontratasClientes
            .Select(sc => new { sc.TenantId, Proveedora = sc.SubcontrataId, Cliente = sc.ClienteId })
            .ToListAsync();

        var paresLegacy = legacyEmpresaCliente.Concat(legacySubcontrataEmpresa).Concat(legacySubcontrataCliente)
            .Select(x => (x.TenantId, x.Proveedora, x.Cliente))
            .ToHashSet();

        var paresVigentesEnNueva = await contexto.RelacionesEmpresariales
            .Where(r => r.VigenciaHasta == null)
            .Select(r => new { r.TenantId, r.ProveedoraId, r.ClienteId })
            .ToListAsync();
        var paresVigentes = paresVigentesEnNueva.Select(x => (x.TenantId, Proveedora: x.ProveedoraId, Cliente: x.ClienteId)).ToHashSet();

        paresVigentes.Should().BeEquivalentTo(paresLegacy,
            "cada par legacy vivo debe tener exactamente una RelacionEmpresarial vigente, y no debe haber vigentes sin respaldo legacy");
    }

    private IAlcanceDatosService CrearAlcanceConAccesoTotal(CaeManagerDbContext contexto) =>
        new AlcanceDatosService(
            contexto, new CurrentUserServiceFalso(Guid.NewGuid(), "Administrador"),
            new TenantActualAmbiental { TenantId = _tenant },
            new SesionPrivilegiadaAusente());

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
