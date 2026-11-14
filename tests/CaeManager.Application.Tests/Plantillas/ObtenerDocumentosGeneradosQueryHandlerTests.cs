using CaeManager.Application.Plantillas.Queries.ObtenerDocumentosGenerados;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Documentos;
using CaeManager.Application.Tests.Proyectos;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Plantillas;
using CaeManager.Domain.Trabajadores;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Plantillas;

public class ObtenerDocumentosGeneradosQueryHandlerTests
{
    private readonly DocumentosQueryContextFalso _documentos = new();

    private static (PlantillasQueryContextFalso Contexto, PlantillaDocumento Plantilla, PlantillaDocumentoVersion Version) ConstruirPlantilla(
        AmbitoAplicacion ambito = AmbitoAplicacion.Trabajador, Guid? centroId = null, Guid? clienteId = null)
    {
        var contexto = new PlantillasQueryContextFalso();
        var plantilla = new PlantillaDocumento(OrigenPlantilla.Externa, "Ficha de riesgos", ambito, FormatoOrigenPlantilla.PdfVisual, Guid.NewGuid(),
            centroId: centroId, clienteId: clienteId);
        var version = new PlantillaDocumentoVersion(plantilla.Id, 1, "url.pdf", new string('a', 64));
        contexto.ListaPlantillasDocumento.Add(plantilla);
        contexto.ListaPlantillasDocumentoVersion.Add(version);
        return (contexto, plantilla, version);
    }

    private ObtenerDocumentosGeneradosQueryHandler Handler(PlantillasQueryContextFalso contexto, AlcanceDatosServiceFalso alcance) =>
        new(contexto, new TrabajadoresQueryContextFalso(), new EmpresasQueryContextFalso(), _documentos, new ProyectosQueryContextFalso(), alcance);

    /// <summary>
    /// Lo que guarda la generación desde una plantilla de ámbito Cliente sin Centro: el Documento es del Cliente
    /// empresarial y el registro no lleva Trabajador, Empresa ni Centro.
    /// </summary>
    private DocumentoGenerado GeneradoParaClienteEmpresarialSinCentro(PlantillaDocumentoVersion version, Guid clienteEmpresarialId)
    {
        var documento = Documento.DeCliente(clienteEmpresarialId, Guid.NewGuid(), DiaDeNegocio.Hoy(), VigenciaDocumento.NoCaduca, "generado.pdf");
        _documentos.ListaDocumentos.Add(documento);
        return new DocumentoGenerado(version.Id, documento.Id, "{}", Guid.NewGuid(), DateTime.UtcNow);
    }

    private DocumentoGenerado GeneradoParaTrabajador(PlantillaDocumentoVersion version, Guid trabajadorId, Guid? centroId = null)
    {
        var documento = Documento.DeTrabajador(trabajadorId, Guid.NewGuid(), DiaDeNegocio.Hoy(), VigenciaDocumento.NoCaduca, "generado.pdf");
        _documentos.ListaDocumentos.Add(documento);
        return new DocumentoGenerado(version.Id, documento.Id, "{}", Guid.NewGuid(), DateTime.UtcNow, trabajadorId: trabajadorId, centroId: centroId);
    }

    [Fact]
    public async Task Devuelve_los_documentos_generados_con_nombre_de_plantilla_y_trabajador()
    {
        var (contexto, plantilla, version) = ConstruirPlantilla();
        var trabajadoresContext = new TrabajadoresQueryContextFalso();
        var trabajador = Trabajador.DeEmpresa(Guid.NewGuid(), "Juan", "Pérez", "77189989B");
        trabajadoresContext.ListaTrabajadores.Add(trabajador);
        var empresasContext = new EmpresasQueryContextFalso();

        var generado = new DocumentoGenerado(version.Id, Guid.NewGuid(), "{}", Guid.NewGuid(), DateTime.UtcNow, trabajadorId: trabajador.Id);
        contexto.ListaDocumentosGenerados.Add(generado);

        var handler = new ObtenerDocumentosGeneradosQueryHandler(
            contexto, trabajadoresContext, empresasContext, _documentos, new ProyectosQueryContextFalso(), new AlcanceDatosServiceFalso());

        var resultado = await handler.Handle(new ObtenerDocumentosGeneradosQuery(), CancellationToken.None);

        var fila = resultado.Should().ContainSingle().Subject;
        fila.PlantillaNombre.Should().Be("Ficha de riesgos");
        fila.TrabajadorNombreCompleto.Should().Be("Juan Pérez");
    }

