using CaeManager.Application.Alertas;
using CaeManager.Application.Centros.Commands.EstablecerDocumentacionRequeridaCentro;
using CaeManager.Application.Documentos.Commands.CrearDocumento;
using CaeManager.Application.Documentos.Commands.RenovarDocumento;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Plantillas;
using CaeManager.Application.Tests.Proyectos;
using CaeManager.Application.Tests.Reportes;
using CaeManager.Application.Tests.TiposDocumento;
using CaeManager.Application.Tests.Visitas;
using CaeManager.Application.Vehiculos;
using CaeManager.Application.Visitas;
using CaeManager.Application.Visitas.Antelacion;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Common;
using CaeManager.Domain.Configuracion;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Trabajadores;
using CaeManager.Domain.Visitas;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CaeManager.Application.Tests.Documentos;

/// <summary>
/// Catálogo de Tipos de Documento revisado el 2026-10-17: un tipo <b>archivado</b> ya no admite documentos nuevos ni se
/// empieza a exigir, y ya no avisa; un documento en <b>renuncia</b> nunca acredita. Una prueba por superficie: si alguien
/// retira la guarda de una sola, su prueba se pone en rojo. Cada caso de fallo lleva su control positivo (el mismo
/// escenario con el tipo sin archivar, o con un documento que no es renuncia, sí pasa), para que el rojo no se deba a
/// otra cosa. Contextos falsos, sin Postgres.
/// </summary>
public class TipoArchivadoYRenunciaEnSuperficiesTests
{
    private static readonly DateOnly Hoy = DiaDeNegocio.Hoy();

