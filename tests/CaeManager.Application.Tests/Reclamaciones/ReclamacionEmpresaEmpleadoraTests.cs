using CaeManager.Application.Alertas;
using CaeManager.Application.Centros;
using CaeManager.Application.Common;
using CaeManager.Application.Contactos;
using CaeManager.Application.Reclamaciones;
using CaeManager.Application.Reclamaciones.Commands.EnviarReclamacion;
using CaeManager.Application.Reclamaciones.Commands.EnviarReclamacionEmpresaEmpleadora;
using CaeManager.Application.Reclamaciones.Queries.ObtenerLoteReclamacion;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Common;
using CaeManager.Application.Tests.Documentos;
using CaeManager.Application.Tests.Empresas;
using CaeManager.Application.Tests.Plantillas;
using CaeManager.Application.Tests.Reportes;
using CaeManager.Application.Tests.TiposDocumento;
using CaeManager.Domain.Asignaciones;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Trabajadores;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Reclamaciones;

/// <summary>
/// «Gestionar» reclama los documentos de un Trabajador de Empresa a la Empresa que lo emplea, al contacto de reclamación de
/// su Delegación (decisión de Chris, 2026-10-18); sin Delegación o con una Delegación sin correo, a la agenda de la Empresa.
/// Ningún test envía correo: el registro y el envío son el doble <see cref="RegistroEnvioReclamacionServiceFalso"/>.
/// </summary>
public class ReclamacionEmpresaEmpleadoraTests
{
    private static readonly DateOnly Hoy = DiaDeNegocio.Hoy();
    private const string CorreoSevilla = "reclamaciones.sevilla@contratista.example";

    private readonly EmpresasQueryContextFalso _empresas = new();
    private readonly DocumentosQueryContextFalso _documentos = new();
    private readonly TrabajadoresQueryContextFalso _trabajadores = new();
    private readonly TiposDocumentoQueryContextFalso _tipos = new();
    private readonly AsignacionesQueryContextFalso _asignaciones = new();
    private readonly CentrosQueryContextFalso _centros = new();
    private readonly DelegacionEmpresaYOficioEmpresaTests.CatalogoFalso _catalogo = new();
    private readonly ResolucionDestinatariosAgendaServiceFalso _agenda = new();
    private readonly RegistroEnvioReclamacionServiceFalso _registro = new();

    private readonly Empresa _cliente = Empresa.CrearComoCliente("Cliente SA", "B12345674", false, null, null);
    private readonly Empresa _contratista = new("Contratista SL", "B12345674");
    private readonly Centro _centro;
    private readonly TipoDocumento _tipo = new("Ficha de riesgos", null, false, 1, AmbitoAplicacion.Trabajador);
    private readonly DelegacionEmpresa _sevilla;
    private readonly DelegacionEmpresa _malagaSinCorreo;
    private readonly Trabajador _anaSevilla;
    private readonly Trabajador _beaSevilla;
    private readonly Trabajador _carlosSinDelegacion;
    private readonly Trabajador _davidMalaga;
    private readonly Trabajador _saraSubcontrata;

    private static readonly DestinatarioAgendaDto ContactoAgendaEmpresa =
        new(Guid.NewGuid(), "Agenda Contratista", "agenda@contratista.example", ["Ficha de riesgos"]);