    [Fact]
    public async Task Filtra_por_plantilla()
    {
        var (contexto, plantillaUno, versionUno) = ConstruirPlantilla();
        var plantillaDos = new PlantillaDocumento(OrigenPlantilla.Externa, "Otra ficha", AmbitoAplicacion.Trabajador, FormatoOrigenPlantilla.PdfVisual, Guid.NewGuid());
        var versionDos = new PlantillaDocumentoVersion(plantillaDos.Id, 1, "url2.pdf", new string('b', 64));
        contexto.ListaPlantillasDocumento.Add(plantillaDos);
        contexto.ListaPlantillasDocumentoVersion.Add(versionDos);

        contexto.ListaDocumentosGenerados.AddRange(
        [
            new DocumentoGenerado(versionUno.Id, Guid.NewGuid(), "{}", Guid.NewGuid(), DateTime.UtcNow),
            new DocumentoGenerado(versionDos.Id, Guid.NewGuid(), "{}", Guid.NewGuid(), DateTime.UtcNow),
        ]);

        var resultado = await Handler(contexto, new AlcanceDatosServiceFalso())
            .Handle(new ObtenerDocumentosGeneradosQuery(PlantillaDocumentoId: plantillaUno.Id), CancellationToken.None);

        resultado.Should().ContainSingle(d => d.PlantillaNombre == "Ficha de riesgos");
    }

    [Fact]
    public async Task Filtra_por_trabajador()
    {
        var (contexto, _, version) = ConstruirPlantilla();
        var trabajadorBuscado = Guid.NewGuid();
        contexto.ListaDocumentosGenerados.AddRange(
        [
            new DocumentoGenerado(version.Id, Guid.NewGuid(), "{}", Guid.NewGuid(), DateTime.UtcNow, trabajadorId: trabajadorBuscado),
            new DocumentoGenerado(version.Id, Guid.NewGuid(), "{}", Guid.NewGuid(), DateTime.UtcNow, trabajadorId: Guid.NewGuid()),
        ]);

        var resultado = await Handler(contexto, new AlcanceDatosServiceFalso())
            .Handle(new ObtenerDocumentosGeneradosQuery(TrabajadorId: trabajadorBuscado), CancellationToken.None);

        resultado.Should().ContainSingle(d => d.TrabajadorId == trabajadorBuscado);
    }

    [Fact]
    public async Task Respeta_el_alcance_de_datos_del_usuario()
    {
        var (contexto, _, version) = ConstruirPlantilla();
        var trabajadorVisible = Guid.NewGuid();
        var trabajadorAjeno = Guid.NewGuid();
        contexto.ListaDocumentosGenerados.AddRange(
        [
            GeneradoParaTrabajador(version, trabajadorVisible),
            GeneradoParaTrabajador(version, trabajadorAjeno),
        ]);
        var alcance = new AlcanceDatosServiceFalso(tieneAccesoTotal: false, trabajadorIdsVisibles: [trabajadorVisible]);

        var resultado = await Handler(contexto, alcance).Handle(new ObtenerDocumentosGeneradosQuery(), CancellationToken.None);

        resultado.Should().ContainSingle(d => d.TrabajadorId == trabajadorVisible);
    }