    // ---- Alta, renovación y exigencia en Centro ----

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Crear_un_documento_de_un_tipo_archivado_falla_y_con_el_tipo_sin_archivar_no(bool archivado)
    {
        var mundo = new MundoAcreditaciones();
        var trabajador = mundo.Trabajador();
        var tipo = mundo.Tipo(RequisitoDocumental.No);
        if (archivado) tipo.Archivar(null);
        var documentos = new DocumentoRepositorioFalso();
        var handler = new CrearDocumentoCommandHandler(
            documentos, mundo.TiposDocumentoContexto, mundo.TrabajadoresContexto, new VehiculosVacios(),
            new ProyectosQueryContextFalso(), new EmpresasQueryContextFalso(), new UnitOfWorkFalso(),
            new CaminosDeAltaAcreditacionesTests.ColaAnalisisSinUso(), new CurrentUserServiceFalso(), mundo.Servicio(),
            new PublisherFalso(), new AlcanceDatosServiceFalso());

        var resultado = await handler.Handle(
            new CrearDocumentoCommand(trabajador.Id, null, null, null, null, tipo.Id, Hoy, null, null, null),
            CancellationToken.None);

        if (archivado)
        {
            resultado.EsExitoso.Should().BeFalse("un tipo archivado ya no admite documentos nuevos");
            resultado.Error.Codigo.Should().Be("Documento.TipoDocumentoArchivado");
            documentos.Documentos.Should().BeEmpty();
        }
        else
        {
            resultado.EsExitoso.Should().BeTrue("control: el mismo alta con el tipo sin archivar sí se hace");
            documentos.Documentos.Should().ContainSingle();
        }
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public async Task Renovar_un_documento_de_un_tipo_archivado_falla_con_archivo_nuevo_y_corrigiendo_fechas(
        bool archivado, bool conArchivoNuevo)
    {
        var mundo = new MundoAcreditaciones();
        var tipo = mundo.Tipo(RequisitoDocumental.No, AmbitoAplicacion.Empresa, "Certificado");
        var documento = mundo.DocumentoDeEmpresa(tipo);
        var documentos = new DocumentoRepositorioFalso();
        documentos.Documentos.Add(documento);
        var versionAntes = documento.Version;
        if (archivado) tipo.Archivar(null);
        var handler = new RenovarDocumentoCommandHandler(
            documentos, mundo.TiposDocumentoContexto, new AlcanceDatosServiceFalso(), new ProyectosQueryContextFalso(),
            new CaminosDeAltaAcreditacionesTests.ColaAnalisisSinUso(), new CurrentUserServiceFalso(), mundo.Acreditaciones,
            mundo.Servicio(), new PublisherFalso(), new UnitOfWorkFalso());

        var resultado = await handler.Handle(
            new RenovarDocumentoCommand(documento.Id, Hoy, null, conArchivoNuevo ? "blob://renovado.pdf" : null, null, NoCaduca: true),
            CancellationToken.None);

        if (archivado)
        {
            resultado.EsExitoso.Should().BeFalse("un tipo archivado no se renueva: se sube con su sustituto");
            resultado.Error.Codigo.Should().Be("Documento.TipoDocumentoArchivado");
            documentos.Documentos.Should().ContainSingle("no nace ningún documento nuevo");
            documento.Version.Should().Be(versionAntes, "el documento no se toca");
        }
        else
        {
            resultado.EsExitoso.Should().BeTrue("control: el mismo caso con el tipo sin archivar sí se renueva");
        }
    }

    [Fact]
    public async Task Exigir_en_un_centro_un_tipo_archivado_falla_y_dejar_de_exigirlo_no()
    {
        var mundo = new MundoAcreditaciones();
        var centro = mundo.Centro();
        mundo.AccesoPlataforma(centro);
        var tipo = mundo.Tipo(RequisitoDocumental.No);
        tipo.Archivar(null);
        var handler = new EstablecerDocumentacionRequeridaCentroCommandHandler(
            new TipoDocumentoCentroRepositorioFalso(), mundo.CentrosContexto, mundo.TiposDocumentoContexto,
            mundo.Servicio(), new AlcanceDatosServiceFalso(), new UnitOfWorkFalso());

        var exigir = await handler.Handle(
            new EstablecerDocumentacionRequeridaCentroCommand(centro.Id, tipo.Id, Incluido: true, null, false, null, null, ToleranciaDias: null),
            CancellationToken.None);
        var dejarDeExigir = await handler.Handle(
            new EstablecerDocumentacionRequeridaCentroCommand(centro.Id, tipo.Id, Incluido: false, null, false, null, null, ToleranciaDias: null),
            CancellationToken.None);

        exigir.EsExitoso.Should().BeFalse("un tipo archivado no se empieza a exigir: se exige su sustituto");
        exigir.Error.Codigo.Should().Be("DocumentacionRequeridaCentro.TipoDocumentoArchivado");
        dejarDeExigir.EsExitoso.Should().BeTrue("limpiar la configuración que ya lo tenía sí se permite");
    }

    // ---- Alertas ----

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Las_alertas_de_trabajador_no_incluyen_documentos_de_un_tipo_archivado(bool archivado)
    {
        var mundo = new MundoAcreditaciones();
        var trabajador = mundo.Trabajador();
        var tipo = mundo.Tipo(RequisitoDocumental.Si);
        var documento = mundo.DocumentoDe(trabajador, tipo);
        if (archivado) tipo.Archivar(null);

        var alertas = DocumentosConAlertaDeVigencia.DeTrabajador(
            mundo.DocumentosContexto, mundo.TrabajadoresContexto, mundo.TiposDocumentoContexto,
            new ProyectosQueryContextFalso(), null, Hoy).ToList();

        if (archivado)
            alertas.Should().BeEmpty("el tipo archivado ya no se exige: no debe seguir avisando");
        else
            alertas.Should().ContainSingle().Which.Id.Should().Be(documento.Id, "control: con el tipo sin archivar avisa");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Las_alertas_de_empresa_no_incluyen_documentos_de_un_tipo_archivado(bool archivado)
    {
        var mundo = new MundoAcreditaciones();
        var empresas = new EmpresasQueryContextFalso();
        var empresa = Empresa.CrearComoCliente("Titular Demo SA", "B12345674", esCritico: false, notas: null, ejecutivoUsuarioId: null);
        empresas.ListaEmpresas.Add(empresa);
        var tipo = mundo.Tipo(RequisitoDocumental.Si, AmbitoAplicacion.Empresa, "Seguro RC");
        var documento = Documento.DeEmpresa(empresa.Id, tipo.Id, Hoy.AddYears(-1), VigenciaDocumento.VenceEl(Hoy.AddDays(5)));
        mundo.DocumentosContexto.ListaDocumentos.Add(documento);
        if (archivado) tipo.Archivar(null);

        var alertas = DocumentosConAlertaDeVigencia.DeEmpresa(
            mundo.DocumentosContexto, empresas, mundo.TiposDocumentoContexto, null,
            DocumentosConAlertaDeVigencia.FechaLimite(Hoy, new ParametroSistema(umbralAmbarDias: 30, umbralRojoDias: 7))).ToList();

        if (archivado)
            alertas.Should().BeEmpty("el tipo archivado ya no se exige: no debe seguir avisando");
        else
            alertas.Should().ContainSingle().Which.Id.Should().Be(documento.Id, "control: con el tipo sin archivar avisa");
    }

    // ---- Expediente de la Visita ----

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task El_expediente_de_la_visita_no_se_cubre_con_un_documento_en_renuncia(bool esRenuncia, bool completoEsperado)
    {
        var titular = Empresa.CrearComoCliente("Titular Demo SA", "B12345674", esCritico: false, notas: null, ejecutivoUsuarioId: null);
        var proveedora = new Empresa("Contratista Demo SL", "B12345674");
        var centro = new Centro(titular.Id, proveedora.Id, "Nave Norte");
        var trabajador = Trabajador.DeEmpresa(proveedora.Id, "Ana", "García", "12345678Z");
        var tipo = new TipoDocumento(
            "Aptitud médica", vigenciaMeses: null, aplicaVencimientoAutomatico: false, orden: 1,
            AmbitoAplicacion.Trabajador, RequisitoDocumental.Si);
        tipo.EstablecerDatosAdicionales(DatosAdicionalesDocumento.Renuncia);
        var documento = Documento.DeTrabajador(trabajador.Id, tipo.Id, Hoy.AddDays(-30), VigenciaDocumento.VenceEl(Hoy.AddYears(1)));
        documento.EstablecerDatosAdicionales(tipo, null, null, esRenuncia);

        var centros = new CentrosQueryContextFalso();
        centros.ListaCentros.Add(centro);
        var documentos = new DocumentosQueryContextFalso();
        documentos.ListaDocumentos.Add(documento);
        var tipos = new TiposDocumentoQueryContextFalso();
        tipos.ListaTiposDocumento.Add(tipo);
        var trabajadores = new TrabajadoresQueryContextFalso();
        trabajadores.ListaTrabajadores.Add(trabajador);
        var configuracion = new ConfiguracionQueryContextFalso();
        configuracion.ListaParametrosSistema.Add(new ParametroSistema(umbralAmbarDias: 30, umbralRojoDias: 7));
        var visita = new Visita(centro.Id, Hoy.AddDays(3), Hoy.AddDays(3), null);
        visita.RegistrarOrigenSolicitud(Guid.NewGuid(), DateTime.UtcNow.AddHours(-1));
        var visitas = new VisitasQueryContextFalso();
        visitas.ListaVisitas.Add(visita);
        visitas.ListaVisitasTrabajadores.Add(new VisitaTrabajador(visita.Id, trabajador.Id));
        var repositorio = new VisitaRepositorioFalso();
        repositorio.Visitas.Add(visita);

        var sellado = await new EvaluadorExpedienteVisitaService(
                visitas, centros, documentos, tipos, trabajadores, configuracion, repositorio, new UnitOfWorkFalso(),
                NullLogger<EvaluadorExpedienteVisitaService>.Instance)
            .EvaluarAsync(visita.Id);

        sellado.Should().Be(completoEsperado, esRenuncia
            ? "una renuncia no acredita: el tipo requerido queda sin cubrir"
            : "control: el mismo documento sin renuncia sí cubre el tipo");
    }

    private sealed class VisitasQueryContextFalso : IVisitasQueryContext
    {
        public List<Visita> ListaVisitas { get; } = [];
        public List<VisitaTrabajador> ListaVisitasTrabajadores { get; } = [];

        public IQueryable<Visita> Visitas => new Integraciones.TestAsyncQueryable<Visita>(ListaVisitas.AsQueryable());
        public IQueryable<VisitaTrabajador> VisitasTrabajadores => new Integraciones.TestAsyncQueryable<VisitaTrabajador>(ListaVisitasTrabajadores.AsQueryable());
    }

    private sealed class VehiculosVacios : IVehiculosQueryContext
    {
        public IQueryable<Domain.Vehiculos.Vehiculo> Vehiculos =>
            new Integraciones.TestAsyncQueryable<Domain.Vehiculos.Vehiculo>(Array.Empty<Domain.Vehiculos.Vehiculo>().AsQueryable());
    }
}
