using CaeManager.Application.Common;
using CaeManager.Application.Inbound.Requisitos;
using CaeManager.Application.Inbound.Requisitos.Commands.AnadirRequisitoAPlantilla;
using CaeManager.Application.Inbound.Requisitos.Commands.CrearPlantillaRequisitos;
using CaeManager.Application.Inbound.Requisitos.Commands.CrearRequisitoPropio;
using CaeManager.Application.Inbound.Requisitos.Commands.EditarRequisitoPlantilla;
using CaeManager.Application.Inbound.Requisitos.Commands.RetirarPlantillaRequisitos;
using CaeManager.Application.Inbound.Requisitos.Commands.RetirarRequisitoPlantilla;
using CaeManager.Application.Inbound.Requisitos.Queries;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Documentos;
using CaeManager.Application.Tests.Plantillas;
using CaeManager.Application.Tests.TiposDocumento;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Inbound.CatalogoBase;
using CaeManager.Domain.Inbound.Requisitos;
using CaeManager.Domain.Trabajadores;
using FluentAssertions;
using Xunit;
using TipoDocumentoDominio = CaeManager.Domain.Documentos.TipoDocumento;

namespace CaeManager.Application.Tests.Inbound.Requisitos;

/// <summary>
/// Comandos y consultas de las plantillas de requisitos (B-14). Lo que no está en la cartera del Gestor CAE responde como
/// si no existiera y no guarda nada; las plantillas de Actividad o de riesgo, que valen para todo el Tenant TCE, solo las
/// cambia quien alcanza el Tenant entero; el usuario de portal no ve ni cambia ninguna.
/// </summary>
public class PlantillaRequisitosHandlersTests
{
    private static readonly AtributosRequisito Basicos = new(
        OrigenRequisito.Actividad, ModoVigenciaRequisito.LaPoneElDocumento, VigenciaMeses: null, ToleranciaDias: null,
        BloqueaAcceso: true, Criterio: null, DestinatarioRequisito.Todos, TiposContratacion.Todos, [],
        AmbitoDocumentoRequisito.GeneralTec);

    private readonly Empresa _cliente = Empresa.CrearComoCliente("Cliente empresarial de la cartera", "B10380186", false, null, null);
    private readonly Empresa _otroCliente = Empresa.CrearComoCliente("Cliente empresarial de otra cartera", "B10380202", false, null, null);
    private readonly Centro _centro;
    private readonly Centro _centroAjeno;
    private readonly ActividadCatalogo _actividad = new("actividad-prueba", "Actividad de prueba");
    private readonly PlantillaRequisitosRepositorioFalso _plantillas = new();
    private readonly TipoDocumentoRepositorioFalso _tipos = new();
    private readonly CatalogoBaseQueryContextFalso _catalogo = new();
    private readonly CentrosQueryContextFalso _centros = new();
    private readonly EmpresasQueryContextFalso _empresas = new();
    private readonly UnitOfWorkFalso _unitOfWork = new();

    public PlantillaRequisitosHandlersTests()
    {
        _centro = new Centro(_cliente.Id, _cliente.Id, "Centro de la cartera");
        _centroAjeno = new Centro(_otroCliente.Id, _otroCliente.Id, "Centro de otra cartera");
        _centros.ListaCentros.AddRange([_centro, _centroAjeno]);
        _empresas.ListaEmpresas.AddRange([_cliente, _otroCliente]);
        _catalogo.Actividades.Add(_actividad);
    }

    /// <summary>Un Gestor CAE con cartera acotada: un Centro y un Cliente empresarial.</summary>
    private AlcanceDatosServiceFalso Acotado() =>
        new(tieneAccesoTotal: false, clienteIdsVisibles: [_cliente.Id], centroIdsVisibles: [_centro.Id]);

    private static AlcanceDatosServiceFalso Total() => new(tieneAccesoTotal: true);

    private static AlcanceDatosServiceFalso Portal() => new(tieneAccesoTotal: false, ladoDeGestion: false, centroIdsParaGestion: []);