    [Fact]
    public async Task Un_Gestor_CAE_no_ve_el_generado_sin_Trabajador_ni_Empresa_de_un_Cliente_empresarial_fuera_de_su_Cartera()
    {
        var (contexto, _, version) = ConstruirPlantilla(AmbitoAplicacion.Cliente);
        var clienteFueraDeCartera = Guid.NewGuid();
        var generado = GeneradoParaClienteEmpresarialSinCentro(version, clienteFueraDeCartera);
        contexto.ListaDocumentosGenerados.Add(generado);
        var gestorConCarteraLimitada = new AlcanceDatosServiceFalso(tieneAccesoTotal: false, clienteIdsVisibles: [Guid.NewGuid()]);

        var resultado = await Handler(contexto, gestorConCarteraLimitada).Handle(new ObtenerDocumentosGeneradosQuery(), CancellationToken.None);

        generado.TrabajadorId.Should().BeNull("control del instrumento: es el registro que dejaba pasar el filtro");
        generado.EmpresaId.Should().BeNull("control del instrumento: es el registro que dejaba pasar el filtro");
        resultado.Should().BeEmpty();
    }

    [Fact]
    public async Task Un_Gestor_CAE_ve_el_generado_sin_Trabajador_ni_Empresa_de_un_Cliente_empresarial_de_su_Cartera()
    {
        var (contexto, _, version) = ConstruirPlantilla(AmbitoAplicacion.Cliente);
        var clienteDeCartera = Guid.NewGuid();
        var deCartera = GeneradoParaClienteEmpresarialSinCentro(version, clienteDeCartera);
        contexto.ListaDocumentosGenerados.AddRange([deCartera, GeneradoParaClienteEmpresarialSinCentro(version, Guid.NewGuid())]);
        var gestorConCarteraLimitada = new AlcanceDatosServiceFalso(tieneAccesoTotal: false, clienteIdsVisibles: [clienteDeCartera]);

        var resultado = await Handler(contexto, gestorConCarteraLimitada).Handle(new ObtenerDocumentosGeneradosQuery(), CancellationToken.None);

        var fila = resultado.Should().ContainSingle().Subject;
        fila.DocumentoGeneradoId.Should().Be(deCartera.Id);
        fila.PlantillaNombre.Should().Be("Ficha de riesgos");
    }

    [Fact]
    public async Task No_se_ve_un_generado_cuyo_Documento_no_es_legible_en_el_Tenant_propietario_actual()
    {
        // RLS deja fuera del contexto el Documento de otro Tenant propietario: el registro que apuntara a él no
        // tiene Documento legible y no sale, aunque no lleve ninguna otra coordenada.
        var (contexto, _, version) = ConstruirPlantilla(AmbitoAplicacion.Cliente);
        var clienteEmpresarial = Guid.NewGuid();
        var documentoDeOtroTenant = Documento.DeCliente(clienteEmpresarial, Guid.NewGuid(), DiaDeNegocio.Hoy(), VigenciaDocumento.NoCaduca, "ajeno.pdf");
        contexto.ListaDocumentosGenerados.Add(new DocumentoGenerado(version.Id, documentoDeOtroTenant.Id, "{}", Guid.NewGuid(), DateTime.UtcNow));
        var gestorQueVeAlClienteEmpresarial = new AlcanceDatosServiceFalso(tieneAccesoTotal: false, clienteIdsVisibles: [clienteEmpresarial]);

        var resultado = await Handler(contexto, gestorQueVeAlClienteEmpresarial).Handle(new ObtenerDocumentosGeneradosQuery(), CancellationToken.None);

        resultado.Should().BeEmpty();
    }

    [Fact]
    public async Task Un_Gestor_CAE_no_ve_el_generado_en_un_Centro_fuera_de_su_Cartera_aunque_vea_al_Trabajador()
    {
        var (contexto, _, version) = ConstruirPlantilla();
        var trabajador = Guid.NewGuid();
        var centroDeCartera = Guid.NewGuid();
        var enCentroDeCartera = GeneradoParaTrabajador(version, trabajador, centroDeCartera);
        contexto.ListaDocumentosGenerados.AddRange([enCentroDeCartera, GeneradoParaTrabajador(version, trabajador, Guid.NewGuid())]);
        var alcance = new AlcanceDatosServiceFalso(tieneAccesoTotal: false, trabajadorIdsVisibles: [trabajador], centroIdsVisibles: [centroDeCartera]);

        var resultado = await Handler(contexto, alcance).Handle(new ObtenerDocumentosGeneradosQuery(), CancellationToken.None);

        resultado.Should().ContainSingle().Which.DocumentoGeneradoId.Should().Be(enCentroDeCartera.Id);
    }

