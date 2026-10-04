using CaeManager.Application.Documentos.Presentaciones;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Documentos;
using CaeManager.Application.Tests.Integraciones;
using CaeManager.Application.Tests.Plantillas;
using CaeManager.Application.Tests.Proyectos;
using CaeManager.Application.Visitas;
using CaeManager.Application.Visitas.Commands.RegistrarEnvioPaqueteDocumentalPorCorreo;
using CaeManager.Application.Visitas.Queries.ObtenerSolicitudAccesoCorreo;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Visitas;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Visitas;

/// <summary>
/// El envío por correo confirmado en el compositor de la Visita registra una presentación por Documento al Centro de la Visita
/// (2026-10-04). Los identificadores llegan de la pantalla, así que el servidor solo registra los que el paquete de esa Visita puede
/// contener y que el usuario ve.
/// </summary>
public class RegistrarEnvioPaqueteDocumentalPorCorreoCommandHandlerTests
{
    private static readonly DateOnly Hoy = DiaDeNegocio.Hoy();

    private readonly VisitasQueryContextFalso _visitas = new();
    private readonly CentrosQueryContextFalso _centros = new();
    private readonly DocumentosQueryContextFalso _documentos = new();
    private readonly PresentacionDocumentoEnCentroRepositorioFalso _presentaciones = new();
    private readonly UnitOfWorkFalso _uow = new();

    private readonly Guid _empresaId = Guid.NewGuid();
    private readonly Guid _trabajadorEnVisita = Guid.NewGuid();
    private readonly Centro _centro;
    private readonly Visita _visita;

    public RegistrarEnvioPaqueteDocumentalPorCorreoCommandHandlerTests()
    {
        _centro = new Centro(Guid.NewGuid(), _empresaId, "Nave Norte");
        _visita = new Visita(_centro.Id, Hoy.AddDays(1), Hoy.AddDays(2), null);
        _centros.ListaCentros.Add(_centro);
        _visitas.ListaVisitas.Add(_visita);
        _visitas.ListaVisitasTrabajadores.Add(new VisitaTrabajador(_visita.Id, _trabajadorEnVisita));
    }

    private RegistrarEnvioPaqueteDocumentalPorCorreoCommandHandler Handler(AlcanceDatosServiceFalso? alcance = null) => new(
        _visitas, _centros, _documentos, alcance ?? new AlcanceDatosServiceFalso(), new ProyectosQueryContextFalso(),
        new RegistroDePresentaciones(_presentaciones, _centros), _uow);

    private Documento DelTrabajador(Guid trabajadorId, Guid? tipoId = null)
    {
        var d = Documento.DeTrabajador(trabajadorId, tipoId ?? Guid.NewGuid(), Hoy.AddMonths(-3), VigenciaDocumento.VenceEl(Hoy.AddYears(1)));
        _documentos.ListaDocumentos.Add(d);
        return d;
    }

    private Documento DeLaEmpresa(Guid empresaId)
    {
        var d = Documento.DeEmpresa(empresaId, Guid.NewGuid(), Hoy.AddMonths(-3), VigenciaDocumento.VenceEl(Hoy.AddYears(1)));
        _documentos.ListaDocumentos.Add(d);
        return d;
    }