    public ReclamacionEmpresaEmpleadoraTests()
    {
        _centro = new Centro(_cliente.Id, _contratista.Id, "Centro Norte");
        _sevilla = new DelegacionEmpresa(_contratista.Id, "Sevilla");
        _sevilla.FijarContactoReclamacion("Marta Sevilla", CorreoSevilla, null);
        _malagaSinCorreo = new DelegacionEmpresa(_contratista.Id, "Málaga");
        _malagaSinCorreo.FijarContactoReclamacion("Luis Málaga", null, "600000000");
        _catalogo.Agregar(_sevilla);
        _catalogo.Agregar(_malagaSinCorreo);

        _anaSevilla = DeContratista("Ana", "00000000T", _sevilla);
        _beaSevilla = DeContratista("Bea", "00000001R", _sevilla);
        _carlosSinDelegacion = DeContratista("Carlos", "00000002W", null);
        _davidMalaga = DeContratista("David", "00000003A", _malagaSinCorreo);
        _saraSubcontrata = Trabajador.DeSubcontrata(Guid.NewGuid(), "Sara", "Ruiz", "12345678Z");
        _trabajadores.ListaTrabajadores.Add(_saraSubcontrata);

        _empresas.ListaEmpresas.Add(_cliente);
        _empresas.ListaEmpresas.Add(_contratista);
        _centros.ListaCentros.Add(_centro);
        _tipos.ListaTiposDocumento.Add(_tipo);
        _agenda.RespuestaResolverParaEmpresaAsync = [ContactoAgendaEmpresa];
    }

    private Trabajador DeContratista(string nombre, string dni, DelegacionEmpresa? delegacion)
    {
        var trabajador = Trabajador.DeEmpresa(_contratista.Id, nombre, "Pérez", dni);
        if (delegacion is not null)
            trabajador.FijarDelegacionEmpresaYOficio(delegacion, null);
        _trabajadores.ListaTrabajadores.Add(trabajador);
        _asignaciones.ListaAsignaciones.Add(new Asignacion(trabajador.Id, _centro.Id, new DateOnly(2026, 1, 1)));
        return trabajador;
    }

    private ReclamacionEmpresaEmpleadoraService Servicio(AlcanceDatosServiceFalso? alcance = null, string rol = "GestorCae") =>
        new(_trabajadores, _empresas, _catalogo, alcance ?? new AlcanceDatosServiceFalso(), PoliticaNotaInternaPruebas.Con(rol), _agenda);

    private EnviarReclamacionEmpresaEmpleadoraCommandHandler Handler(AlcanceDatosServiceFalso? alcance = null, string rol = "GestorCae")
    {
        alcance ??= new AlcanceDatosServiceFalso();
        var pendientes = new PendientesDeReclamacionService(
            _documentos, _tipos, _trabajadores, _asignaciones, _centros, _empresas,
            new DocumentosFaltantesService(_tipos, _documentos, _centros),
            new EvaluacionDeAccesoPorCentroService(_centros, _tipos, _trabajadores, _asignaciones, _documentos, alcance),
            alcance);
        return new(_empresas, _documentos, _trabajadores, _tipos, _asignaciones, _centros, alcance, Servicio(alcance, rol), _registro, pendientes);
    }

    private Documento DocumentoQueVence(Trabajador trabajador)
    {
        var documento = Documento.DeTrabajador(trabajador.Id, _tipo.Id, Hoy.AddYears(-1), VigenciaDocumento.VenceEl(Hoy.AddDays(10)));
        _documentos.ListaDocumentos.Add(documento);
        return documento;
    }

    private DocumentoReclamableDto Reclamable(Trabajador trabajador) =>
        new(Guid.NewGuid(), trabajador.Id, trabajador.Nombre, _tipo.Id, _tipo.Nombre, Hoy.AddDays(10), EstadoDocumento.Proximo);

    private LoteReclamacionClienteDto LoteDelCliente(params DocumentoReclamableDto[] documentos) =>
        new(_cliente.Id, _cliente.RazonSocial, null, documentos);

    [Fact]
    public async Task Dos_Trabajadores_de_la_misma_Delegacion_salen_en_un_unico_lote_a_su_contacto_de_reclamacion()
    {
        var reagrupados = await Servicio().ReagruparAsync([LoteDelCliente(Reclamable(_anaSevilla), Reclamable(_beaSevilla))], CancellationToken.None);

        reagrupados.LotesCliente.Should().BeEmpty("lo de un Trabajador de Empresa nunca se reclama al Cliente del Centro");
        var lote = reagrupados.LotesEmpresaEmpleadora.Should().ContainSingle().Subject;
        lote.TitularId.Should().Be(_contratista.Id);
        lote.Documentos.Should().HaveCount(2);
        lote.EmpresaEmpleadora.Should().Be(new DestinoEmpresaEmpleadoraDto(_sevilla.Id, "Sevilla"));
        var destinatario = lote.Destinatarios.Should().ContainSingle().Subject;
        destinatario.Email.Should().Be(CorreoSevilla);
        destinatario.Nombre.Should().Be("Marta Sevilla");
    }

