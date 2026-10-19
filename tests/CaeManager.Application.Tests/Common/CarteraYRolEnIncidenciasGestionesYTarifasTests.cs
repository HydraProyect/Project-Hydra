using CaeManager.Application.Common;
using CaeManager.Application.Facturacion;
using CaeManager.Application.Facturacion.Commands.ActualizarTarifaCliente;
using CaeManager.Application.Facturacion.Commands.CrearTarifaCliente;
using CaeManager.Application.Facturacion.Commands.EliminarTarifaCliente;
using CaeManager.Application.Gestiones.Commands.CrearGestionesParaTrabajador;
using CaeManager.Application.Incidencias.Commands.CrearIncidencia;
using CaeManager.Application.Incidencias.Commands.EditarIncidencia;
using CaeManager.Application.Incidencias.Commands.EliminarIncidencia;
using CaeManager.Application.Incidencias.Commands.EliminarIncidencias;
using CaeManager.Application.Incidencias.Commands.MarcarResueltaIncidencia;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Documentos;
using CaeManager.Application.Tests.Gestiones;
using CaeManager.Application.Tests.Incidencias;
using CaeManager.Application.Tests.Plantillas;
using CaeManager.Application.Tests.Reportes;
using CaeManager.Application.Tests.TiposDocumento;
using CaeManager.Domain.Asignaciones;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Comunicaciones;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Facturacion;
using CaeManager.Domain.Incidencias;
using FluentAssertions;
using Xunit;
using CurrentUserServiceFalso = CaeManager.Application.Tests.CurrentUserServiceFalso;

namespace CaeManager.Application.Tests.Common;

/// <summary>
/// Seguimiento de la Asignación de Cartera en escrituras: las Incidencias se acotan por la
/// cartera de gestión de su Centro, CrearGestionesParaTrabajador solo crea Gestiones en Centros
/// de esa cartera, y las Tarifas (plano Comercial, exentas de cartera) exigen en el propio
/// comando rol Administrador o Dirección CAE. Fuera de cartera, la fila «no existe» con el mismo
/// código que un Id inexistente; alcance null (Administrador, Dirección CAE) = sin restricción.
/// </summary>
public class CarteraYRolEnIncidenciasGestionesYTarifasTests
{
    private static readonly DateOnly Hoy = new(2026, 10, 17);

    private static AlcanceDatosServiceFalso CarteraDe(params Guid[] centros) =>
        new(tieneAccesoTotal: false, centroIdsVisibles: centros);

    private static Incidencia IncidenciaEn(Guid centroId) =>
        new(centroId, null, default, default, Hoy, "Caída al mismo nivel en el acceso");

    /// <summary>
    /// Centro visible para LEER pero fuera del alcance de GESTIÓN (lo que la implementación real
    /// devuelve a un usuario de portal): distingue CentroParaGestionVisibleAsync de CentroVisibleAsync.
    /// </summary>
    private static AlcanceDatosServiceFalso SoloLecturaDe(Guid centro) =>
        new(tieneAccesoTotal: false, centroIdsVisibles: [centro], centroIdsParaGestion: []);

    // ---- Incidencias