    private CrearPlantillaRequisitosCommandHandler Crear(IAlcanceDatosService alcance) =>
        new(_plantillas, _catalogo, _centros, _empresas, alcance, _unitOfWork);

    private AnadirRequisitoAPlantillaCommandHandler Anadir(IAlcanceDatosService alcance) =>
        new(_plantillas, _tipos, _catalogo, alcance, _unitOfWork);

    private TipoDocumentoDominio TipoGuardado(GrupoTipoDocumento? grupo = GrupoTipoDocumento.FormacionPrl)
    {
        var tipo = new TipoDocumentoDominio("Tipo de prueba " + Guid.NewGuid().ToString("N")[..6], null, false, 0,
            AmbitoAplicacion.Trabajador, sensibilidad: SensibilidadDocumental.DatosPersonales);
        tipo.EstablecerGrupo(grupo);
        _tipos.Agregar(tipo);
        return tipo;
    }

    private PlantillaRequisitos PlantillaDe(PlantillaRequisitos plantilla)
    {
        _plantillas.Agregar(plantilla);
        return plantilla;
    }

    // ── Alta de la plantilla ────────────────────────────────────────────────────────

    [Fact]
    public async Task Crear_la_plantilla_de_un_Centro_de_la_cartera_la_guarda()
    {
        var resultado = await Crear(Acotado()).Handle(
            new CrearPlantillaRequisitosCommand(TipoPlantillaRequisitos.DeCentro, _centro.Id), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        _plantillas.Plantillas.Should().ContainSingle(p => p.Id == resultado.Valor && p.CentroId == _centro.Id);
        _unitOfWork.VecesGuardado.Should().Be(1);
    }

    [Theory]
    [InlineData(TipoPlantillaRequisitos.DeCentro)]
    [InlineData(TipoPlantillaRequisitos.ExigenciasClienteEmpresarial)]
    public async Task Una_referencia_fuera_de_la_cartera_responde_como_inexistente(TipoPlantillaRequisitos tipo)
    {
        var referencia = tipo == TipoPlantillaRequisitos.DeCentro ? _centroAjeno.Id : _otroCliente.Id;

        var resultado = await Crear(Acotado()).Handle(new CrearPlantillaRequisitosCommand(tipo, referencia), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("PlantillaRequisitos.ReferenciaNoEncontrada");
        _plantillas.Plantillas.Should().BeEmpty();
        _unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Una_plantilla_por_Actividad_vale_para_todo_el_Tenant_y_una_cartera_acotada_no_la_crea()
    {
        var acotado = await Crear(Acotado()).Handle(
            new CrearPlantillaRequisitosCommand(TipoPlantillaRequisitos.PorActividad, _actividad.Id), CancellationToken.None);
        acotado.Error.Codigo.Should().Be("PlantillaRequisitos.ReferenciaNoEncontrada");

        var total = await Crear(Total()).Handle(
            new CrearPlantillaRequisitosCommand(TipoPlantillaRequisitos.PorActividad, _actividad.Id), CancellationToken.None);
        total.EsExitoso.Should().BeTrue();
        _plantillas.Plantillas.Should().ContainSingle(p => p.ActividadId == _actividad.Id);
    }

    [Fact]
    public async Task Una_Asignacion_de_Cartera_universal_sin_acceso_total_si_cambia_las_plantillas_por_Actividad()
    {
        // AlcanzaElTenantEntero, no TieneAccesoTotal: una cartera universal alcanza el Tenant entero sin acceso total. Con
        // la otra condición este test falla, y con OperaDesdeElLadoDeGestion falla el de la cartera acotada.
        var universal = new AlcanceUniversalSinAccesoTotal();
        (await universal.TieneAccesoTotalAsync()).Should().BeFalse("el instrumento simula de verdad una cartera sin acceso total");

        var resultado = await Crear(universal).Handle(
            new CrearPlantillaRequisitosCommand(TipoPlantillaRequisitos.PorActividad, _actividad.Id), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        _plantillas.Plantillas.Should().ContainSingle(p => p.ActividadId == _actividad.Id);
    }

    /// <summary>Cartera universal vigente: alcanza el Tenant entero, pero no tiene acceso total.</summary>
    private sealed class AlcanceUniversalSinAccesoTotal() : AlcanceDatosServiceFalso(tieneAccesoTotal: false), IAlcanceDatosService
    {
        public Task<bool> AlcanzaElTenantEnteroAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
    }

    [Fact]
    public async Task Las_exigencias_solo_son_de_un_Cliente_empresarial_aunque_el_acceso_sea_total()
    {
        // Una Empresa contratista (sin EsCritico) no es Cliente empresarial: no tiene exigencias N3 que imponer, y el acceso
        // total no filtra por cartera, así que la comprobación tiene que hacerla el comando.
        var contratista = new Empresa("Empresa contratista", "B10380210");
        _empresas.ListaEmpresas.Add(contratista);

        var rechazo = await Crear(Total()).Handle(
            new CrearPlantillaRequisitosCommand(TipoPlantillaRequisitos.ExigenciasClienteEmpresarial, contratista.Id), CancellationToken.None);
        rechazo.Error.Codigo.Should().Be("PlantillaRequisitos.ReferenciaNoEncontrada");
        _plantillas.Plantillas.Should().BeEmpty();

        var alta = await Crear(Total()).Handle(
            new CrearPlantillaRequisitosCommand(TipoPlantillaRequisitos.ExigenciasClienteEmpresarial, _cliente.Id), CancellationToken.None);
        alta.EsExitoso.Should().BeTrue();
        _plantillas.Plantillas.Should().ContainSingle(p => p.ClienteEmpresarialId == _cliente.Id);
    }

    [Fact]
    public void Toda_escritura_sobre_una_plantilla_exige_la_version_leida()
    {
        // Guid.Empty desactiva ConcurrenciaOptimista: el validador lo impide antes de llegar al comando.
        var id = Guid.NewGuid();
        new AnadirRequisitoAPlantillaCommandValidator().Validate(new AnadirRequisitoAPlantillaCommand(id, id, null, Basicos))
            .Errors.Should().Contain(e => e.PropertyName == "Version");
        new EditarRequisitoPlantillaCommandValidator().Validate(new EditarRequisitoPlantillaCommand(id, id, null, Basicos))
            .Errors.Should().Contain(e => e.PropertyName == "Version");
        new RetirarRequisitoPlantillaCommandValidator().Validate(new RetirarRequisitoPlantillaCommand(id, id))
            .Errors.Should().Contain(e => e.PropertyName == "Version");
        new RetirarPlantillaRequisitosCommandValidator().Validate(new RetirarPlantillaRequisitosCommand(id))
            .Errors.Should().Contain(e => e.PropertyName == "Version");
        new CrearRequisitoPropioCommandValidator()
            .Validate(new CrearRequisitoPropioCommand(id, "Permiso propio", AmbitoAplicacion.Trabajador, null, null, Basicos))
            .Errors.Should().Contain(e => e.PropertyName == "Version");

        new RetirarPlantillaRequisitosCommandValidator().Validate(new RetirarPlantillaRequisitosCommand(id, Guid.NewGuid()))
            .IsValid.Should().BeTrue("con la versión, el control positivo pasa");
    }

    [Fact]
    public async Task El_usuario_de_portal_no_crea_exigencias_de_su_Cliente_empresarial()
    {
        var portal = new AlcanceDatosServiceFalso(tieneAccesoTotal: false, clienteIdsVisibles: [_cliente.Id], ladoDeGestion: false);

        var resultado = await Crear(portal).Handle(
            new CrearPlantillaRequisitosCommand(TipoPlantillaRequisitos.ExigenciasClienteEmpresarial, _cliente.Id), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("PlantillaRequisitos.ReferenciaNoEncontrada");
        _plantillas.Plantillas.Should().BeEmpty();
    }

    [Fact]
    public async Task Una_segunda_plantilla_activa_para_la_misma_referencia_se_rechaza()
    {
        var comando = new CrearPlantillaRequisitosCommand(TipoPlantillaRequisitos.DeCentro, _centro.Id);
        (await Crear(Total()).Handle(comando, CancellationToken.None)).EsExitoso.Should().BeTrue();

        var segunda = await Crear(Total()).Handle(comando, CancellationToken.None);

        segunda.Error.Codigo.Should().Be("PlantillaRequisitos.YaExiste");
        _plantillas.Plantillas.Should().ContainSingle();
    }

    [Fact]
    public async Task Una_Actividad_retirada_o_un_Centro_inexistente_no_reciben_plantilla()
    {
        _actividad.Retirar();

        (await Crear(Total()).Handle(new CrearPlantillaRequisitosCommand(TipoPlantillaRequisitos.PorActividad, _actividad.Id), CancellationToken.None))
            .Error.Codigo.Should().Be("PlantillaRequisitos.Invalido");
        (await Crear(Total()).Handle(new CrearPlantillaRequisitosCommand(TipoPlantillaRequisitos.DeCentro, Guid.NewGuid()), CancellationToken.None))
            .Error.Codigo.Should().Be("PlantillaRequisitos.ReferenciaNoEncontrada");
        _plantillas.Plantillas.Should().BeEmpty();
    }

    // ── Requisitos de la plantilla ──────────────────────────────────────────────────

    [Fact]
    public async Task Anadir_un_requisito_a_una_plantilla_de_la_cartera_lo_guarda()
    {
        var plantilla = PlantillaDe(PlantillaRequisitos.ParaCentro(_centro.Id));
        var tipo = TipoGuardado();

        var resultado = await Anadir(Acotado()).Handle(
            new AnadirRequisitoAPlantillaCommand(plantilla.Id, tipo.Id, null, Basicos, plantilla.Version), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        plantilla.Requisitos.Should().ContainSingle(r => r.Id == resultado.Valor && r.TipoDocumentoId == tipo.Id);
        _unitOfWork.VecesGuardado.Should().Be(1);
    }

    [Fact]
    public async Task Cada_escritura_sobre_una_plantilla_fuera_de_la_cartera_responde_no_encontrada_sin_guardar()
    {
        var plantilla = PlantillaDe(PlantillaRequisitos.ParaCentro(_centroAjeno.Id));
        var tipo = TipoGuardado();
        var requisito = plantilla.AnadirRequisito(tipo, null, Basicos);
        var alcance = Acotado();

        var resultados = new[]
        {
            (Result)await Anadir(alcance).Handle(new AnadirRequisitoAPlantillaCommand(plantilla.Id, TipoGuardado().Id, null, Basicos), CancellationToken.None),
            await new EditarRequisitoPlantillaCommandHandler(_plantillas, _tipos, _catalogo, alcance, _unitOfWork).Handle(
                new EditarRequisitoPlantillaCommand(plantilla.Id, requisito.Id, null, Basicos with { BloqueaAcceso = false }), CancellationToken.None),
            await new RetirarRequisitoPlantillaCommandHandler(_plantillas, alcance, _unitOfWork).Handle(
                new RetirarRequisitoPlantillaCommand(plantilla.Id, requisito.Id), CancellationToken.None),
            await new RetirarPlantillaRequisitosCommandHandler(_plantillas, alcance, _unitOfWork).Handle(
                new RetirarPlantillaRequisitosCommand(plantilla.Id), CancellationToken.None),
            await new CrearRequisitoPropioCommandHandler(_plantillas, _tipos, alcance, _unitOfWork).Handle(
                new CrearRequisitoPropioCommand(plantilla.Id, "Permiso de trabajo propio", AmbitoAplicacion.Trabajador, null, null, Basicos),
                CancellationToken.None),
        };

        resultados.Should().AllSatisfy(r => r.Error.Codigo.Should().Be("PlantillaRequisitos.NoEncontrada"));
        _unitOfWork.VecesGuardado.Should().Be(0);
        plantilla.Activa.Should().BeTrue();
        requisito.Activo.Should().BeTrue();
        requisito.BloqueaAcceso.Should().BeTrue();
        _tipos.Tipos.Should().NotContain(t => t.Nombre == "Permiso de trabajo propio");
    }

    [Fact]
    public async Task Una_version_vieja_es_un_conflicto_y_no_guarda()
    {
        var plantilla = PlantillaDe(PlantillaRequisitos.ParaCentro(_centro.Id));
        var versionVieja = plantilla.Version;
        plantilla.AnadirRequisito(TipoGuardado(), null, Basicos);

        var resultado = await Anadir(Total()).Handle(
            new AnadirRequisitoAPlantillaCommand(plantilla.Id, TipoGuardado().Id, null, Basicos, versionVieja), CancellationToken.None);

        resultado.Error.Codigo.Should().Be(ConcurrenciaOptimista.CodigoConflicto);
        _unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Un_tipo_o_un_requisito_de_base_inexistentes_o_pendientes_de_revision_legal_se_rechazan()
    {
        var plantilla = PlantillaDe(PlantillaRequisitos.ParaCentro(_centro.Id));
        var pendiente = new RequisitoBase("a1-prueba", "A1 de prueba", "Fundamento", NivelExigencia.N1, pendienteRevisionLegal: true);
        _catalogo.Requisitos.Add(pendiente);

        (await Anadir(Total()).Handle(new AnadirRequisitoAPlantillaCommand(plantilla.Id, Guid.NewGuid(), null, Basicos), CancellationToken.None))
            .Error.Codigo.Should().Be("PlantillaRequisitos.TipoDocumentoNoEncontrado");
        (await Anadir(Total()).Handle(new AnadirRequisitoAPlantillaCommand(plantilla.Id, TipoGuardado().Id, Guid.NewGuid(), Basicos), CancellationToken.None))
            .Error.Codigo.Should().Be("PlantillaRequisitos.RequisitoBaseNoEncontrado");
        (await Anadir(Total()).Handle(
                new AnadirRequisitoAPlantillaCommand(plantilla.Id, TipoGuardado().Id, pendiente.Id, Basicos with { Origen = OrigenRequisito.LegalN1 }),
                CancellationToken.None))
            .Error.Codigo.Should().Be("PlantillaRequisitos.Invalido", "R-14: A1 y ETT no se adoptan hasta la revisión legal");
        plantilla.Requisitos.Should().BeEmpty();
        _unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task R6_una_tolerancia_en_formacion_PRL_se_rechaza_en_el_comando()
    {
        var plantilla = PlantillaDe(PlantillaRequisitos.ParaCentro(_centro.Id));

        var resultado = await Anadir(Total()).Handle(
            new AnadirRequisitoAPlantillaCommand(plantilla.Id, TipoGuardado(GrupoTipoDocumento.FormacionPrl).Id, null, Basicos with { ToleranciaDias = 10 }),
            CancellationToken.None);

        resultado.Error.Codigo.Should().Be("PlantillaRequisitos.Invalido");
        _unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Editar_y_retirar_un_requisito_cambian_lo_suyo()
    {
        var plantilla = PlantillaDe(PlantillaRequisitos.ParaCentro(_centro.Id));
        var requisito = plantilla.AnadirRequisito(TipoGuardado(), null, Basicos);

        (await new EditarRequisitoPlantillaCommandHandler(_plantillas, _tipos, _catalogo, Acotado(), _unitOfWork).Handle(
                new EditarRequisitoPlantillaCommand(plantilla.Id, requisito.Id, null, Basicos with { BloqueaAcceso = false }), CancellationToken.None))
            .EsExitoso.Should().BeTrue();
        requisito.BloqueaAcceso.Should().BeFalse();

        (await new RetirarRequisitoPlantillaCommandHandler(_plantillas, Acotado(), _unitOfWork).Handle(
                new RetirarRequisitoPlantillaCommand(plantilla.Id, requisito.Id), CancellationToken.None))
            .EsExitoso.Should().BeTrue();
        requisito.Activo.Should().BeFalse();
        _unitOfWork.VecesGuardado.Should().Be(2);
    }

    // ── Requisito propio ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Un_requisito_propio_crea_su_tipo_de_documento_con_sus_alias_y_fuerza_el_origen()
    {
        var plantilla = PlantillaDe(PlantillaRequisitos.ParaCentro(_centro.Id));

        var resultado = await new CrearRequisitoPropioCommandHandler(_plantillas, _tipos, Acotado(), _unitOfWork).Handle(
            new CrearRequisitoPropioCommand(plantilla.Id, "Permiso de trabajo en caliente", AmbitoAplicacion.Trabajador,
                ["Permiso de soldadura"], "Lo pide el Centro", Basicos with { Origen = OrigenRequisito.LegalN1 }),
            CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        var tipo = _tipos.Tipos.Should().ContainSingle(t => t.Nombre == "Permiso de trabajo en caliente").Subject;
        tipo.Aliases.Select(a => a.Texto).Should().Equal("Permiso de soldadura");
        tipo.Requerido.Should().Be(RequisitoDocumental.No);
        tipo.Naturaleza.Should().Be(NaturalezaJuridica.RequisitoCliente);
        var requisito = plantilla.Requisitos.Should().ContainSingle().Subject;
        requisito.Origen.Should().Be(OrigenRequisito.PropioTenant, "quien llama no elige el origen de un requisito propio");
        requisito.TipoDocumentoId.Should().Be(tipo.Id);
        requisito.RequisitoBaseId.Should().BeNull();
    }

    [Fact]
    public async Task D15_un_requisito_propio_en_las_exigencias_del_Cliente_empresarial_es_exigencia_N3()
    {
        var plantilla = PlantillaDe(PlantillaRequisitos.ParaExigenciasDe(_cliente.Id));

        var resultado = await new CrearRequisitoPropioCommandHandler(_plantillas, _tipos, Acotado(), _unitOfWork).Handle(
            new CrearRequisitoPropioCommand(plantilla.Id, "Curso interno del Cliente empresarial", AmbitoAplicacion.Trabajador, null, null, Basicos),
            CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        plantilla.Requisitos.Should().ContainSingle().Which.Origen.Should().Be(OrigenRequisito.ExigenciaN3);
    }

    [Fact]
    public async Task Un_requisito_propio_con_un_nombre_que_ya_existe_o_con_tolerancia_no_crea_nada()
    {
        var plantilla = PlantillaDe(PlantillaRequisitos.ParaCentro(_centro.Id));
        var existente = TipoGuardado();
        var handler = new CrearRequisitoPropioCommandHandler(_plantillas, _tipos, Total(), _unitOfWork);

        (await handler.Handle(new CrearRequisitoPropioCommand(plantilla.Id, existente.Nombre, AmbitoAplicacion.Trabajador, null, null, Basicos),
                CancellationToken.None))
            .Error.Codigo.Should().Be("PlantillaRequisitos.NombreDuplicado");
        (await handler.Handle(new CrearRequisitoPropioCommand(plantilla.Id, "Nuevo con tolerancia", AmbitoAplicacion.Trabajador, null, null,
                Basicos with { ToleranciaDias = 10 }), CancellationToken.None))
            .Error.Codigo.Should().Be("PlantillaRequisitos.Invalido", "nace con la sensibilidad de salud: sin tolerancia (R-6)");

        _tipos.Tipos.Should().ContainSingle();
        plantilla.Requisitos.Should().BeEmpty();
        _unitOfWork.VecesGuardado.Should().Be(0);
    }

    // ── Lecturas ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task El_listado_solo_ensena_lo_de_la_cartera_y_las_plantillas_de_todo_el_Tenant()
    {
        var porActividad = PlantillaDe(PlantillaRequisitos.ParaActividad(_actividad));
        var deCentro = PlantillaDe(PlantillaRequisitos.ParaCentro(_centro.Id));
        PlantillaDe(PlantillaRequisitos.ParaCentro(_centroAjeno.Id));
        var exigencias = PlantillaDe(PlantillaRequisitos.ParaExigenciasDe(_cliente.Id));
        PlantillaDe(PlantillaRequisitos.ParaExigenciasDe(_otroCliente.Id));
        var contexto = new PlantillasRequisitosQueryContextFalso(_plantillas);

        var acotado = await new ObtenerPlantillasRequisitosQueryHandler(contexto, Acotado())
            .Handle(new ObtenerPlantillasRequisitosQuery(), CancellationToken.None);
        var total = await new ObtenerPlantillasRequisitosQueryHandler(contexto, Total())
            .Handle(new ObtenerPlantillasRequisitosQuery(), CancellationToken.None);
        var portal = await new ObtenerPlantillasRequisitosQueryHandler(contexto, Portal())
            .Handle(new ObtenerPlantillasRequisitosQuery(), CancellationToken.None);

        acotado.Elementos.Select(p => p.Id).Should().BeEquivalentTo([porActividad.Id, deCentro.Id, exigencias.Id]);
        acotado.TotalElementos.Should().Be(3);
        total.TotalElementos.Should().Be(5);
        portal.Elementos.Should().BeEmpty();
    }

    [Fact]
    public async Task El_detalle_de_una_plantilla_ajena_o_para_el_portal_es_nulo()
    {
        var ajena = PlantillaDe(PlantillaRequisitos.ParaCentro(_centroAjeno.Id));
        var propia = PlantillaDe(PlantillaRequisitos.ParaCentro(_centro.Id));
        propia.AnadirRequisito(TipoGuardado(), null, Basicos);
        var contexto = new PlantillasRequisitosQueryContextFalso(_plantillas);

        (await new ObtenerPlantillaRequisitosPorIdQueryHandler(contexto, Acotado()).Handle(new(ajena.Id), CancellationToken.None))
            .Should().BeNull();
        (await new ObtenerPlantillaRequisitosPorIdQueryHandler(contexto, Portal()).Handle(new(propia.Id), CancellationToken.None))
            .Should().BeNull();
        var detalle = await new ObtenerPlantillaRequisitosPorIdQueryHandler(contexto, Acotado()).Handle(new(propia.Id), CancellationToken.None);
        detalle!.ReferenciaId.Should().Be(_centro.Id);
        detalle.Requisitos.Should().ContainSingle();
    }

    [Fact]
    public async Task D28_la_sugerencia_encuentra_un_tipo_por_su_alias_y_un_requisito_de_base_activo()
    {
        var tipos = new TiposDocumentoQueryContextFalso();
        var conAlias = new TipoDocumentoDominio("Relación nominal de trabajadores", null, false, 0, AmbitoAplicacion.Empresa);
        var otro = new TipoDocumentoDominio("Seguro de responsabilidad civil", null, false, 0, AmbitoAplicacion.Empresa);
        tipos.ListaTiposDocumento.AddRange([conAlias, otro]);
        tipos.ListaTiposDocumentoAlias.Add(new TipoDocumentoAlias(conAlias.Id, "RNT"));
        var baseActivo = new RequisitoBase("rnt-base", "RNT", "Fundamento", NivelExigencia.N1);
        var basePendiente = new RequisitoBase("rnt-pendiente", "RNT pendiente", "Fundamento", NivelExigencia.N1, pendienteRevisionLegal: true);
        _catalogo.Requisitos.AddRange([baseActivo, basePendiente]);

        var sugerencias = await new BuscarRequisitosParecidosQueryHandler(tipos, _catalogo, Acotado())
            .Handle(new BuscarRequisitosParecidosQuery("rnt"), CancellationToken.None);
        var paraElPortal = await new BuscarRequisitosParecidosQueryHandler(tipos, _catalogo, Portal())
            .Handle(new BuscarRequisitosParecidosQuery("rnt"), CancellationToken.None);

        sugerencias.Select(s => s.Id).Should().Contain([conAlias.Id, baseActivo.Id]).And.NotContain([otro.Id]);
        sugerencias.Should().ContainSingle(s => s.Id == conAlias.Id).Which.CoincideCon.Should().Be("RNT");
        paraElPortal.Should().BeEmpty();
    }
}