    [Fact]
    public async Task Sin_Delegacion_o_con_una_Delegacion_sin_correo_va_a_la_agenda_de_la_Empresa_en_un_solo_lote()
    {
        var reagrupados = await Servicio().ReagruparAsync(
            [LoteDelCliente(Reclamable(_carlosSinDelegacion), Reclamable(_davidMalaga), Reclamable(_anaSevilla))], CancellationToken.None);

        reagrupados.LotesEmpresaEmpleadora.Should().HaveCount(2);
        var aLaAgenda = reagrupados.LotesEmpresaEmpleadora.Single(l => l.EmpresaEmpleadora!.DelegacionEmpresaId is null);
        aLaAgenda.Documentos.Select(d => d.TrabajadorId).Should().BeEquivalentTo([_carlosSinDelegacion.Id, _davidMalaga.Id]);
        aLaAgenda.Destinatarios.Should().Equal(ContactoAgendaEmpresa);
        _agenda.UltimaLlamadaResolverParaEmpresasAsync!.Keys.Should().Equal(_contratista.Id);
    }

    [Fact]
    public async Task Lo_de_un_Trabajador_de_Subcontrata_sigue_yendo_al_Cliente()
    {
        var deSubcontrata = Reclamable(_saraSubcontrata);

        var reagrupados = await Servicio().ReagruparAsync([LoteDelCliente(deSubcontrata, Reclamable(_anaSevilla))], CancellationToken.None);

        reagrupados.LotesCliente.Should().ContainSingle().Which.Documentos.Should().Equal(deSubcontrata);
        reagrupados.LotesEmpresaEmpleadora.Should().ContainSingle().Which.TitularId.Should().Be(_contratista.Id);
    }

    [Fact]
    public async Task Una_Empresa_fuera_de_la_Asignacion_de_Cartera_no_se_ofrece_ni_a_ella_ni_al_Cliente()
    {
        var fueraDeCartera = new AlcanceDatosServiceFalso(tieneAccesoTotal: false, empresaIdsParaGestion: []);

        var reagrupados = await Servicio(fueraDeCartera).ReagruparAsync([LoteDelCliente(Reclamable(_anaSevilla))], CancellationToken.None);

        reagrupados.LotesEmpresaEmpleadora.Should().BeEmpty();
        reagrupados.LotesCliente.Should().BeEmpty("caer de vuelta al Cliente reclamaría a quien la decisión excluye");
    }

    [Fact]
    public async Task Quien_no_lee_la_nota_interna_recibe_el_lote_de_la_Delegacion_sin_destinatario()
    {
        var reagrupados = await Servicio(rol: "Cliente").ReagruparAsync([LoteDelCliente(Reclamable(_anaSevilla))], CancellationToken.None);

        var lote = reagrupados.LotesEmpresaEmpleadora.Should().ContainSingle().Subject;
        lote.EmpresaEmpleadora!.DelegacionEmpresaId.Should().Be(_sevilla.Id);
        lote.Destinatarios.Should().BeEmpty("el contacto de la Delegación es un dato personal que solo ve quien lee la nota interna");
    }