    [Fact]
    public async Task Registra_una_presentacion_por_documento_al_Centro_de_la_Visita_con_origen_Envio_por_correo()
    {
        var delTrabajador = DelTrabajador(_trabajadorEnVisita);
        var deLaEmpresa = DeLaEmpresa(_empresaId);

        var resultado = await Handler().Handle(
            new RegistrarEnvioPaqueteDocumentalPorCorreoCommand(_visita.Id, [delTrabajador.Id, deLaEmpresa.Id]), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        _presentaciones.Agregadas.Select(p => p.DocumentoId).Should().BeEquivalentTo([delTrabajador.Id, deLaEmpresa.Id]);
        _presentaciones.Agregadas.Should().OnlyContain(p =>
            p.CentroId == _centro.Id && p.Origen == OrigenPresentacionDocumentoEnCentro.EnvioPorCorreo && p.FechaPresentacion == Hoy);
        _uow.VecesGuardado.Should().Be(1);
    }

    [Fact]
    public async Task Ignora_sin_registrar_los_documentos_que_el_paquete_de_la_Visita_no_puede_contener()
    {
        var deOtroTrabajador = DelTrabajador(Guid.NewGuid());
        var deOtraEmpresa = DeLaEmpresa(Guid.NewGuid());
        var inexistente = Guid.NewGuid();
        var bueno = DelTrabajador(_trabajadorEnVisita);

        var resultado = await Handler().Handle(
            new RegistrarEnvioPaqueteDocumentalPorCorreoCommand(_visita.Id, [deOtroTrabajador.Id, deOtraEmpresa.Id, inexistente, bueno.Id]),
            CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        _presentaciones.Agregadas.Should().ContainSingle().Which.DocumentoId.Should().Be(bueno.Id);
    }

    [Fact]
    public async Task Un_documento_del_historial_no_se_registra()
    {
        var tipo = Guid.NewGuid();
        var antiguo = DelTrabajador(_trabajadorEnVisita, tipo);
        var nuevo = DelTrabajador(_trabajadorEnVisita, tipo);
        antiguo.SustituirPor(nuevo, MotivoSustitucionDocumento.Renovacion, DateTime.UtcNow);

        await Handler().Handle(
            new RegistrarEnvioPaqueteDocumentalPorCorreoCommand(_visita.Id, [antiguo.Id, nuevo.Id]), CancellationToken.None);

        _presentaciones.Agregadas.Should().ContainSingle().Which.DocumentoId.Should().Be(nuevo.Id);
    }

    [Fact]
    public async Task Los_ids_repetidos_y_el_reenvio_el_mismo_dia_no_duplican_filas()
    {
        var documento = DelTrabajador(_trabajadorEnVisita);
        var comando = new RegistrarEnvioPaqueteDocumentalPorCorreoCommand(_visita.Id, [documento.Id, documento.Id]);

        await Handler().Handle(comando, CancellationToken.None);
        await Handler().Handle(comando, CancellationToken.None);

        _presentaciones.Agregadas.Should().ContainSingle();
    }

    [Fact]
    public async Task Una_Visita_cancelada_no_registra_nada()
    {
        var documento = DelTrabajador(_trabajadorEnVisita);
        _visita.Cancelar(DateTime.UtcNow, "Cancelada");

        var resultado = await Handler().Handle(
            new RegistrarEnvioPaqueteDocumentalPorCorreoCommand(_visita.Id, [documento.Id]), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Should().Be(ObtenerSolicitudAccesoCorreoQueryHandler.VisitaCancelada);
        _presentaciones.Agregadas.Should().BeEmpty();
        _uow.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Una_Visita_de_un_Centro_sin_gestion_CAE_no_registra_nada()
    {
        var documento = DelTrabajador(_trabajadorEnVisita);
        _centro.EstablecerGestionCae(ModalidadGestionCae.SinGestionCae);

        var resultado = await Handler().Handle(
            new RegistrarEnvioPaqueteDocumentalPorCorreoCommand(_visita.Id, [documento.Id]), CancellationToken.None);

        resultado.Error.Should().Be(ObtenerSolicitudAccesoCorreoQueryHandler.CentroSinGestionCae);
        _presentaciones.Agregadas.Should().BeEmpty();
    }

    [Fact]
    public async Task Una_Visita_inexistente_o_de_un_Centro_fuera_del_alcance_de_gestion_da_no_encontrada()
    {
        var documento = DelTrabajador(_trabajadorEnVisita);
        var fueraDeAlcance = new AlcanceDatosServiceFalso(tieneAccesoTotal: false, centroIdsParaGestion: []);

        var inexistente = await Handler().Handle(
            new RegistrarEnvioPaqueteDocumentalPorCorreoCommand(Guid.NewGuid(), [documento.Id]), CancellationToken.None);
        var ajena = await Handler(fueraDeAlcance).Handle(
            new RegistrarEnvioPaqueteDocumentalPorCorreoCommand(_visita.Id, [documento.Id]), CancellationToken.None);

        inexistente.Error.Should().Be(ObtenerSolicitudAccesoCorreoQueryHandler.NoEncontrada);
        ajena.Error.Should().Be(ObtenerSolicitudAccesoCorreoQueryHandler.NoEncontrada);
        _presentaciones.Agregadas.Should().BeEmpty();
    }

    [Fact]
    public void El_validador_exige_entre_1_y_500_documentos()
    {
        var validador = new RegistrarEnvioPaqueteDocumentalPorCorreoCommandValidator();
        var visitaId = Guid.NewGuid();

        validador.Validate(new RegistrarEnvioPaqueteDocumentalPorCorreoCommand(Guid.Empty, [Guid.NewGuid()])).IsValid.Should().BeFalse();
        validador.Validate(new RegistrarEnvioPaqueteDocumentalPorCorreoCommand(visitaId, [])).IsValid.Should().BeFalse();
        validador.Validate(new RegistrarEnvioPaqueteDocumentalPorCorreoCommand(visitaId, [Guid.NewGuid()])).IsValid.Should().BeTrue();
        validador.Validate(new RegistrarEnvioPaqueteDocumentalPorCorreoCommand(
            visitaId, Enumerable.Range(0, RegistrarEnvioPaqueteDocumentalPorCorreoCommandValidator.MaximoDocumentos).Select(_ => Guid.NewGuid()).ToList()))
            .IsValid.Should().BeTrue();
        validador.Validate(new RegistrarEnvioPaqueteDocumentalPorCorreoCommand(
            visitaId, Enumerable.Range(0, RegistrarEnvioPaqueteDocumentalPorCorreoCommandValidator.MaximoDocumentos + 1).Select(_ => Guid.NewGuid()).ToList()))
            .IsValid.Should().BeFalse();
    }

    private sealed class VisitasQueryContextFalso : IVisitasQueryContext
    {
        public List<Visita> ListaVisitas { get; } = [];
        public List<VisitaTrabajador> ListaVisitasTrabajadores { get; } = [];

        public IQueryable<Visita> Visitas => new TestAsyncQueryable<Visita>(ListaVisitas.AsQueryable());
        public IQueryable<VisitaTrabajador> VisitasTrabajadores => new TestAsyncQueryable<VisitaTrabajador>(ListaVisitasTrabajadores.AsQueryable());
    }
}
