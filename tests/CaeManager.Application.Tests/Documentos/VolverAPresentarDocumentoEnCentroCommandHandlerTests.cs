using CaeManager.Application.Documentos.Commands.VolverAPresentarDocumentoEnCentro;
using CaeManager.Application.Documentos.Presentaciones;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Plantillas;
using CaeManager.Application.Tests.Proyectos;
using CaeManager.Application.Tests.Reportes;
using CaeManager.Application.Tests.TiposDocumento;
using CaeManager.Domain.Asignaciones;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Trabajadores;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Documentos;

/// <summary>
/// «Volver a presentar» (2026-10-04): registra una nueva presentación del MISMO Documento a un Centro con periodicidad especial.
/// Cada test observa lo que el comando escribe en el historial de presentaciones y si guarda; los falsos de contexto son los
/// mismos que usan los demás tests de Application.
/// </summary>
public class VolverAPresentarDocumentoEnCentroCommandHandlerTests
{
    private static readonly DateOnly Hoy = DiaDeNegocio.Hoy();

    private readonly DocumentoRepositorioFalso _documentos = new();
    private readonly CentrosQueryContextFalso _centros = new();
    private readonly TiposDocumentoQueryContextFalso _tipos = new();
    private readonly AsignacionesQueryContextFalso _asignaciones = new();
    private readonly TrabajadoresQueryContextFalso _trabajadores = new();
    private readonly PresentacionDocumentoEnCentroRepositorioFalso _presentaciones = new();
    private readonly UnitOfWorkFalso _uow = new();

    private readonly Guid _empresaId = Guid.NewGuid();
    private readonly Guid _tipoId = Guid.NewGuid();
    private readonly Centro _centro;
    private readonly Trabajador _ana;

    public VolverAPresentarDocumentoEnCentroCommandHandlerTests()
    {
        _centro = new Centro(Guid.NewGuid(), _empresaId, "Nave Norte");
        _ana = Trabajador.DeEmpresa(_empresaId, "Ana", "Garcia", "12345678Z");
        _centros.ListaCentros.Add(_centro);
        _trabajadores.ListaTrabajadores.Add(_ana);
        _asignaciones.ListaAsignaciones.Add(new Asignacion(_ana.Id, _centro.Id, Hoy.AddMonths(-6)));
        _tipos.ListaTiposDocumentoCentros.Add(new TipoDocumentoCentro(_tipoId, _centro.Id, incluido: true, periodicidadEspecialMeses: 12));
    }

    private VolverAPresentarDocumentoEnCentroCommandHandler Handler(AlcanceDatosServiceFalso? alcance = null) => new(
        _documentos, alcance ?? new AlcanceDatosServiceFalso(), new ProyectosQueryContextFalso(), _centros, _tipos, _asignaciones, _trabajadores,
        new RegistroDePresentaciones(_presentaciones, _centros), _uow);

    private Documento DocumentoDeAna(VigenciaDocumento? vigencia = null)
    {
        var documento = Documento.DeTrabajador(_ana.Id, _tipoId, Hoy.AddMonths(-13), vigencia ?? VigenciaDocumento.VenceEl(Hoy.AddYears(3)));
        _documentos.Agregar(documento);
        return documento;
    }