    [Fact]
    public async Task El_envio_a_la_Delegacion_registra_a_la_Empresa_con_documentos_de_Trabajador_y_va_al_contacto_de_reclamacion()
    {
        var documento = DocumentoQueVence(_anaSevilla);
        var comando = new EnviarReclamacionEmpresaEmpleadoraCommand(_contratista.Id, _sevilla.Id, [documento.Id], ContactoIdsSeleccionados: [_sevilla.Id]);

        var preparada = await Handler().PrepararAsync(comando, CancellationToken.None);
        var resultado = await Handler().Handle(comando, CancellationToken.None);

        preparada.EsExitoso.Should().BeTrue(preparada.EsFallido ? preparada.Error.Mensaje : null);
        preparada.Valor.EmpresaEmpleadora.Should().Be(new DestinoEmpresaEmpleadoraDto(_sevilla.Id, "Sevilla"));
        resultado.EsExitoso.Should().BeTrue(resultado.EsFallido ? resultado.Error.Mensaje : null);
        _registro.VecesLlamado.Should().Be(1);
        _registro.UltimoTitular.Should().Be(new TitularReclamacion(_contratista.Id, _contratista.RazonSocial, AmbitoAplicacion.Empresa, DocumentosDeTrabajadores: true));
        _registro.UltimosDestinatarios.Should().Equal(CorreoSevilla);
        _registro.UltimosDocumentoIds.Should().Equal(documento.Id);
    }

    [Fact]
    public async Task El_envio_a_una_Empresa_fuera_de_la_Asignacion_de_Cartera_se_rechaza_sin_enviar()
    {
        var documento = DocumentoQueVence(_anaSevilla);
        var fueraDeCartera = new AlcanceDatosServiceFalso(tieneAccesoTotal: false, empresaIdsParaGestion: []);

        var resultado = await Handler(fueraDeCartera).Handle(
            new EnviarReclamacionEmpresaEmpleadoraCommand(_contratista.Id, _sevilla.Id, [documento.Id]), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Reclamacion.SinAcceso");
        _registro.VecesLlamado.Should().Be(0);
    }

    [Fact]
    public async Task Sin_leer_la_nota_interna_el_envio_a_la_Delegacion_se_rechaza_sin_enviar()
    {
        var documento = DocumentoQueVence(_anaSevilla);

        var resultado = await Handler(rol: "Cliente").Handle(
            new EnviarReclamacionEmpresaEmpleadoraCommand(_contratista.Id, _sevilla.Id, [documento.Id]), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Reclamacion.SinAcceso");
        _registro.VecesLlamado.Should().Be(0);
    }

    [Fact]
    public async Task Una_Delegacion_que_perdio_el_correo_hace_fallar_el_envio_entero()
    {
        var documento = DocumentoQueVence(_anaSevilla);
        _sevilla.FijarContactoReclamacion("Marta Sevilla", null, null);

        var resultado = await Handler().Handle(
            new EnviarReclamacionEmpresaEmpleadoraCommand(_contratista.Id, _sevilla.Id, [documento.Id]), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Reclamacion.DelegacionEmpresaDesactualizada");
        _registro.VecesLlamado.Should().Be(0);
    }

    [Fact]
    public async Task Un_documento_de_un_Trabajador_de_otro_destino_no_viaja_en_el_lote_de_la_Delegacion()
    {
        var deSevilla = DocumentoQueVence(_anaSevilla);
        var sinDelegacion = DocumentoQueVence(_carlosSinDelegacion);

        var resultado = await Handler().Handle(
            new EnviarReclamacionEmpresaEmpleadoraCommand(_contratista.Id, _sevilla.Id, [deSevilla.Id, sinDelegacion.Id]), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Reclamacion.DocumentosDesactualizados");
        _registro.VecesLlamado.Should().Be(0);
    }

    [Fact]
    public async Task El_envio_sin_Delegacion_va_a_la_agenda_de_la_Empresa()
    {
        var documento = DocumentoQueVence(_carlosSinDelegacion);

        var resultado = await Handler().Handle(
            new EnviarReclamacionEmpresaEmpleadoraCommand(_contratista.Id, null, [documento.Id]), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue(resultado.EsFallido ? resultado.Error.Mensaje : null);
        _registro.UltimosDestinatarios.Should().Equal(ContactoAgendaEmpresa.Email);
        _agenda.UltimaLlamadaResolverParaEmpresaAsync!.Value.EmpresaId.Should().Be(_contratista.Id);
    }
}
