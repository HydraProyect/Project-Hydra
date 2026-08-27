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
/// F4.2c — <c>RelacionEmpresarial</c> como ÚNICA fuente de escritura (R6
/// aceptada 2026-08-27): los cinco escritores reales ya no tocan las tres
/// tablas puente legacy. Sustituye a <c>DobleEscrituraRelacionEmpresarialTests</c>,
/// cuyo contrato (paridad legacy↔arista) muere con este incremento — la
/// paridad de los datos preexistentes se verificó con SELECT real contra
/// staging y producción (0 divergencias en ambas direcciones) antes de
/// retirar la doble escritura.
///
/// Invoca los HANDLERS REALES contra Postgres real, y además de las
/// propiedades heredadas (idempotencia, enmarcadaEn, cerrar-no-borrar)
/// afirma las dos nuevas: que las tablas legacy NO reciben escrituras, y que
/// una contraparte soft-deleted (OPACA para el diff, ver
/// <see cref="ContrapartesVigentes"/>) jamás se cierra por ausencia en el
/// request — el fallo de pérdida de datos que la revisión adversarial de
/// F4.2b encontró y que este diseño existe para impedir.
/// </summary>
public class FuenteUnicaRelacionEmpresarialTests : IAsyncLifetime
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
    public async Task CrearEmpresaCommand_escribe_solo_la_arista_y_nada_en_legacy()
    {
        Guid cliente;
        await using (var contexto = CrearContexto())
        {
            var cli = Empresa.CrearComoCliente("Cliente Fuente Unica Uno S.A.", "B12345674", false, null, null);
            contexto.Empresas.Add(cli);
            await contexto.SaveChangesAsync();
            cliente = cli.Id;
        }

        Guid empresaId;
        await using (var contexto = CrearContexto())
        {
            var handler = new CrearEmpresaCommandHandler(
                new EmpresaRepository(contexto), new RelacionEmpresarialRepository(contexto), contexto, contexto);

            var resultado = await handler.Handle(
                new CrearEmpresaCommand("Empresa Fuente Unica Uno S.L.", "B10380186", [cliente]), CancellationToken.None);

            resultado.EsExitoso.Should().BeTrue();
            empresaId = resultado.Valor;
        }

        await using var verificacion = CrearContexto();
        var relacion = await verificacion.RelacionesEmpresariales.SingleAsync(r => r.ProveedoraId == empresaId && r.ClienteId == cliente);
        relacion.EstaVigente.Should().BeTrue();
        relacion.EnmarcadaEnId.Should().BeNull();
        relacion.OrigenVigencia.Should().Be(OrigenVigencia.HistoricaConfirmada, "es un alta nueva, no una migración de datos legacy");

        (await verificacion.EmpresasClientes.CountAsync()).Should().Be(0,
            "F4.2c: la tabla puente legacy ya no recibe escrituras — la arista es la única fuente");
    }

    [Fact]
    public async Task EditarEmpresaCommand_cierra_sin_borrar_y_no_escribe_legacy()
    {
        Guid empresaId, clienteUno, clienteDos;
        await using (var contexto = CrearContexto())
        {
            var cli1 = Empresa.CrearComoCliente("Cliente Fuente Unica Dos S.A.", "B10380194", false, null, null);
            var cli2 = Empresa.CrearComoCliente("Cliente Fuente Unica Tres S.A.", "B10380202", false, null, null);
            var empresa = new Empresa("Empresa Fuente Unica Dos S.L.", "B10380210");
            contexto.Empresas.AddRange(cli1, cli2, empresa);
            await contexto.SaveChangesAsync();
            clienteUno = cli1.Id; clienteDos = cli2.Id; empresaId = empresa.Id;

            contexto.RelacionesEmpresariales.Add(RelacionEmpresarial.Crear(empresaId, clienteUno, DateTime.UtcNow));
            await contexto.SaveChangesAsync();
        }

        await using (var contexto = CrearContexto())
        {
            var handler = new EditarEmpresaCommandHandler(
                new EmpresaRepository(contexto), new RelacionEmpresarialRepository(contexto), contexto,
                CrearAlcanceConAccesoTotal(contexto), contexto);

            // Deseado: quitar clienteUno, añadir clienteDos.
            var resultado = await handler.Handle(
                new EditarEmpresaCommand(empresaId, "Empresa Fuente Unica Dos S.L.", "B10380210", [clienteDos]),
                CancellationToken.None);

            resultado.EsExitoso.Should().BeTrue();
        }

        await using var verificacion = CrearContexto();
        (await verificacion.RelacionesEmpresariales.SingleAsync(r => r.ProveedoraId == empresaId && r.ClienteId == clienteUno))
            .EstaVigente.Should().BeFalse("la baja debe CERRAR la relación, nunca borrarla");
        (await verificacion.RelacionesEmpresariales.SingleAsync(r => r.ProveedoraId == empresaId && r.ClienteId == clienteDos))
            .EstaVigente.Should().BeTrue();
        (await verificacion.EmpresasClientes.CountAsync()).Should().Be(0);
    }

    /// <summary>
    /// EL test de este incremento — el escenario exacto del fallo de pérdida
    /// de datos que la revisión adversarial de F4.2b encontró: una
    /// contraparte soft-deleted es invisible para la lectura (el usuario no
    /// pudo desmarcarla), así que su ausencia en el request NO puede
    /// interpretarse como baja. La relación debe sobrevivir a una edición
    /// que no la menciona.
    /// </summary>
    [Fact]
    public async Task EditarEmpresaCommand_no_cierra_la_relacion_con_una_contraparte_soft_deleted_ausente_del_request()
    {
        Guid empresaId, clienteVivo, clienteEliminado;
        await using (var contexto = CrearContexto())
        {
            var vivo = Empresa.CrearComoCliente("Cliente Vivo Fuente Unica S.A.", "B10380228", false, null, null);
            var eliminado = Empresa.CrearComoCliente("Cliente Eliminado Fuente Unica S.A.", "B10380236", false, null, null);
            var empresa = new Empresa("Empresa Con Cliente Eliminado S.L.", "B10380244");
            contexto.Empresas.AddRange(vivo, eliminado, empresa);
            await contexto.SaveChangesAsync();
            clienteVivo = vivo.Id; clienteEliminado = eliminado.Id; empresaId = empresa.Id;

            contexto.RelacionesEmpresariales.AddRange(
                RelacionEmpresarial.Crear(empresaId, clienteVivo, DateTime.UtcNow),
                RelacionEmpresarial.Crear(empresaId, clienteEliminado, DateTime.UtcNow));
            await contexto.SaveChangesAsync();

            eliminado.MarcarComoEliminado(Guid.NewGuid());
            await contexto.SaveChangesAsync();
        }

        await using (var contexto = CrearContexto())
        {
            var handler = new EditarEmpresaCommandHandler(
                new EmpresaRepository(contexto), new RelacionEmpresarialRepository(contexto), contexto,
                CrearAlcanceConAccesoTotal(contexto), contexto);

            // El request trae SOLO el cliente vivo — exactamente lo que la UI
            // enviaría, porque el eliminado no se pinta en ningún selector.
            var resultado = await handler.Handle(
                new EditarEmpresaCommand(empresaId, "Empresa Con Cliente Eliminado S.L.", "B10380244", [clienteVivo]),
                CancellationToken.None);

            resultado.EsExitoso.Should().BeTrue();
        }

        await using var verificacion = CrearContexto();
        (await verificacion.RelacionesEmpresariales.SingleAsync(r => r.ProveedoraId == empresaId && r.ClienteId == clienteEliminado))
            .EstaVigente.Should().BeTrue(
                "la contraparte eliminada es OPACA para el diff: el usuario no pudo desmarcarla, así que su ausencia no es una baja");
        (await verificacion.RelacionesEmpresariales.SingleAsync(r => r.ProveedoraId == empresaId && r.ClienteId == clienteVivo))
            .EstaVigente.Should().BeTrue();
    }

    [Fact]
    public async Task CrearSubcontrataCommand_resuelve_enmarcadaEn_con_un_unico_candidato_coherente()
    {
        Guid cliente, empresaPropia;
        await using (var contexto = CrearContexto())
        {
            var cli = Empresa.CrearComoCliente("Cliente Fuente Unica Cuatro S.A.", "B10380251", false, null, null);
            var empresa = new Empresa("Empresa Fuente Unica Tres S.L.", "B10380269");
            contexto.Empresas.AddRange(cli, empresa);
            await contexto.SaveChangesAsync();
            cliente = cli.Id; empresaPropia = empresa.Id;

            contexto.RelacionesEmpresariales.Add(RelacionEmpresarial.Crear(empresaPropia, cliente, DateTime.UtcNow));
            await contexto.SaveChangesAsync();
        }

        Guid subcontrataId;
        await using (var contexto = CrearContexto())
        {
            var handler = new CrearSubcontrataCommandHandler(
                new EmpresaRepository(contexto), new RelacionEmpresarialRepository(contexto), contexto, contexto);

            var resultado = await handler.Handle(
                new CrearSubcontrataCommand("Subcontrata Fuente Unica Uno S.L.", "B10380277", [cliente], [empresaPropia]),
                CancellationToken.None);

            resultado.EsExitoso.Should().BeTrue();
            subcontrataId = resultado.Valor;
        }

        await using var verificacion = CrearContexto();
        var relacionEmpresaCliente = await verificacion.RelacionesEmpresariales.SingleAsync(r => r.ProveedoraId == empresaPropia && r.ClienteId == cliente);

        (await verificacion.RelacionesEmpresariales.SingleAsync(r => r.ProveedoraId == subcontrataId && r.ClienteId == empresaPropia))
            .EnmarcadaEnId.Should().BeNull("es de primer nivel: nada la enmarca");

        (await verificacion.RelacionesEmpresariales.SingleAsync(r => r.ProveedoraId == subcontrataId && r.ClienteId == cliente))
            .EnmarcadaEnId.Should().Be(relacionEmpresaCliente.Id,
                "único candidato coherente: la Empresa vinculada ya servía a este Cliente");

        (await verificacion.SubcontratasClientes.CountAsync()).Should().Be(0);
        (await verificacion.SubcontratasEmpresas.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task EditarSubcontrataCommand_diffs_de_ambos_ejes_sobre_la_arista()
    {
        Guid subcontrataId, cliente, empresaUno, empresaDos;
        await using (var contexto = CrearContexto())
        {
            var cli = Empresa.CrearComoCliente("Cliente Fuente Unica Cinco S.A.", "B10380285", false, null, null);
            var e1 = new Empresa("Empresa Fuente Unica Cuatro S.L.", "B10380293");
            var e2 = new Empresa("Empresa Fuente Unica Cinco S.L.", "B10380301");
            contexto.Empresas.AddRange(cli, e1, e2);
            await contexto.SaveChangesAsync();
            cliente = cli.Id; empresaUno = e1.Id; empresaDos = e2.Id;

            var subcontrata = Empresa.CrearComoSubcontrata("Subcontrata Fuente Unica Dos S.L.", "B10380319", NivelServicioSubcontrata.Gestionada.ToString());
            contexto.Empresas.Add(subcontrata);
            await contexto.SaveChangesAsync();
            subcontrataId = subcontrata.Id;

            contexto.RelacionesEmpresariales.Add(RelacionEmpresarial.Crear(subcontrataId, empresaUno, DateTime.UtcNow));
            await contexto.SaveChangesAsync();
        }

        await using (var contexto = CrearContexto())
        {
            var handler = new EditarSubcontrataCommandHandler(
                new EmpresaRepository(contexto), new RelacionEmpresarialRepository(contexto), contexto,
                CrearAlcanceConAccesoTotal(contexto), contexto);

            // Deseado: quitar empresaUno, añadir empresaDos y el cliente.
            var resultado = await handler.Handle(
                new EditarSubcontrataCommand(subcontrataId, "Subcontrata Fuente Unica Dos S.L.", "B10380319", [cliente], [empresaDos]),
                CancellationToken.None);

            resultado.EsExitoso.Should().BeTrue();
        }

        await using var verificacion = CrearContexto();
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
            Clientes: [new ClienteImportadoDto("Cliente Import Fuente Unica S.A.", "B10380327", false, YaExiste: false)],
            Empresas: [new EmpresaCombinadaImportadaDto("Empresa Import Fuente Unica S.L.", ["Cliente Import Fuente Unica S.A."], YaExiste: false)],
            Centros: [], Trabajadores: [], Advertencias: [], Omitidos: []);

        var primeraEjecucion = await EjecutarImportacionAsync(plan);
        primeraEjecucion.ClientesCreados.Should().Be(1);
        primeraEjecucion.EmpresasCreadas.Should().Be(1);

        var segundaEjecucion = await EjecutarImportacionAsync(plan);
        segundaEjecucion.ClientesCreados.Should().Be(0, "el cliente ya existe — la segunda ejecución no debe recrearlo");
        segundaEjecucion.EmpresasCreadas.Should().Be(0);

        await using var verificacion = CrearContexto();
        (await verificacion.RelacionesEmpresariales.CountAsync()).Should().Be(1,
            "ejecutar la misma importación dos veces no debe duplicar la relación");
    }

    /// <summary>
    /// Idempotencia INTRA-transacción: dos filas del mismo plan que resuelven
    /// a la misma empresa y al mismo cliente producen una sola arista, no un
    /// 23505 sobre el índice único parcial en el SaveChanges. La protege la
    /// lista en memoria del handler Y la comprobación del ChangeTracker en
    /// <c>AgregarSiNoVigenteAsync</c> — doble red, ambas medidas aquí.
    /// </summary>
    [Fact]
    public async Task EjecutarImportacionCombinadaCommand_dos_filas_del_mismo_par_en_un_plan_crean_una_sola_arista()
    {
        var plan = new PlanImportacionCombinadaDto(
            Clientes: [new ClienteImportadoDto("Cliente Repetido Fuente Unica S.A.", "B10380335", false, YaExiste: false)],
            Empresas:
            [
                new EmpresaCombinadaImportadaDto("Empresa Repetida Fuente Unica S.L.", ["Cliente Repetido Fuente Unica S.A."], YaExiste: false),
                new EmpresaCombinadaImportadaDto("Empresa Repetida Fuente Unica S.L.", ["Cliente Repetido Fuente Unica S.A."], YaExiste: false),
            ],
            Centros: [], Trabajadores: [], Advertencias: [], Omitidos: []);

        var resultado = await EjecutarImportacionAsync(plan);
        resultado.EmpresasCreadas.Should().Be(1, "la segunda fila es la misma razón social");

        await using var verificacion = CrearContexto();
        (await verificacion.RelacionesEmpresariales.CountAsync()).Should().Be(1,
            "dos filas del mismo par en un mismo plan no deben ni duplicar la arista ni reventar el índice único parcial");
    }

    /// <summary>
    /// El repositorio por sí solo — la segunda red de la propiedad de arriba,
    /// medible sin el handler: dos altas del mismo par dentro de la MISMA
    /// transacción (la primera aún sin guardar, invisible para una consulta)
    /// deben producir una sola fila. Sin la comprobación del ChangeTracker,
    /// esto revienta con 23505 en el SaveChanges.
    /// </summary>
    [Fact]
    public async Task AgregarSiNoVigenteAsync_dos_veces_el_mismo_par_en_una_transaccion_crea_una_sola_fila()
    {
        Guid proveedora, cliente;
        await using (var contexto = CrearContexto())
        {
            var p = new Empresa("Proveedora Del Par Repetido S.L.", "B10380343");
            var c = Empresa.CrearComoCliente("Cliente Del Par Repetido S.A.", "B10380350", false, null, null);
            contexto.Empresas.AddRange(p, c);
            await contexto.SaveChangesAsync();
            proveedora = p.Id; cliente = c.Id;
        }

        await using (var contexto = CrearContexto())
        {
            var repositorio = new RelacionEmpresarialRepository(contexto);
            var ahora = DateTime.UtcNow;

            (await repositorio.AgregarSiNoVigenteAsync(proveedora, cliente, ahora)).Should().BeTrue();
            (await repositorio.AgregarSiNoVigenteAsync(proveedora, cliente, ahora)).Should().BeFalse(
                "el segundo alta del mismo par en la misma transacción debe reconocer la primera aunque aún no esté guardada");

            await contexto.SaveChangesAsync();
        }

        await using var verificacion = CrearContexto();
        (await verificacion.RelacionesEmpresariales.CountAsync(r => r.ProveedoraId == proveedora && r.ClienteId == cliente))
            .Should().Be(1);
    }

    /// <summary>
    /// La clasificación que sostiene todo el diseño: Cliente real al eje
    /// Clientes, Empresa propia al suyo, y una contraparte soft-deleted (que
    /// el filtro global de Empresas oculta) a OPACAS — nunca clasificada por
    /// defecto, porque las opacas son exactamente lo que un diff no puede
    /// cerrar.
    /// </summary>
    [Fact]
    public async Task ObtenerContrapartesVigentesAsync_clasifica_y_aparta_las_opacas()
    {
        Guid subcontrataId, clienteVivo, empresaPropia, clienteEliminado;
        await using (var contexto = CrearContexto())
        {
            var s = Empresa.CrearComoSubcontrata("Subcontrata Clasificadora S.L.", "B10380368", NivelServicioSubcontrata.Gestionada.ToString());
            var cv = Empresa.CrearComoCliente("Cliente Vivo Clasificado S.A.", "B10380376", false, null, null);
            var ep = new Empresa("Empresa Propia Clasificada S.L.", "B10380384");
            var ce = Empresa.CrearComoCliente("Cliente Eliminado Clasificado S.A.", "B10380392", false, null, null);
            contexto.Empresas.AddRange(s, cv, ep, ce);
            await contexto.SaveChangesAsync();
            subcontrataId = s.Id; clienteVivo = cv.Id; empresaPropia = ep.Id; clienteEliminado = ce.Id;

            var ahora = DateTime.UtcNow;
            contexto.RelacionesEmpresariales.AddRange(
                RelacionEmpresarial.Crear(subcontrataId, clienteVivo, ahora),
                RelacionEmpresarial.Crear(subcontrataId, empresaPropia, ahora),
                RelacionEmpresarial.Crear(subcontrataId, clienteEliminado, ahora));
            await contexto.SaveChangesAsync();

            ce.MarcarComoEliminado(Guid.NewGuid());
            await contexto.SaveChangesAsync();
        }

        await using var lectura = CrearContexto();
        var contrapartes = await new RelacionEmpresarialRepository(lectura)
            .ObtenerContrapartesVigentesAsync(subcontrataId);

        contrapartes.ClienteIds.Should().BeEquivalentTo([clienteVivo]);
        contrapartes.EmpresaPropiaIds.Should().BeEquivalentTo([empresaPropia]);
        contrapartes.OpacaIds.Should().BeEquivalentTo([clienteEliminado],
            "una contraparte que la consulta de clasificación no devuelve cae en Opacas — jamás se clasifica por defecto");
    }

    private async Task<ResultadoImportacionCombinadaDto> EjecutarImportacionAsync(PlanImportacionCombinadaDto plan)
    {
        await using var contexto = CrearContexto();
        var handler = new EjecutarImportacionCombinadaCommandHandler(
            new EmpresaRepository(contexto),
            new RelacionEmpresarialRepository(contexto),
            new CentroRepository(contexto),
            new TrabajadorRepository(contexto),
            contexto, contexto, contexto, contexto);

        var resultado = await handler.Handle(new EjecutarImportacionCombinadaCommand(plan, ReemplazarExistentes: false), CancellationToken.None);
        return resultado.Valor;
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