    [Theory]
    [InlineData("crear")]
    [InlineData("editar")]
    [InlineData("eliminar")]
    [InlineData("eliminar-lote")]
    [InlineData("marcar-resuelta")]
    public async Task Un_centro_visible_solo_para_leer_no_basta_para_escribir_sus_incidencias(string operacion)
    {
        var centro = new Centro(Guid.NewGuid(), Guid.NewGuid(), "Nave 3");
        var centros = new CentrosQueryContextFalso();
        centros.ListaCentros.Add(centro);
        var incidencia = IncidenciaEn(centro.Id);
        var repositorio = new IncidenciaRepositorioFalso();
        repositorio.Incidencias.Add(incidencia);
        var unitOfWork = new UnitOfWorkFalso();
        var alcance = SoloLecturaDe(centro.Id);
        var usuario = new CurrentUserServiceFalso(Guid.NewGuid());

        var codigo = operacion switch
        {
            "crear" => (await new CrearIncidenciaCommandHandler(repositorio, centros, new TrabajadoresQueryContextFalso(), alcance, unitOfWork)
                .Handle(new CrearIncidenciaCommand(centro.Id, null, default, default, Hoy, "Caída"), CancellationToken.None)).Error.Codigo,
            "editar" => (await new EditarIncidenciaCommandHandler(repositorio, new TrabajadoresQueryContextFalso(), alcance, unitOfWork)
                .Handle(new EditarIncidenciaCommand(incidencia.Id, null, default, default, Hoy, "Texto nuevo"), CancellationToken.None)).Error.Codigo,
            "eliminar" => (await new EliminarIncidenciaCommandHandler(repositorio, unitOfWork, usuario, alcance)
                .Handle(new EliminarIncidenciaCommand(incidencia.Id), CancellationToken.None)).Error.Codigo,
            "eliminar-lote" => (await new EliminarIncidenciasCommandHandler(repositorio, unitOfWork, usuario, alcance)
                .Handle(new EliminarIncidenciasCommand([incidencia.Id]), CancellationToken.None)).Valor.Eliminados == 0 ? "ninguna" : "alguna",
            _ => (await new MarcarResueltaIncidenciaCommandHandler(repositorio, alcance, unitOfWork)
                .Handle(new MarcarResueltaIncidenciaCommand(incidencia.Id, true), CancellationToken.None)).Error.Codigo,
        };

        codigo.Should().BeOneOf("Incidencia.CentroNoEncontrado", "Incidencia.NoEncontrada", "ninguna");
        repositorio.Incidencias.Should().ContainSingle();
        incidencia.EstaEliminado.Should().BeFalse();
        incidencia.Resuelta.Should().BeFalse();
        incidencia.Descripcion.Should().NotBe("Texto nuevo");
    }