    [Fact]
    public async Task Un_Gestor_CAE_no_ve_el_generado_de_una_plantilla_atada_a_un_Cliente_empresarial_fuera_de_su_Cartera()
    {
        var clienteDeCartera = Guid.NewGuid();
        var (contexto, _, version) = ConstruirPlantilla(AmbitoAplicacion.Trabajador, clienteId: Guid.NewGuid());
        var trabajador = Guid.NewGuid();
        contexto.ListaDocumentosGenerados.Add(GeneradoParaTrabajador(version, trabajador));
        var alcance = new AlcanceDatosServiceFalso(tieneAccesoTotal: false, clienteIdsVisibles: [clienteDeCartera], trabajadorIdsVisibles: [trabajador]);

        var resultado = await Handler(contexto, alcance).Handle(new ObtenerDocumentosGeneradosQuery(), CancellationToken.None);

        resultado.Should().BeEmpty();
    }

    [Fact]
    public async Task Un_Gestor_CAE_no_ve_el_generado_de_un_Centro_cuya_Empresa_esta_fuera_de_su_Cartera()
    {
        // Plantilla de ámbito Cliente generada en un Centro: el Documento es del Cliente empresarial y el registro
        // guarda el Centro y la Empresa del Centro. Ver el Cliente empresarial y el Centro no basta.
        var (contexto, _, version) = ConstruirPlantilla(AmbitoAplicacion.Cliente);
        var (clienteEmpresarial, centro, empresaDeCartera) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        DocumentoGenerado EnCentro(Guid empresaId)
        {
            var documento = Documento.DeCliente(clienteEmpresarial, Guid.NewGuid(), DiaDeNegocio.Hoy(), VigenciaDocumento.NoCaduca, "generado.pdf");
            _documentos.ListaDocumentos.Add(documento);
            return new DocumentoGenerado(version.Id, documento.Id, "{}", Guid.NewGuid(), DateTime.UtcNow, empresaId: empresaId, centroId: centro);
        }
        var conEmpresaDeCartera = EnCentro(empresaDeCartera);
        contexto.ListaDocumentosGenerados.AddRange([conEmpresaDeCartera, EnCentro(Guid.NewGuid())]);
        var alcance = new AlcanceDatosServiceFalso(
            tieneAccesoTotal: false, clienteIdsVisibles: [clienteEmpresarial], centroIdsVisibles: [centro], empresaIdsVisibles: [empresaDeCartera]);

        var resultado = await Handler(contexto, alcance).Handle(new ObtenerDocumentosGeneradosQuery(), CancellationToken.None);

        resultado.Should().ContainSingle().Which.DocumentoGeneradoId.Should().Be(conEmpresaDeCartera.Id);
    }

    [Fact]
    public async Task Con_acceso_total_no_se_pierde_ningun_generado()
    {
        var (contexto, _, version) = ConstruirPlantilla(AmbitoAplicacion.Cliente);
        contexto.ListaDocumentosGenerados.AddRange(
        [
            GeneradoParaClienteEmpresarialSinCentro(version, Guid.NewGuid()),
            GeneradoParaTrabajador(version, Guid.NewGuid(), Guid.NewGuid()),
            // Su Documento ya no está (dado de baja): con acceso total la traza se sigue viendo, como antes.
            new DocumentoGenerado(version.Id, Guid.NewGuid(), "{}", Guid.NewGuid(), DateTime.UtcNow),
        ]);

        var resultado = await Handler(contexto, new AlcanceDatosServiceFalso(tieneAccesoTotal: true))
            .Handle(new ObtenerDocumentosGeneradosQuery(), CancellationToken.None);

        resultado.Should().HaveCount(3);
    }
}