    [Fact]
    public async Task Registra_una_presentacion_de_hoy_al_Centro_con_origen_Volver_a_presentar_y_guarda()
    {
        var documento = DocumentoDeAna();

        var resultado = await Handler().Handle(new VolverAPresentarDocumentoEnCentroCommand(documento.Id, _centro.Id), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        var fila = _presentaciones.Agregadas.Should().ContainSingle().Subject;
        fila.DocumentoId.Should().Be(documento.Id);
        fila.CentroId.Should().Be(_centro.Id);
        fila.FechaPresentacion.Should().Be(Hoy);
        fila.Origen.Should().Be(OrigenPresentacionDocumentoEnCentro.VolverAPresentar);
        _uow.VecesGuardado.Should().Be(1);
    }

    [Fact]
    public async Task No_cambia_el_documento_ni_su_vigencia()
    {
        var vigencia = VigenciaDocumento.VenceEl(Hoy.AddYears(3));
        var documento = DocumentoDeAna(vigencia);

        await Handler().Handle(new VolverAPresentarDocumentoEnCentroCommand(documento.Id, _centro.Id), CancellationToken.None);

        documento.Vigencia.Should().Be(vigencia);
        documento.FechaEmision.Should().Be(Hoy.AddMonths(-13));
        documento.EstaSustituido.Should().BeFalse();
    }

    [Fact]
    public async Task Pulsarlo_dos_veces_el_mismo_dia_no_duplica_la_fila()
    {
        var documento = DocumentoDeAna();
        var comando = new VolverAPresentarDocumentoEnCentroCommand(documento.Id, _centro.Id);

        (await Handler().Handle(comando, CancellationToken.None)).EsExitoso.Should().BeTrue();
        (await Handler().Handle(comando, CancellationToken.None)).EsExitoso.Should().BeTrue();

        _presentaciones.Agregadas.Should().ContainSingle();
    }

    [Fact]
    public async Task Un_documento_vencido_por_su_fecha_se_renueva_no_se_vuelve_a_presentar()
    {
        var documento = DocumentoDeAna(VigenciaDocumento.VenceEl(Hoy.AddDays(-1)));

        var resultado = await Handler().Handle(new VolverAPresentarDocumentoEnCentroCommand(documento.Id, _centro.Id), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be(VolverAPresentarDocumentoEnCentroCommandHandler.CodigoNoAplica);
        _presentaciones.Agregadas.Should().BeEmpty();
        _uow.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Un_documento_que_no_caduca_no_se_vuelve_a_presentar()
    {
        var documento = DocumentoDeAna(VigenciaDocumento.NoCaduca);

        var resultado = await Handler().Handle(new VolverAPresentarDocumentoEnCentroCommand(documento.Id, _centro.Id), CancellationToken.None);

        resultado.Error.Codigo.Should().Be(VolverAPresentarDocumentoEnCentroCommandHandler.CodigoNoAplica);
        _presentaciones.Agregadas.Should().BeEmpty();
    }

    [Fact]
    public async Task Un_Centro_sin_periodicidad_para_ese_tipo_no_ofrece_volver_a_presentar()
    {
        _tipos.ListaTiposDocumentoCentros.Clear();
        _tipos.ListaTiposDocumentoCentros.Add(new TipoDocumentoCentro(_tipoId, _centro.Id, incluido: true));
        var documento = DocumentoDeAna();

        var resultado = await Handler().Handle(new VolverAPresentarDocumentoEnCentroCommand(documento.Id, _centro.Id), CancellationToken.None);

        resultado.Error.Codigo.Should().Be(VolverAPresentarDocumentoEnCentroCommandHandler.CodigoNoAplica);
        _presentaciones.Agregadas.Should().BeEmpty();
    }

    [Fact]
    public async Task Un_documento_del_historial_no_se_vuelve_a_presentar()
    {
        var sustituido = DocumentoDeAna();
        var nuevo = Documento.DeTrabajador(_ana.Id, _tipoId, Hoy, VigenciaDocumento.VenceEl(Hoy.AddYears(4)));
        _documentos.Agregar(nuevo);
        sustituido.SustituirPor(nuevo, MotivoSustitucionDocumento.Renovacion, DateTime.UtcNow);

        var resultado = await Handler().Handle(new VolverAPresentarDocumentoEnCentroCommand(sustituido.Id, _centro.Id), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        _presentaciones.Agregadas.Should().BeEmpty();
    }

    [Fact]
    public async Task Un_Trabajador_sin_Asignacion_activa_en_el_Centro_se_trata_como_inexistente()
    {
        var otro = Trabajador.DeEmpresa(_empresaId, "Otro", "Sin Asignar", "77189989B");
        _trabajadores.ListaTrabajadores.Add(otro);
        var documento = Documento.DeTrabajador(otro.Id, _tipoId, Hoy.AddMonths(-13), VigenciaDocumento.VenceEl(Hoy.AddYears(3)));
        _documentos.Agregar(documento);

        var resultado = await Handler().Handle(new VolverAPresentarDocumentoEnCentroCommand(documento.Id, _centro.Id), CancellationToken.None);

        resultado.Error.Codigo.Should().Be(VolverAPresentarDocumentoEnCentroCommandHandler.CodigoNoEncontrado);
        _presentaciones.Agregadas.Should().BeEmpty();
    }

    [Fact]
    public async Task Una_Asignacion_dada_de_baja_ya_no_vincula_al_Trabajador_con_el_Centro()
    {
        var asignacion = _asignaciones.ListaAsignaciones.Single();
        asignacion.DarDeBaja(Hoy.AddDays(-1));
        var documento = DocumentoDeAna();

        var resultado = await Handler().Handle(new VolverAPresentarDocumentoEnCentroCommand(documento.Id, _centro.Id), CancellationToken.None);

        resultado.Error.Codigo.Should().Be(VolverAPresentarDocumentoEnCentroCommandHandler.CodigoNoEncontrado);
        _presentaciones.Agregadas.Should().BeEmpty();
    }

    [Fact]
    public async Task Un_Documento_de_la_Empresa_propia_del_Centro_se_puede_volver_a_presentar_y_uno_de_otra_Empresa_no()
    {
        var propio = Documento.DeEmpresa(_empresaId, _tipoId, Hoy.AddMonths(-13), VigenciaDocumento.VenceEl(Hoy.AddYears(3)));
        var ajeno = Documento.DeEmpresa(Guid.NewGuid(), _tipoId, Hoy.AddMonths(-13), VigenciaDocumento.VenceEl(Hoy.AddYears(3)));
        _documentos.Agregar(propio);
        _documentos.Agregar(ajeno);

        (await Handler().Handle(new VolverAPresentarDocumentoEnCentroCommand(propio.Id, _centro.Id), CancellationToken.None)).EsExitoso.Should().BeTrue();
        var alAjeno = await Handler().Handle(new VolverAPresentarDocumentoEnCentroCommand(ajeno.Id, _centro.Id), CancellationToken.None);

        alAjeno.Error.Codigo.Should().Be(VolverAPresentarDocumentoEnCentroCommandHandler.CodigoNoEncontrado);
        _presentaciones.Agregadas.Should().ContainSingle().Which.DocumentoId.Should().Be(propio.Id);
    }

    [Fact]
    public async Task Un_documento_que_no_existe_o_un_Centro_inexistente_dan_el_mismo_error_de_no_encontrado()
    {
        var documento = DocumentoDeAna();

        var sinDocumento = await Handler().Handle(new VolverAPresentarDocumentoEnCentroCommand(Guid.NewGuid(), _centro.Id), CancellationToken.None);
        var sinCentro = await Handler().Handle(new VolverAPresentarDocumentoEnCentroCommand(documento.Id, Guid.NewGuid()), CancellationToken.None);

        sinDocumento.Error.Codigo.Should().Be(VolverAPresentarDocumentoEnCentroCommandHandler.CodigoNoEncontrado);
        sinCentro.Error.Codigo.Should().Be(VolverAPresentarDocumentoEnCentroCommandHandler.CodigoNoEncontrado);
        _presentaciones.Agregadas.Should().BeEmpty();
    }

    [Fact]
    public async Task Un_Centro_fuera_del_alcance_de_gestion_se_trata_como_inexistente()
    {
        var documento = DocumentoDeAna();
        var alcance = new AlcanceDatosServiceFalso(tieneAccesoTotal: false, centroIdsParaGestion: []);

        var resultado = await Handler(alcance).Handle(new VolverAPresentarDocumentoEnCentroCommand(documento.Id, _centro.Id), CancellationToken.None);

        resultado.Error.Codigo.Should().Be(VolverAPresentarDocumentoEnCentroCommandHandler.CodigoNoEncontrado);
        _presentaciones.Agregadas.Should().BeEmpty();
        _uow.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Un_Centro_sin_gestion_CAE_no_registra_presentaciones()
    {
        _centro.EstablecerGestionCae(ModalidadGestionCae.SinGestionCae);
        var documento = DocumentoDeAna();

        var resultado = await Handler().Handle(new VolverAPresentarDocumentoEnCentroCommand(documento.Id, _centro.Id), CancellationToken.None);

        resultado.Error.Codigo.Should().Be(VolverAPresentarDocumentoEnCentroCommandHandler.CodigoNoEncontrado);
        _presentaciones.Agregadas.Should().BeEmpty();
    }

    [Fact]
    public async Task Volver_a_presentar_reinicia_el_plazo_de_ESE_Centro_y_no_el_de_otro()
    {
        // Prueba extremo a extremo de la regla con el comando: antes vencido en el Centro; tras presentar, vigente un ano mas.
        var documento = DocumentoDeAna();
        var antes = new DocumentoParaAcceso(documento.Vigencia, documento.FechaEmision);
        var condiciones = new CondicionesDeAccesoDelCentro(12, 0);
        ReglaBloqueoDeAcceso.EstadoEnElCentro(antes, condiciones, Hoy, 30, 15).Estado.Should().Be(EstadoDocumento.Vencido);

        await Handler().Handle(new VolverAPresentarDocumentoEnCentroCommand(documento.Id, _centro.Id), CancellationToken.None);

        var despues = antes with { UltimaPresentacionEnElCentro = _presentaciones.Agregadas.Single().FechaPresentacion };
        ReglaBloqueoDeAcceso.EstadoEnElCentro(despues, condiciones, Hoy, 30, 15)
            .Should().Be(new EstadoDeDocumentoEnElCentro(EstadoDocumento.Vigente, Hoy.AddMonths(12), null));
    }

    [Fact]
    public void El_validador_exige_los_dos_identificadores()
    {
        var validador = new VolverAPresentarDocumentoEnCentroCommandValidator();

        validador.Validate(new VolverAPresentarDocumentoEnCentroCommand(Guid.Empty, Guid.NewGuid())).IsValid.Should().BeFalse();
        validador.Validate(new VolverAPresentarDocumentoEnCentroCommand(Guid.NewGuid(), Guid.Empty)).IsValid.Should().BeFalse();
        validador.Validate(new VolverAPresentarDocumentoEnCentroCommand(Guid.NewGuid(), Guid.NewGuid())).IsValid.Should().BeTrue();
    }
}