    /// <summary>
    /// Fuera de cartera con una Version obsoleta: «no existe», no un conflicto de concurrencia. La
    /// cartera va antes de ConcurrenciaOptimista para no confirmar que la Incidencia existe.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Editar_una_incidencia_con_version_obsoleta_solo_da_conflicto_dentro_de_la_cartera(bool enCartera)
    {
        var incidencia = IncidenciaEn(Guid.NewGuid());
        var repositorio = new IncidenciaRepositorioFalso();
        repositorio.Incidencias.Add(incidencia);
        var handler = new EditarIncidenciaCommandHandler(
            repositorio, new TrabajadoresQueryContextFalso(),
            CarteraDe(enCartera ? incidencia.CentroId : Guid.NewGuid()), new UnitOfWorkFalso());

        var resultado = await handler.Handle(
            new EditarIncidenciaCommand(incidencia.Id, null, default, default, Hoy, "Texto nuevo", Version: Guid.NewGuid()),
            CancellationToken.None);

        // Control: dentro de la cartera la misma Version obsoleta sí es un conflicto, así que fuera
        // de ella el «no existe» lo decide la cartera y no un orden casual de las comprobaciones.
        resultado.Error.Codigo.Should().Be(enCartera ? ConcurrenciaOptimista.CodigoConflicto : "Incidencia.NoEncontrada");
        incidencia.Descripcion.Should().NotBe("Texto nuevo");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Crear_una_incidencia_respeta_la_cartera_de_gestion_de_su_centro(bool enCartera)
    {
        var centro = new Centro(Guid.NewGuid(), Guid.NewGuid(), "Nave 3");
        var centros = new CentrosQueryContextFalso();
        centros.ListaCentros.Add(centro);
        var repositorio = new IncidenciaRepositorioFalso();
        var unitOfWork = new UnitOfWorkFalso();
        var handler = new CrearIncidenciaCommandHandler(
            repositorio, centros, new TrabajadoresQueryContextFalso(),
            CarteraDe(enCartera ? centro.Id : Guid.NewGuid()), unitOfWork);

        var resultado = await handler.Handle(
            new CrearIncidenciaCommand(centro.Id, null, default, default, Hoy, "Caída"), CancellationToken.None);

        if (enCartera)
        {
            resultado.EsExitoso.Should().BeTrue();
            repositorio.Incidencias.Should().ContainSingle();
        }
        else
        {
            resultado.Error.Codigo.Should().Be("Incidencia.CentroNoEncontrado");
            repositorio.Incidencias.Should().BeEmpty();
            unitOfWork.VecesGuardado.Should().Be(0);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Editar_una_incidencia_respeta_la_cartera_de_gestion_de_su_centro(bool enCartera)
    {
        var incidencia = IncidenciaEn(Guid.NewGuid());
        var repositorio = new IncidenciaRepositorioFalso();
        repositorio.Incidencias.Add(incidencia);
        var unitOfWork = new UnitOfWorkFalso();
        var handler = new EditarIncidenciaCommandHandler(
            repositorio, new TrabajadoresQueryContextFalso(),
            CarteraDe(enCartera ? incidencia.CentroId : Guid.NewGuid()), unitOfWork);

        var resultado = await handler.Handle(
            new EditarIncidenciaCommand(incidencia.Id, null, default, default, Hoy, "Texto nuevo"), CancellationToken.None);

        if (enCartera)
        {
            resultado.EsExitoso.Should().BeTrue();
            incidencia.Descripcion.Should().Be("Texto nuevo");
        }
        else
        {
            resultado.Error.Codigo.Should().Be("Incidencia.NoEncontrada");
            incidencia.Descripcion.Should().NotBe("Texto nuevo");
            unitOfWork.VecesGuardado.Should().Be(0);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Eliminar_una_incidencia_respeta_la_cartera_de_gestion_de_su_centro(bool enCartera)
    {
        var incidencia = IncidenciaEn(Guid.NewGuid());
        var repositorio = new IncidenciaRepositorioFalso();
        repositorio.Incidencias.Add(incidencia);
        var unitOfWork = new UnitOfWorkFalso();
        var handler = new EliminarIncidenciaCommandHandler(
            repositorio, unitOfWork, new CurrentUserServiceFalso(Guid.NewGuid()),
            CarteraDe(enCartera ? incidencia.CentroId : Guid.NewGuid()));

        var resultado = await handler.Handle(new EliminarIncidenciaCommand(incidencia.Id), CancellationToken.None);

        incidencia.EstaEliminado.Should().Be(enCartera);
        if (!enCartera)
        {
            resultado.Error.Codigo.Should().Be("Incidencia.NoEncontrada");
            unitOfWork.VecesGuardado.Should().Be(0);
        }
    }

    [Fact]
    public async Task Eliminar_en_lote_solo_elimina_las_incidencias_de_centros_en_cartera_y_cuenta_las_demas_como_inexistentes()
    {
        var dentro = IncidenciaEn(Guid.NewGuid());
        var fuera = IncidenciaEn(Guid.NewGuid());
        var repositorio = new IncidenciaRepositorioFalso();
        repositorio.Incidencias.AddRange([dentro, fuera]);
        var handler = new EliminarIncidenciasCommandHandler(
            repositorio, new UnitOfWorkFalso(), new CurrentUserServiceFalso(Guid.NewGuid()), CarteraDe(dentro.CentroId));

        var resultado = await handler.Handle(new EliminarIncidenciasCommand([dentro.Id, fuera.Id]), CancellationToken.None);

        resultado.Valor.Eliminados.Should().Be(1);
        resultado.Valor.Errores.Should().ContainSingle().Which.Should().Be("Una incidencia ya no existía.");
        dentro.EstaEliminado.Should().BeTrue();
        fuera.EstaEliminado.Should().BeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Marcar_resuelta_una_incidencia_respeta_la_cartera_de_gestion_de_su_centro(bool enCartera)
    {
        var incidencia = IncidenciaEn(Guid.NewGuid());
        var repositorio = new IncidenciaRepositorioFalso();
        repositorio.Incidencias.Add(incidencia);
        var unitOfWork = new UnitOfWorkFalso();
        var handler = new MarcarResueltaIncidenciaCommandHandler(
            repositorio, CarteraDe(enCartera ? incidencia.CentroId : Guid.NewGuid()), unitOfWork);

        var resultado = await handler.Handle(new MarcarResueltaIncidenciaCommand(incidencia.Id, true), CancellationToken.None);

        incidencia.Resuelta.Should().Be(enCartera);
        if (!enCartera)
        {
            resultado.Error.Codigo.Should().Be("Incidencia.NoEncontrada");
            unitOfWork.VecesGuardado.Should().Be(0);
        }
    }

    [Fact]
    public async Task Con_acceso_total_las_incidencias_no_tienen_recorte_de_cartera()
    {
        var incidencia = IncidenciaEn(Guid.NewGuid());
        var repositorio = new IncidenciaRepositorioFalso();
        repositorio.Incidencias.Add(incidencia);
        var handler = new MarcarResueltaIncidenciaCommandHandler(repositorio, new AlcanceDatosServiceFalso(), new UnitOfWorkFalso());

        var resultado = await handler.Handle(new MarcarResueltaIncidenciaCommand(incidencia.Id, true), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        incidencia.Resuelta.Should().BeTrue();
    }

    // ---- CrearGestionesParaTrabajador

    [Fact]
    public async Task Crear_gestiones_para_un_trabajador_solo_las_crea_en_los_centros_de_la_cartera_de_gestion()
    {
        var (handler, gestiones, trabajadorId, tipoId, centroEnCartera, _) = CrearGestiones(enCartera: true);

        var resultado = await handler.Handle(new CrearGestionesParaTrabajadorCommand(trabajadorId, tipoId), CancellationToken.None);

        resultado.Valor.Creadas.Should().Be(1);
        gestiones.Gestiones.Should().ContainSingle().Which.CentroId.Should().Be(centroEnCartera);
    }

    [Fact]
    public async Task Crear_gestiones_para_un_trabajador_sin_ningun_centro_en_la_cartera_no_crea_ninguna()
    {
        var (handler, gestiones, trabajadorId, tipoId, _, unitOfWork) = CrearGestiones(enCartera: false);

        var resultado = await handler.Handle(new CrearGestionesParaTrabajadorCommand(trabajadorId, tipoId), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("Gestion.SinCentros");
        gestiones.Gestiones.Should().BeEmpty();
        unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Crear_gestiones_en_centros_visibles_solo_para_leer_no_crea_ninguna()
    {
        var (handler, gestiones, trabajadorId, tipoId, _, unitOfWork) = CrearGestiones(enCartera: true, soloLectura: true);

        var resultado = await handler.Handle(new CrearGestionesParaTrabajadorCommand(trabajadorId, tipoId), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("Gestion.SinCentros");
        gestiones.Gestiones.Should().BeEmpty();
        unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Con_acceso_total_crear_gestiones_las_crea_en_todos_los_centros_del_trabajador()
    {
        var (handler, gestiones, trabajadorId, tipoId, _, _) = CrearGestiones(enCartera: true, accesoTotal: true);

        var resultado = await handler.Handle(new CrearGestionesParaTrabajadorCommand(trabajadorId, tipoId), CancellationToken.None);

        resultado.Valor.Creadas.Should().Be(2);
        gestiones.Gestiones.Should().HaveCount(2);
    }

    /// <summary>Un Trabajador visible asignado a dos Centros, uno de ellos de otro Cliente empresarial fuera de la cartera.</summary>
    private static (CrearGestionesParaTrabajadorCommandHandler, GestionRepositorioFalso, Guid TrabajadorId, Guid TipoId, Guid CentroEnCartera, UnitOfWorkFalso)
        CrearGestiones(bool enCartera, bool accesoTotal = false, bool soloLectura = false)
    {
        var trabajadorId = Guid.NewGuid();
        var centroEnCartera = Guid.NewGuid();
        var centroAjeno = Guid.NewGuid();
        var asignaciones = new AsignacionesQueryContextFalso();
        asignaciones.ListaAsignaciones.AddRange([new Asignacion(trabajadorId, centroEnCartera, Hoy), new Asignacion(trabajadorId, centroAjeno, Hoy)]);
        var tipo = new TipoDocumento("Formación en altura", 12, false, 1, AmbitoAplicacion.Trabajador);
        var tipos = new TipoDocumentoRepositorioFalso();
        tipos.Tipos.Add(tipo);
        var gestiones = new GestionRepositorioFalso();
        var unitOfWork = new UnitOfWorkFalso();
        var alcance = accesoTotal
            ? new AlcanceDatosServiceFalso()
            : new AlcanceDatosServiceFalso(
                tieneAccesoTotal: false, trabajadorIdsVisibles: [trabajadorId],
                centroIdsVisibles: enCartera ? [centroEnCartera] : [Guid.NewGuid()],
                centroIdsParaGestion: soloLectura ? [] : null);
        var handler = new CrearGestionesParaTrabajadorCommandHandler(
            asignaciones, tipos, gestiones, new DetallesSugerenciaVacios(), new Comunicaciones.ComunicacionesQueryContextFalso(), alcance,
            new CurrentUserServiceFalso(Guid.NewGuid()), unitOfWork);
        return (handler, gestiones, trabajadorId, tipo.Id, centroEnCartera, unitOfWork);
    }

    private sealed class DetallesSugerenciaVacios : IDetalleSugerenciaGestionCorreoRepository
    {
        public Task<DetalleSugerenciaGestionCorreo?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult<DetalleSugerenciaGestionCorreo?>(null);
    }

    // ---- Tarifas (plano Comercial: por rol, no por cartera)

    public static TheoryData<string?, bool> Roles => new()
    {
        { "Administrador", true },
        { "DireccionCae", true },
        { "CoordinadorCae", false },
        { "GestorCae", false },
        { "Consulta", false },
        { "Cliente", false },
        { null, false },
    };

    [Theory]
    [MemberData(nameof(Roles))]
    public async Task Crear_una_tarifa_exige_rol_administrador_o_direccion_cae(string? rol, bool permitido)
    {
        var cliente = Empresa.CrearComoCliente("Cliente empresarial S.A.", "B12345674", false, null, null);
        var empresas = new EmpresasQueryContextFalso();
        empresas.ListaEmpresas.Add(cliente);
        var repositorio = new TarifasFalsas();
        var handler = new CrearTarifaClienteCommandHandler(
            repositorio, empresas, new CurrentUserServiceFalso(Guid.NewGuid(), rol), new UnitOfWorkFalso());

        var resultado = await handler.Handle(
            new CrearTarifaClienteCommand(cliente.Id, default, 10m), CancellationToken.None);

        resultado.EsExitoso.Should().Be(permitido);
        repositorio.Tarifas.Should().HaveCount(permitido ? 1 : 0);
        if (!permitido) resultado.Error.Codigo.Should().Be("TarifaCliente.SinPermiso");
    }

    [Theory]
    [MemberData(nameof(Roles))]
    public async Task Actualizar_una_tarifa_exige_rol_administrador_o_direccion_cae(string? rol, bool permitido)
    {
        var tarifa = TarifaCliente.Crear(Guid.NewGuid(), default, 10m, "EUR");
        var repositorio = new TarifasFalsas();
        repositorio.Tarifas.Add(tarifa);
        var handler = new ActualizarTarifaClienteCommandHandler(
            repositorio, new CurrentUserServiceFalso(Guid.NewGuid(), rol), new UnitOfWorkFalso());

        var resultado = await handler.Handle(new ActualizarTarifaClienteCommand(tarifa.Id, 25m, "EUR"), CancellationToken.None);

        resultado.EsExitoso.Should().Be(permitido);
        tarifa.PrecioUnitario.Should().Be(permitido ? 25m : 10m);
        if (!permitido) resultado.Error.Codigo.Should().Be("TarifaCliente.SinPermiso");
    }

    [Theory]
    [MemberData(nameof(Roles))]
    public async Task Eliminar_una_tarifa_exige_rol_administrador_o_direccion_cae(string? rol, bool permitido)
    {
        var tarifa = TarifaCliente.Crear(Guid.NewGuid(), default, 10m, "EUR");
        var repositorio = new TarifasFalsas();
        repositorio.Tarifas.Add(tarifa);
        var handler = new EliminarTarifaClienteCommandHandler(
            repositorio, new UnitOfWorkFalso(), new CurrentUserServiceFalso(Guid.NewGuid(), rol));

        var resultado = await handler.Handle(new EliminarTarifaClienteCommand(tarifa.Id), CancellationToken.None);

        resultado.EsExitoso.Should().Be(permitido);
        tarifa.EstaEliminado.Should().Be(permitido);
        if (!permitido) resultado.Error.Should().Be(AutorizacionTarifasCliente.SinPermiso);
    }

    private sealed class TarifasFalsas : ITarifaClienteRepository
    {
        public List<TarifaCliente> Tarifas { get; } = [];

        public Task<TarifaCliente?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(Tarifas.FirstOrDefault(t => t.Id == id));

        public Task<List<TarifaCliente>> ObtenerPorClienteAsync(Guid clienteId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Tarifas.Where(t => t.ClienteId == clienteId).ToList());

        public Task<bool> ExisteParaConceptoAsync(Guid clienteId, ConceptoFacturable concepto, Guid? excluirId = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(Tarifas.Any(t => t.ClienteId == clienteId && t.Concepto == concepto && t.Id != excluirId));

        public void Agregar(TarifaCliente tarifa) => Tarifas.Add(tarifa);

        public void Eliminar(TarifaCliente tarifa) => Tarifas.Remove(tarifa);
    }
}
