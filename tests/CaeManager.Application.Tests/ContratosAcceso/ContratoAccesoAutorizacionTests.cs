using CaeManager.Application.ContratosAcceso.Commands.ActualizarContratoAcceso;
using CaeManager.Application.ContratosAcceso.Commands.CerrarContratoAcceso;
using CaeManager.Application.ContratosAcceso.Commands.CrearContratoAcceso;
using CaeManager.Application.ContratosAcceso.Commands.EliminarContratoAcceso;
using CaeManager.Application.ContratosAcceso.Commands.ReabrirContratoAcceso;
using CaeManager.Application.ContratosAcceso.Queries.ObtenerContratoAccesoPorId;
using CaeManager.Application.ContratosAcceso.Queries.ObtenerContratosAcceso;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Documentos;
using CaeManager.Application.Tests.Plantillas;
using CaeManager.Domain.Centros;
using CaeManager.Domain.ContratosAcceso;
using CaeManager.Domain.Empresas;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.ContratosAcceso;

/// <summary>
/// Autoridad del contrato de acceso: la del Cliente empresarial titular del Centro, como en Proyecto
/// (ver ProyectoAutorizacionEscrituraTests). Cada escritura fuera de alcance falla con «no encontrado» y no guarda
/// nada; cada lectura fuera de alcance responde como si el contrato no existiera.
/// </summary>
public class ContratoAccesoAutorizacionTests
{
    private static readonly DateOnly Inicio = new(2026, 3, 1);

    private readonly Empresa _cliente = new("Cliente empresarial titular", "B10380186");
    private readonly Empresa _otroCliente = new("Otro Cliente empresarial", "B10380202");
    private readonly Empresa _contratista = new("Empresa contratista", "B10380194");
    private readonly ContratoAccesoRepositorioFalso _repositorio = new();
    private readonly CentrosQueryContextFalso _centros = new();
    private readonly EmpresasQueryContextFalso _empresas = new();
    private readonly UnitOfWorkFalso _unitOfWork = new();
    private readonly Centro _centro;
    private readonly Centro _centroDeOtroCliente;

    public ContratoAccesoAutorizacionTests()
    {
        _centro = new Centro(_cliente.Id, _cliente.Id, "Centro de trabajo del titular");
        _centroDeOtroCliente = new Centro(_otroCliente.Id, _otroCliente.Id, "Centro de trabajo de otro Cliente empresarial");
        _centros.ListaCentros.AddRange([_centro, _centroDeOtroCliente]);
        _empresas.ListaEmpresas.AddRange([_cliente, _otroCliente, _contratista]);
    }

    private static AlcanceDatosServiceFalso AlcanceSinAcceso(Guid? clienteVisible = null) =>
        new(tieneAccesoTotal: false, clienteIdsVisibles: clienteVisible is { } id ? [id] : []);

    private CrearContratoAccesoCommandHandler Crear(AlcanceDatosServiceFalso alcance) =>
        new(_repositorio, _centros, _empresas, alcance, _unitOfWork);

    private ContratoAcceso ContratoExistente(Guid? clienteEmpresarialId = null)
    {
        var contrato = ContratoAcceso.Crear(clienteEmpresarialId ?? _cliente.Id, _centro.Id, _contratista.Id, Inicio, null, "Nota");
        _repositorio.Agregar(contrato);
        return contrato;
    }

