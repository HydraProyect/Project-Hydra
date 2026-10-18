using CaeManager.Application.Documentos.Queries.ObtenerPropagacionesPendientes;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Plantillas;
using CaeManager.Application.Tests.Proyectos;
using CaeManager.Application.Tests.Reportes;
using CaeManager.Application.Tests.TiposDocumento;
using CaeManager.Domain.Asignaciones;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Trabajadores;
using FluentAssertions;

namespace CaeManager.Application.Tests.Documentos;

public class ObtenerPropagacionesPendientesTests
{
    private sealed class Escenario
    {
        public DocumentosQueryContextFalso Documentos { get; } = new();
        public CentrosQueryContextFalso Centros { get; } = new();
        public AsignacionesQueryContextFalso Asignaciones { get; } = new();
        public TrabajadoresQueryContextFalso Trabajadores { get; } = new();
        public TiposDocumentoQueryContextFalso Tipos { get; } = new();
        public PropagacionesTests.Repositorio Propagaciones { get; } = new();
        public Trabajador Persona { get; } = Trabajador.DeEmpresa(Guid.NewGuid(), "Ana", "Pérez", "12345678Z");
        public Centro Centro { get; } = new(Guid.NewGuid(), Guid.NewGuid(), "Centro Norte");
        public TipoDocumento Tipo { get; } = new("RNT", null, false, 1, AmbitoAplicacion.Trabajador, RequisitoDocumental.Si);
        public Documento Documento { get; }
        public CanalGestionDocumental Canal { get; }

        public Escenario()
        {
            Documento = Documento.DeTrabajador(Persona.Id, Tipo.Id, new DateOnly(2026, 10, 1), VigenciaDocumento.NoCaduca);
            Documento.AdjuntarArchivo("documentos/prueba.pdf");
            Canal = CanalGestionDocumental.PorEmail(Centro.Id, "Avisos", "centro@example.test", null);
            Documentos.ListaDocumentos.Add(Documento);
            Documentos.ListaAprobacionesDocumento.Add(AprobacionDocumento.CrearManual(Documento.Id, 100, Guid.NewGuid()));
            Centros.ListaCentros.Add(Centro);
            Centros.ListaCanalesGestionDocumental.Add(Canal);
            Trabajadores.ListaTrabajadores.Add(Persona);
            Asignaciones.ListaAsignaciones.Add(new(Persona.Id, Centro.Id, new DateOnly(2026, 10, 1)));
            Tipos.ListaTiposDocumento.Add(Tipo);
        }

        public Task<IReadOnlyList<PropagacionPendienteDto>> Consultar(AlcanceDatosServiceFalso? alcance = null) =>
            new ObtenerPropagacionesPendientesQueryHandler(Documentos, Centros, Asignaciones, Trabajadores, Tipos,
                Propagaciones, alcance ?? new(), new ProyectosQueryContextFalso()).Handle(new(), CancellationToken.None);
    }

    [Fact]
    public async Task Solo_los_documentos_aprobados_con_actividad_y_exigencia_tienen_entregas_pendientes()
    {
        var e = new Escenario();
        (await e.Consultar()).Should().ContainSingle().Which.DocumentoId.Should().Be(e.Documento.Id);
        e.Documentos.ListaAprobacionesDocumento.Clear();
        (await e.Consultar()).Should().BeEmpty();
        e.Documentos.ListaAprobacionesDocumento.Add(AprobacionDocumento.CrearAutomatica(e.Documento.Id, 100));
        e.Tipos.ListaTiposDocumentoCentros.Add(new(e.Tipo.Id, e.Centro.Id, incluido: false));
        (await e.Consultar()).Should().BeEmpty();
    }

    [Fact]
    public async Task Un_Centro_fuera_de_cartera_no_devuelve_documentos_ni_destinatarios()
    {
        var e = new Escenario();
        (await e.Consultar(new(tieneAccesoTotal: false, centroIdsParaGestion: []))).Should().BeEmpty();
    }

    [Fact]
    public async Task Un_Trabajador_de_Empresa_contraparte_con_EmpresaId_nulo_no_crea_actividad_para_otra_persona()
    {
        var e = new Escenario();
        e.Asignaciones.ListaAsignaciones.Clear();
        // Deuda terminológica existente: DeSubcontrata representa a la persona de una Empresa contraparte.
        var otraPersona = Trabajador.DeSubcontrata(Guid.NewGuid(), "Otra", "Persona", "87654321X");
        e.Trabajadores.ListaTrabajadores.Add(otraPersona);
        e.Asignaciones.ListaAsignaciones.Add(new(otraPersona.Id, e.Centro.Id, new DateOnly(2026, 10, 1)));
        (await e.Consultar()).Should().BeEmpty();
    }

    [Fact]
    public async Task Un_Centro_que_solo_consulta_TALVEG_no_genera_trabajo_manual()
    {
        var e = new Escenario();
        e.Centros.ListaCanalesGestionDocumental.Clear();
        (await e.Consultar()).Should().BeEmpty();
    }

    [Fact]
    public async Task Una_entrega_hecha_no_reaparece_y_deshacer_la_devuelve_a_pendiente()
    {
        var e = new Escenario();
        var entrega = new PropagacionDocumento(e.Documento.Id, e.Canal.Id);
        var persona = Guid.NewGuid();
        entrega.MarcarHecha(persona, DateTime.UtcNow);
        e.Propagaciones.Filas.Add(entrega);
        (await e.Consultar()).Should().BeEmpty();
        entrega.Deshacer(persona);
        (await e.Consultar()).Should().ContainSingle();
    }

    [Fact]
    public async Task La_fecha_es_la_exigencia_del_Centro_y_un_envio_de_la_extension_no_sigue_pendiente()
    {
        var e = new Escenario();
        e.Tipos.ListaTiposDocumentoCentros.Add(new(e.Tipo.Id, e.Centro.Id, periodicidadEspecialMeses: 1, bloqueaAcceso: true));
        var fila = (await e.Consultar()).Should().ContainSingle().Subject;
        fila.Vencimiento.Should().Be(new DateOnly(2026, 11, 1));
        fila.BloqueaAcceso.Should().BeTrue();
        var acreditacion = new AcreditacionDocumentoPlataforma(e.Documento.Id, e.Canal.Id);
        acreditacion.MarcarSubida();
        e.Documentos.ListaAcreditacionesDocumentoPlataforma.Add(acreditacion);
        (await e.Consultar()).Should().BeEmpty();
    }
}
