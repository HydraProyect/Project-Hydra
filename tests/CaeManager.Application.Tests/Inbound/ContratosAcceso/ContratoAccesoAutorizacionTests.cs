using CaeManager.Application.Inbound.ContratosAcceso;
using CaeManager.Application.Inbound.ContratosAcceso.Commands.ActualizarContratoAcceso;
using CaeManager.Application.Inbound.ContratosAcceso.Commands.CancelarContratoAcceso;
using CaeManager.Application.Inbound.ContratosAcceso.Commands.CerrarContratoAcceso;
using CaeManager.Application.Inbound.ContratosAcceso.Commands.CrearContratoAcceso;
using CaeManager.Application.Inbound.ContratosAcceso.Commands.EliminarContratoAcceso;
using CaeManager.Application.Inbound.ContratosAcceso.Commands.FijarCondicionesContratoAcceso;
using CaeManager.Application.Inbound.ContratosAcceso.Commands.RenovarContratoAcceso;
using CaeManager.Application.Inbound.ContratosAcceso.EvidenciasInbound;
using CaeManager.Application.Inbound.ContratosAcceso.Queries.ObtenerContratoAccesoPorId;
using CaeManager.Application.Inbound.ContratosAcceso.Queries.ObtenerContratosAcceso;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Documentos;
using CaeManager.Application.Tests.Integraciones;
using CaeManager.Application.Tests.Plantillas;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Inbound.ContratosAcceso;
using CaeManager.Domain.Inbound.EvidenciasInbound;
using CaeManager.Domain.Empresas;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Inbound.ContratosAcceso;

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

    private readonly Guid _usuario = Guid.NewGuid();
    private readonly List<EvidenciaInbound> _evidencias = [];

    /// <summary>Hoy es dos días antes del inicio del contrato: Cancelar todavía cabe.</summary>
    private static readonly TimeProvider AntesDelInicio = new RelojFijo(new DateTimeOffset(2026, 2, 27, 9, 0, 0, TimeSpan.Zero));

    private ActualizarContratoAccesoCommandHandler Actualizar(AlcanceDatosServiceFalso alcance) =>
        new(_repositorio, alcance, _unitOfWork, new CurrentUserServiceFalso(_usuario), AntesDelInicio);

    private RenovarContratoAccesoCommandHandler Renovar(AlcanceDatosServiceFalso alcance, TimeProvider? reloj = null) =>
        new(_repositorio, alcance, _unitOfWork, new CurrentUserServiceFalso(_usuario), reloj ?? AntesDelInicio);

    private CancelarContratoAccesoCommandHandler Cancelar(AlcanceDatosServiceFalso alcance, TimeProvider? reloj = null) =>
        new(_repositorio, new EvidenciasFalsas(_evidencias), alcance, _unitOfWork, reloj ?? AntesDelInicio);

    private FijarCondicionesContratoAccesoCommandHandler FijarCondiciones(AlcanceDatosServiceFalso alcance) =>
        new(_repositorio, alcance, _unitOfWork);

    private static readonly CondicionesContratoAccesoDatos ConCupo =
        new(TipoContratoAcceso.Temporal, PropiaActividadContrato.NoLoSe, 1, 8, 2, "Aforo del muelle");

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
    public async Task Crear_con_el_Centro_de_un_Cliente_empresarial_que_no_ves_responde_no_encontrado_sin_guardar()
    {
        // El actor ve al Cliente empresarial del comando pero no al titular del Centro: «no pertenece» confirmaría que el
        // Centro existe.
        var resultado = await Crear(AlcanceSinAcceso(_cliente.Id)).Handle(
            new CrearContratoAccesoCommand(_cliente.Id, _centroDeOtroCliente.Id, _contratista.Id, Inicio, null, null), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("ContratoAcceso.CentroNoEncontrado");
        _repositorio.Contratos.Should().BeEmpty();
        _unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Crear_con_el_Centro_de_otro_Cliente_empresarial_visible_responde_no_pertenece()
    {
        var alcance = new AlcanceDatosServiceFalso(tieneAccesoTotal: false, clienteIdsVisibles: [_cliente.Id, _otroCliente.Id]);

        var resultado = await Crear(alcance).Handle(
            new CrearContratoAccesoCommand(_cliente.Id, _centroDeOtroCliente.Id, _contratista.Id, Inicio, null, null), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("ContratoAcceso.CentroNoPerteneceACliente");
        _unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Crear_con_la_Empresa_contratista_igual_al_Cliente_empresarial_responde_el_mismo_no_encontrado()
    {
        var comando = new CrearContratoAccesoCommand(_cliente.Id, _centro.Id, _cliente.Id, Inicio, null, null);

        new CrearContratoAccesoCommandValidator().Validate(comando).IsValid.Should().BeFalse();
        var resultado = await Crear(new AlcanceDatosServiceFalso()).Handle(comando, CancellationToken.None);

        resultado.Error.Codigo.Should().Be("ContratoAcceso.CentroNoEncontrado");
        _repositorio.Contratos.Should().BeEmpty();
        _unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Crear_con_una_Empresa_contratista_que_no_existe_en_el_Tenant_responde_el_mismo_no_encontrado()
    {
        var resultado = await Crear(new AlcanceDatosServiceFalso()).Handle(
            new CrearContratoAccesoCommand(_cliente.Id, _centro.Id, Guid.NewGuid(), Inicio, null, null), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("ContratoAcceso.CentroNoEncontrado",
            "el mismo error que un Centro inexistente: no revela qué Empresas hay en el Tenant");
        _repositorio.Contratos.Should().BeEmpty();
        _unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Crear_con_una_Empresa_contratista_dada_de_baja_responde_el_mismo_no_encontrado()
    {
        _contratista.MarcarComoEliminado(Guid.NewGuid());

        var resultado = await Crear(new AlcanceDatosServiceFalso()).Handle(
            new CrearContratoAccesoCommand(_cliente.Id, _centro.Id, _contratista.Id, Inicio, null, null), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("ContratoAcceso.CentroNoEncontrado");
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

        var resultado = await Actualizar(AlcanceSinAcceso())
            .Handle(new ActualizarContratoAccesoCommand(contrato.Id, Inicio.AddDays(10), "Cambiada", contrato.Version), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("ContratoAcceso.NoEncontrado");
        contrato.FechaFin.Should().BeNull();
        contrato.Notas.Should().Be("Nota");
        _unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Actualizar_dentro_del_alcance_cambia_fin_y_notas_y_guarda()
    {
        var contrato = ContratoExistente();

        var resultado = await Actualizar(AlcanceSinAcceso(_cliente.Id))
            .Handle(new ActualizarContratoAccesoCommand(contrato.Id, Inicio.AddDays(10), "Cambiada", contrato.Version), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        contrato.FechaFin.Should().Be(Inicio.AddDays(10));
        contrato.Notas.Should().Be("Cambiada");
        var cambio = contrato.HistorialVigencia.Should().ContainSingle().Subject;
        cambio.Origen.Should().Be(OrigenCambioVigencia.Edicion);
        cambio.RegistradoPorUsuarioId.Should().Be(_usuario, "el historial guarda quién cambió el fin");
        _unitOfWork.VecesGuardado.Should().Be(1);
    }

    [Fact]
    public async Task Actualizar_sin_usuario_no_guarda()
    {
        var contrato = ContratoExistente();

        var resultado = await new ActualizarContratoAccesoCommandHandler(
                _repositorio, AlcanceSinAcceso(_cliente.Id), _unitOfWork, new CurrentUserServiceFalso(null), AntesDelInicio)
            .Handle(new ActualizarContratoAccesoCommand(contrato.Id, Inicio.AddDays(10), "Cambiada", contrato.Version), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        contrato.FechaFin.Should().BeNull();
        _unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Actualizar_con_un_fin_anterior_al_inicio_responde_FechaFinInvalida_sin_guardar()
    {
        var contrato = ContratoExistente();

        var resultado = await Actualizar(AlcanceSinAcceso(_cliente.Id))
            .Handle(new ActualizarContratoAccesoCommand(contrato.Id, Inicio.AddDays(-1), "Cambiada", contrato.Version), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("ContratoAcceso.FechaFinInvalida");
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
    public async Task Cerrar_un_contrato_pendiente_de_confirmar_responde_TransicionNoPermitida_sin_guardar()
    {
        var contrato = ContratoAcceso.Crear(_cliente.Id, _centro.Id, _contratista.Id, Inicio, null, null, pendienteDeConfirmar: true);
        _repositorio.Agregar(contrato);

        var resultado = await new CerrarContratoAccesoCommandHandler(_repositorio, AlcanceSinAcceso(_cliente.Id), _unitOfWork)
            .Handle(new CerrarContratoAccesoCommand(contrato.Id, Inicio.AddDays(5)), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("ContratoAcceso.TransicionNoPermitida");
        contrato.Estado.Should().Be(EstadoContratoAcceso.PendienteDeConfirmar);
        contrato.FechaCierreReal.Should().BeNull();
        _unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Renovar_fuera_de_alcance_falla_sin_tocarlo()
    {
        var contrato = ContratoConFin();

        var resultado = await Renovar(AlcanceSinAcceso())
            .Handle(new RenovarContratoAccesoCommand(contrato.Id, Inicio.AddDays(60), contrato.Version), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("ContratoAcceso.NoEncontrado");
        contrato.FechaFin.Should().Be(Inicio.AddDays(30));
        contrato.HistorialVigencia.Should().BeEmpty();
        _unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Renovar_dentro_del_alcance_amplia_el_fin_lo_deja_en_el_historial_y_guarda()
    {
        var contrato = ContratoConFin();

        var resultado = await Renovar(AlcanceSinAcceso(_cliente.Id))
            .Handle(new RenovarContratoAccesoCommand(contrato.Id, Inicio.AddDays(60), contrato.Version), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        contrato.FechaFin.Should().Be(Inicio.AddDays(60));
        contrato.Estado.Should().Be(EstadoContratoAcceso.Activo);
        var cambio = contrato.HistorialVigencia.Should().ContainSingle().Subject;
        cambio.Origen.Should().Be(OrigenCambioVigencia.Renovacion);
        cambio.RegistradoPorUsuarioId.Should().Be(_usuario);
        cambio.RegistradoEnUtc.Should().Be(AntesDelInicio.GetUtcNow().UtcDateTime);
        _unitOfWork.VecesGuardado.Should().Be(1);
    }

    [Fact]
    public async Task Renovar_a_un_fin_no_posterior_responde_FechaFinInvalida_sin_guardar()
    {
        var contrato = ContratoConFin();

        var resultado = await Renovar(AlcanceSinAcceso(_cliente.Id))
            .Handle(new RenovarContratoAccesoCommand(contrato.Id, Inicio.AddDays(30), contrato.Version), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("ContratoAcceso.FechaFinInvalida");
        contrato.HistorialVigencia.Should().BeEmpty();
        _unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Renovar_un_contrato_finalizado_responde_TransicionNoPermitida_sin_guardar()
    {
        var contrato = ContratoConFin();
        contrato.Finalizar(Inicio.AddDays(5));

        var resultado = await Renovar(AlcanceSinAcceso(_cliente.Id))
            .Handle(new RenovarContratoAccesoCommand(contrato.Id, Inicio.AddDays(60), contrato.Version), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("ContratoAcceso.TransicionNoPermitida");
        contrato.FechaFin.Should().Be(Inicio.AddDays(30));
        _unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Renovar_sin_usuario_no_guarda()
    {
        var contrato = ContratoConFin();

        var resultado = await new RenovarContratoAccesoCommandHandler(
                _repositorio, AlcanceSinAcceso(_cliente.Id), _unitOfWork, new CurrentUserServiceFalso(null), AntesDelInicio)
            .Handle(new RenovarContratoAccesoCommand(contrato.Id, Inicio.AddDays(60), contrato.Version), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        contrato.FechaFin.Should().Be(Inicio.AddDays(30));
        _unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Cancelar_fuera_de_alcance_falla_sin_tocarlo()
    {
        var contrato = ContratoExistente();

        var resultado = await Cancelar(AlcanceSinAcceso())
            .Handle(new CancelarContratoAccesoCommand(contrato.Id, contrato.Version), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("ContratoAcceso.NoEncontrado");
        contrato.Estado.Should().Be(EstadoContratoAcceso.Activo);
        _unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Cancelar_antes_del_inicio_y_sin_actividad_lo_cancela_y_guarda()
    {
        var contrato = ContratoExistente();

        var resultado = await Cancelar(AlcanceSinAcceso(_cliente.Id))
            .Handle(new CancelarContratoAccesoCommand(contrato.Id, contrato.Version), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        contrato.Estado.Should().Be(EstadoContratoAcceso.Cancelado);
        contrato.EstaAbierto.Should().BeFalse();
        _unitOfWork.VecesGuardado.Should().Be(1);
    }

    [Fact]
    public async Task Cancelar_con_una_copia_de_evidencia_del_contrato_responde_TransicionNoPermitida_sin_guardar()
    {
        var contrato = ContratoExistente();
        _evidencias.Add(EvidenciaDe(Guid.NewGuid()));
        _evidencias.Add(EvidenciaDe(contrato.Id));

        var resultado = await Cancelar(AlcanceSinAcceso(_cliente.Id))
            .Handle(new CancelarContratoAccesoCommand(contrato.Id, contrato.Version), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("ContratoAcceso.TransicionNoPermitida");
        contrato.Estado.Should().Be(EstadoContratoAcceso.Activo);
        _unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Una_evidencia_de_otro_contrato_no_impide_cancelar()
    {
        var contrato = ContratoExistente();
        _evidencias.Add(EvidenciaDe(Guid.NewGuid()));

        (await Cancelar(AlcanceSinAcceso(_cliente.Id))
            .Handle(new CancelarContratoAccesoCommand(contrato.Id, contrato.Version), CancellationToken.None)).EsExitoso.Should().BeTrue();
        contrato.Estado.Should().Be(EstadoContratoAcceso.Cancelado);
    }

    [Fact]
    public async Task Cancelar_el_dia_de_inicio_responde_TransicionNoPermitida_sin_guardar()
    {
        var contrato = ContratoExistente();
        var elDiaDeInicio = new RelojFijo(new DateTimeOffset(2026, 3, 1, 9, 0, 0, TimeSpan.Zero));

        var resultado = await Cancelar(AlcanceSinAcceso(_cliente.Id), elDiaDeInicio)
            .Handle(new CancelarContratoAccesoCommand(contrato.Id, contrato.Version), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("ContratoAcceso.TransicionNoPermitida");
        contrato.Estado.Should().Be(EstadoContratoAcceso.Activo);
        _unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task FijarCondiciones_fuera_de_alcance_falla_sin_tocarlo()
    {
        var contrato = ContratoExistente();

        var resultado = await FijarCondiciones(AlcanceSinAcceso())
            .Handle(new FijarCondicionesContratoAccesoCommand(contrato.Id, ConCupo, contrato.Version), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("ContratoAcceso.NoEncontrado");
        contrato.Condiciones.Should().Be(CondicionesContratoAcceso.SinFijar);
        _unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task FijarCondiciones_dentro_del_alcance_las_guarda()
    {
        var contrato = ContratoExistente();

        var resultado = await FijarCondiciones(AlcanceSinAcceso(_cliente.Id))
            .Handle(new FijarCondicionesContratoAccesoCommand(contrato.Id, ConCupo, contrato.Version), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        contrato.Tipo.Should().Be(TipoContratoAcceso.Temporal);
        contrato.PropiaActividad.Should().Be(PropiaActividadContrato.NoLoSe);
        contrato.NivelMaxSubcontrata.Should().Be(1);
        contrato.CupoTrabajadores.Should().Be(8);
        contrato.CupoSubcontratas.Should().Be(2);
        contrato.MotivoCupo.Should().Be("Aforo del muelle");
        _unitOfWork.VecesGuardado.Should().Be(1);
    }

    [Fact]
    public async Task FijarCondiciones_con_cupo_sin_motivo_responde_CondicionesInvalidas_sin_guardar()
    {
        var contrato = ContratoExistente();
        var comando = new FijarCondicionesContratoAccesoCommand(contrato.Id, new CondicionesContratoAccesoDatos(CupoTrabajadores: 4), contrato.Version);

        new FijarCondicionesContratoAccesoCommandValidator().Validate(comando).IsValid.Should().BeFalse();
        var resultado = await FijarCondiciones(AlcanceSinAcceso(_cliente.Id)).Handle(comando, CancellationToken.None);

        resultado.Error.Codigo.Should().Be("ContratoAcceso.CondicionesInvalidas");
        contrato.CupoTrabajadores.Should().BeNull();
        _unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Theory]
    [InlineData(-1, null, null, null)]
    [InlineData(4, null, null, null)]
    [InlineData(null, -1, null, "x")]
    [InlineData(null, null, -1, "x")]
    [InlineData(null, null, null, "Sin cupo")]
    public void El_validador_de_condiciones_rechaza_lo_que_el_dominio_rechaza(int? nivel, int? trabajadores, int? subcontratas, string? motivo)
    {
        var datos = new CondicionesContratoAccesoDatos(null, null, nivel, trabajadores, subcontratas, motivo);

        new CondicionesContratoAccesoDatosValidator().Validate(datos).IsValid.Should().BeFalse();
        datos.ADominio().EsExitoso.Should().BeFalse();
    }

    [Fact]
    public async Task Crear_con_condiciones_las_guarda_y_con_condiciones_invalidas_no_guarda()
    {
        var conCondiciones = await Crear(AlcanceSinAcceso(_cliente.Id)).Handle(
            new CrearContratoAccesoCommand(_cliente.Id, _centro.Id, _contratista.Id, Inicio, null, null, ConCupo), CancellationToken.None);

        conCondiciones.EsExitoso.Should().BeTrue();
        _repositorio.Contratos.Should().ContainSingle().Which.CupoTrabajadores.Should().Be(8);

        var invalidas = await Crear(AlcanceSinAcceso(_cliente.Id)).Handle(
            new CrearContratoAccesoCommand(_cliente.Id, _centro.Id, _contratista.Id, Inicio, null, null,
                new CondicionesContratoAccesoDatos(NivelMaxSubcontrata: 9)), CancellationToken.None);

        invalidas.Error.Codigo.Should().Be("ContratoAcceso.CondicionesInvalidas");
        _repositorio.Contratos.Should().ContainSingle();
        _unitOfWork.VecesGuardado.Should().Be(1);
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
    public async Task Cerrar_y_eliminar_dentro_del_alcance_guardan_y_cerrar_lo_finaliza()
    {
        var contrato = ContratoExistente();
        var alcance = AlcanceSinAcceso(_cliente.Id);

        (await new CerrarContratoAccesoCommandHandler(_repositorio, alcance, _unitOfWork)
            .Handle(new CerrarContratoAccesoCommand(contrato.Id, Inicio.AddDays(5)), CancellationToken.None)).EsExitoso.Should().BeTrue();
        contrato.FechaCierreReal.Should().Be(Inicio.AddDays(5));
        contrato.Estado.Should().Be(EstadoContratoAcceso.Finalizado, "Cerrar es Finalizar: D-21 retira Reabrir");

        (await new CerrarContratoAccesoCommandHandler(_repositorio, alcance, _unitOfWork)
            .Handle(new CerrarContratoAccesoCommand(contrato.Id, Inicio.AddDays(6)), CancellationToken.None))
            .Error.Codigo.Should().Be("ContratoAcceso.YaCerrado");

        var usuario = Guid.NewGuid();
        (await new EliminarContratoAccesoCommandHandler(_repositorio, alcance, _unitOfWork, new CurrentUserServiceFalso(usuario))
            .Handle(new EliminarContratoAccesoCommand(contrato.Id), CancellationToken.None)).EsExitoso.Should().BeTrue();
        contrato.EstaEliminado.Should().BeTrue();
        contrato.EliminadoPorUsuarioId.Should().Be(usuario);
        _unitOfWork.VecesGuardado.Should().Be(2);
    }

    private ContratoAcceso ContratoConFin()
    {
        var contrato = ContratoAcceso.Crear(_cliente.Id, _centro.Id, _contratista.Id, Inicio, Inicio.AddDays(30), "Nota");
        _repositorio.Agregar(contrato);
        return contrato;
    }

    private static EvidenciaInbound EvidenciaDe(Guid contratoAccesoId) =>
        EvidenciaInbound.Registrar(
            new EvidenciaInbound.Procedencia(contratoAccesoId, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()), Guid.NewGuid(),
            new EvidenciaInbound.Sujeto(Guid.NewGuid(), "Ana Pérez", "12345678Z"), Inicio, Inicio.AddYears(1),
            soloConclusionAptitud: false, conclusion: null,
            new EvidenciaInbound.ArchivoCopiado($"{Guid.NewGuid():N}/copia.pdf", new string('a', 64)), Guid.NewGuid(), DateTime.UtcNow);

    private sealed class EvidenciasFalsas(List<EvidenciaInbound> evidencias) : IEvidenciasInboundQueryContext
    {
        public IQueryable<EvidenciaInbound> EvidenciasInbound => new TestAsyncQueryable<EvidenciaInbound>(evidencias.AsQueryable());
    }

    private sealed class RelojFijo(DateTimeOffset ahora) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => ahora;
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