    // ── Alta ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Crear_con_el_Cliente_empresarial_visible_guarda_el_contrato()
    {
        var resultado = await Crear(AlcanceSinAcceso(_cliente.Id)).Handle(
            new CrearContratoAccesoCommand(_cliente.Id, _centro.Id, _contratista.Id, Inicio, null, null), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        var guardado = _repositorio.Contratos.Should().ContainSingle().Subject;
        guardado.Id.Should().Be(resultado.Valor);
        guardado.EmpresaContratistaId.Should().Be(_contratista.Id);
        _unitOfWork.VecesGuardado.Should().Be(1);
    }

    [Fact]
    public async Task Crear_con_un_Cliente_empresarial_fuera_de_alcance_responde_no_encontrado_sin_guardar()
    {
        var resultado = await Crear(AlcanceSinAcceso(_otroCliente.Id)).Handle(
            new CrearContratoAccesoCommand(_cliente.Id, _centro.Id, _contratista.Id, Inicio, null, null), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("ContratoAcceso.CentroNoEncontrado",
            "el mismo error que un Centro inexistente: no revela que el Cliente empresarial existe");
        _repositorio.Contratos.Should().BeEmpty();
        _unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Crear_con_un_Centro_inexistente_responde_el_mismo_no_encontrado()
    {
        var resultado = await Crear(AlcanceSinAcceso(_cliente.Id)).Handle(
            new CrearContratoAccesoCommand(_cliente.Id, Guid.NewGuid(), _contratista.Id, Inicio, null, null), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("ContratoAcceso.CentroNoEncontrado");
        _unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Crear_con_el_Centro_de_otro_Cliente_empresarial_falla_sin_guardar()
    {
        // Con acceso total: el rechazo no depende de la cartera sino de que el Centro no es del Cliente empresarial.
        var resultado = await Crear(new AlcanceDatosServiceFalso()).Handle(
            new CrearContratoAccesoCommand(_cliente.Id, _centroDeOtroCliente.Id, _contratista.Id, Inicio, null, null), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("ContratoAcceso.CentroNoPerteneceACliente");
        _repositorio.Contratos.Should().BeEmpty();
        _unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Crear_con_la_Empresa_contratista_igual_al_Cliente_empresarial_falla_sin_guardar()
    {
        var comando = new CrearContratoAccesoCommand(_cliente.Id, _centro.Id, _cliente.Id, Inicio, null, null);

        new CrearContratoAccesoCommandValidator().Validate(comando).IsValid.Should().BeFalse();
        var resultado = await Crear(new AlcanceDatosServiceFalso()).Handle(comando, CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("ContratoAcceso.ContratistaEsElCliente");
        _repositorio.Contratos.Should().BeEmpty();
        _unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Crear_con_una_Empresa_contratista_que_no_existe_en_el_Tenant_falla_sin_guardar()
    {
        var resultado = await Crear(new AlcanceDatosServiceFalso()).Handle(
            new CrearContratoAccesoCommand(_cliente.Id, _centro.Id, Guid.NewGuid(), Inicio, null, null), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("ContratoAcceso.ContratistaNoEncontrada");
        _repositorio.Contratos.Should().BeEmpty();
        _unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public void El_validador_rechaza_un_fin_anterior_al_inicio()
    {
        new CrearContratoAccesoCommandValidator()
            .Validate(new CrearContratoAccesoCommand(_cliente.Id, _centro.Id, _contratista.Id, Inicio, Inicio.AddDays(-1), null))
            .IsValid.Should().BeFalse();
    }

    // ── Escrituras sobre un contrato existente ────────────────────────────────────────

    [Fact]
    public async Task Actualizar_fuera_de_alcance_falla_sin_tocarlo()
    {
        var contrato = ContratoExistente();

        var resultado = await new ActualizarContratoAccesoCommandHandler(_repositorio, AlcanceSinAcceso(), _unitOfWork)
            .Handle(new ActualizarContratoAccesoCommand(contrato.Id, Inicio.AddDays(10), "Cambiada", contrato.Version), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("ContratoAcceso.NoEncontrado");
        contrato.FechaFin.Should().BeNull();
        contrato.Notas.Should().Be("Nota");
        _unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Cerrar_fuera_de_alcance_falla_sin_tocarlo()
    {
        var contrato = ContratoExistente();

        var resultado = await new CerrarContratoAccesoCommandHandler(_repositorio, AlcanceSinAcceso(), _unitOfWork)
            .Handle(new CerrarContratoAccesoCommand(contrato.Id, Inicio.AddDays(5)), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("ContratoAcceso.NoEncontrado");
        contrato.EstaAbierto.Should().BeTrue();
        _unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Reabrir_fuera_de_alcance_falla_sin_tocarlo()
    {
        var contrato = ContratoExistente();
        contrato.Cerrar(Inicio.AddDays(5));

        var resultado = await new ReabrirContratoAccesoCommandHandler(_repositorio, AlcanceSinAcceso(), _unitOfWork)
            .Handle(new ReabrirContratoAccesoCommand(contrato.Id), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("ContratoAcceso.NoEncontrado");
        contrato.EstaAbierto.Should().BeFalse();
        _unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Eliminar_fuera_de_alcance_falla_sin_tocarlo()
    {
        var contrato = ContratoExistente();

        var resultado = await new EliminarContratoAccesoCommandHandler(
                _repositorio, AlcanceSinAcceso(), _unitOfWork, new CurrentUserServiceFalso(Guid.NewGuid()))
            .Handle(new EliminarContratoAccesoCommand(contrato.Id), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("ContratoAcceso.NoEncontrado");
        contrato.EstaEliminado.Should().BeFalse();
        _unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Cerrar_reabrir_y_eliminar_dentro_del_alcance_guardan()
    {
        var contrato = ContratoExistente();
        var alcance = AlcanceSinAcceso(_cliente.Id);

        (await new CerrarContratoAccesoCommandHandler(_repositorio, alcance, _unitOfWork)
            .Handle(new CerrarContratoAccesoCommand(contrato.Id, Inicio.AddDays(5)), CancellationToken.None)).EsExitoso.Should().BeTrue();
        contrato.FechaCierreReal.Should().Be(Inicio.AddDays(5));

        (await new CerrarContratoAccesoCommandHandler(_repositorio, alcance, _unitOfWork)
            .Handle(new CerrarContratoAccesoCommand(contrato.Id, Inicio.AddDays(6)), CancellationToken.None))
            .Error.Codigo.Should().Be("ContratoAcceso.YaCerrado");

        (await new ReabrirContratoAccesoCommandHandler(_repositorio, alcance, _unitOfWork)
            .Handle(new ReabrirContratoAccesoCommand(contrato.Id), CancellationToken.None)).EsExitoso.Should().BeTrue();
        contrato.EstaAbierto.Should().BeTrue();

        var usuario = Guid.NewGuid();
        (await new EliminarContratoAccesoCommandHandler(_repositorio, alcance, _unitOfWork, new CurrentUserServiceFalso(usuario))
            .Handle(new EliminarContratoAccesoCommand(contrato.Id), CancellationToken.None)).EsExitoso.Should().BeTrue();
        contrato.EstaEliminado.Should().BeTrue();
        contrato.EliminadoPorUsuarioId.Should().Be(usuario);
        _unitOfWork.VecesGuardado.Should().Be(3);
    }

    // ── Lecturas ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ObtenerPorId_fuera_de_alcance_responde_null_y_dentro_devuelve_el_contrato()
    {
        var contrato = ContratoAcceso.Crear(_cliente.Id, _centro.Id, _contratista.Id, Inicio, null, "Nota");
        var contexto = new ContratosAccesoQueryContextFalso();
        contexto.ListaContratos.Add(contrato);

        (await new ObtenerContratoAccesoPorIdQueryHandler(contexto, AlcanceSinAcceso(_otroCliente.Id), new PoliticaNotaInternaFalsa(true))
            .Handle(new ObtenerContratoAccesoPorIdQuery(contrato.Id), CancellationToken.None)).Should().BeNull();

        var visible = await new ObtenerContratoAccesoPorIdQueryHandler(contexto, AlcanceSinAcceso(_cliente.Id), new PoliticaNotaInternaFalsa(true))
            .Handle(new ObtenerContratoAccesoPorIdQuery(contrato.Id), CancellationToken.None);
        visible!.Id.Should().Be(contrato.Id);
        visible.Notas.Should().Be("Nota");

        var sinNota = await new ObtenerContratoAccesoPorIdQueryHandler(contexto, AlcanceSinAcceso(_cliente.Id), new PoliticaNotaInternaFalsa(false))
            .Handle(new ObtenerContratoAccesoPorIdQuery(contrato.Id), CancellationToken.None);
        sinNota!.Notas.Should().BeNull("las notas siguen la política de la «Nota interna»");
    }

    [Fact]
    public async Task El_listado_se_acota_al_alcance_y_filtra_por_Centro_Empresa_contratista_y_vigencia()
    {
        var otraContratista = Guid.NewGuid();
        var vigente = ContratoAcceso.Crear(_cliente.Id, _centro.Id, _contratista.Id, Inicio, Inicio.AddDays(30), null);
        var caducado = ContratoAcceso.Crear(_cliente.Id, _centro.Id, _contratista.Id, Inicio.AddDays(-60), Inicio.AddDays(-1), null);
        var deOtraContratista = ContratoAcceso.Crear(_cliente.Id, _centro.Id, otraContratista, Inicio, null, null);
        var fueraDeAlcance = ContratoAcceso.Crear(_otroCliente.Id, _centroDeOtroCliente.Id, _contratista.Id, Inicio, null, null);
        var eliminado = ContratoAcceso.Crear(_cliente.Id, _centro.Id, _contratista.Id, Inicio, null, null);
        eliminado.MarcarComoEliminado(Guid.NewGuid());
        var contexto = new ContratosAccesoQueryContextFalso();
        contexto.ListaContratos.AddRange([vigente, caducado, deOtraContratista, fueraDeAlcance, eliminado]);
        var handler = new ObtenerContratosAccesoQueryHandler(contexto, AlcanceSinAcceso(_cliente.Id), new PoliticaNotaInternaFalsa(true));

        (await handler.Handle(new ObtenerContratosAccesoQuery(), CancellationToken.None))
            .Select(c => c.Id).Should().BeEquivalentTo([vigente.Id, caducado.Id, deOtraContratista.Id]);
        (await handler.Handle(new ObtenerContratosAccesoQuery(EmpresaContratistaId: _contratista.Id), CancellationToken.None))
            .Select(c => c.Id).Should().BeEquivalentTo([vigente.Id, caducado.Id]);
        (await handler.Handle(new ObtenerContratosAccesoQuery(CentroId: _centroDeOtroCliente.Id), CancellationToken.None))
            .Should().BeEmpty("el Centro de un Cliente empresarial fuera de alcance no abre sus contratos");
        (await handler.Handle(new ObtenerContratosAccesoQuery(_centro.Id, _contratista.Id, Inicio), CancellationToken.None))
            .Select(c => c.Id).Should().Equal([vigente.Id]);
    }
}
