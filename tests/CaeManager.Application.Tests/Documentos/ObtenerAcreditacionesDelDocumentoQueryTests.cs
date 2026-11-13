using CaeManager.Application.Configuracion;
using CaeManager.Application.Documentos.Queries.ObtenerAcreditacionesDelDocumento;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Integraciones;
using CaeManager.Application.Tests.Plantillas;
using CaeManager.Application.Tests.Proyectos;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Configuracion;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Integraciones;
using FluentAssertions;

namespace CaeManager.Application.Tests.Documentos;

/// <summary>
/// D-23 (lienzo «Documentos · listado sin pestañas»): la sección «Plataformas CAE» de la vista rápida de un Documento. Fija qué
/// recibe el DTO desde el servidor según el alcance: el Documento tiene que ser legible, solo cuentan los canales de Plataforma CAE
/// en Centros visibles, y el motivo de rechazo y la dirección del portal solo llegan en Centros que el usuario gestiona. El rol
/// Cliente (usuario de portal) recibe de <c>ObtenerCentroIdsParaGestionAsync</c> la lista vacía, que es lo que aquí simula
/// <c>centroIdsParaGestion: []</c>; que esa lista salga del rol efectivo lo fija <c>AlcanceDatosService</c>, no este handler.
/// </summary>
public class ObtenerAcreditacionesDelDocumentoQueryTests
{
    private sealed class ConfiguracionFalsa : IConfiguracionQueryContext
    {
        public IQueryable<ParametroSistema> ParametrosSistema => new TestAsyncQueryable<ParametroSistema>(new[] { new ParametroSistema(30, 7) }.AsQueryable());
        public IQueryable<PreferenciaDashboardUsuario> PreferenciasDashboardUsuario => Array.Empty<PreferenciaDashboardUsuario>().AsQueryable();
        public IQueryable<FiltroGuardado> FiltrosGuardados => Array.Empty<FiltroGuardado>().AsQueryable();
        public IQueryable<OrdenCajasFicha> OrdenesCajasFicha => Array.Empty<OrdenCajasFicha>().AsQueryable();
        public IQueryable<EstadoAutomatizacion> EstadosAutomatizacion => Array.Empty<EstadoAutomatizacion>().AsQueryable();
    }

    private sealed class Escenario
    {
        public DocumentosQueryContextFalso Documentos { get; } = new();
        public EmpresasQueryContextFalso Empresas { get; } = new();
        public CentrosQueryContextFalso Centros { get; } = new();
        public ProveedoresPlataformaCaeQueryContextFalso Proveedores { get; } = new();
        public Empresa Contratista { get; }
        public Documento Documento { get; }

        public Escenario()
        {
            Contratista = ConEmpresa("Contratista");
            Documento = Documento.DeEmpresa(Contratista.Id, Guid.NewGuid(), new(2026, 1, 1), VigenciaDocumento.NoCaduca);
            Documentos.ListaDocumentos.Add(Documento);
        }

        public ObtenerAcreditacionesDelDocumentoQueryHandler Handler(AlcanceDatosServiceFalso? alcance = null) => new(
            new ConfiguracionFalsa(), Documentos, Empresas, new ProyectosQueryContextFalso(), Centros, Proveedores, alcance ?? new());

        public Task<AcreditacionesDelDocumentoDto?> LeerAsync(AlcanceDatosServiceFalso? alcance = null) =>
            Handler(alcance).Handle(new ObtenerAcreditacionesDelDocumentoQuery(Documento.Id), default);

        public Empresa ConEmpresa(string nombre)
        {
            var empresa = new Empresa(nombre);
            Empresas.ListaEmpresas.Add(empresa);
            return empresa;
        }

        public ProveedorPlataformaCae ConPlataforma(string nombre)
        {
            var proveedor = new ProveedorPlataformaCae(nombre.ToLowerInvariant(), nombre);
            Proveedores.ListaProveedores.Add(proveedor);
            return proveedor;
        }

        public Centro ConCentro(Empresa clienteEmpresarial, string nombre)
        {
            var centro = new Centro(clienteEmpresarial.Id, Contratista.Id, nombre);
            Centros.ListaCentros.Add(centro);
            return centro;
        }

        public AcreditacionDocumentoPlataforma Acreditar(Centro centro, ProveedorPlataformaCae? proveedor, string? url = null)
        {
            var canal = proveedor is null
                ? CanalGestionDocumental.PorEmail(centro.Id, "Documentación", "cae@example.com", null)
                : CanalGestionDocumental.DePlataforma(centro.Id, "Documentación", proveedor.Id, url, null, null);
            Centros.ListaCanalesGestionDocumental.Add(canal);
            var acreditacion = new AcreditacionDocumentoPlataforma(Documento.Id, canal.Id);
            Documentos.ListaAcreditacionesDocumentoPlataforma.Add(acreditacion);
            return acreditacion;
        }

        public void Rechazar(AcreditacionDocumentoPlataforma acreditacion, string motivo)
        {
            acreditacion.Rechazar(CausaRechazoAcreditacion.Ilegible, motivo, DateTime.UtcNow);
            Documentos.ListaRechazosAcreditacionDocumentoPlataforma.Add(acreditacion.HistorialRechazos[^1]);
        }
    }

    [Fact]
    public async Task Quien_gestiona_recibe_cada_acreditacion_en_Plataforma_CAE_con_su_contexto_motivo_y_direccion()
    {
        var e = new Escenario();
        var alcor = e.ConEmpresa("Refrigeración Alcor");
        var lasarte = e.ConEmpresa("Aceros Lasarte");
        var norte = e.ConCentro(alcor, "Centro Norte");
        var berriz = e.ConCentro(lasarte, "Nave Berriz");
        var nalanda = e.ConPlataforma("Nalanda");
        var ctaima = e.ConPlataforma("CTAIMA");
        var enNalanda = e.Acreditar(norte, nalanda, "https://nalanda.example/acceso");
        e.Rechazar(enNalanda, "Documento ilegible");
        var enCtaima = e.Acreditar(berriz, ctaima);
        e.Acreditar(norte, proveedor: null); // canal por correo: no es una Plataforma CAE

        var resultado = await e.LeerAsync();

        resultado.Should().NotBeNull();
        resultado!.Acreditaciones.Select(a => a.Id).Should().Equal([enCtaima.Id, enNalanda.Id],
            "por orden de Plataforma CAE y sin el canal por correo");
        var rechazada = resultado.Acreditaciones.Single(a => a.Id == enNalanda.Id);
        rechazada.Should().BeEquivalentTo(new
        {
            ProveedorId = nalanda.Id,
            PlataformaNombre = "Nalanda",
            PlataformaCodigo = "nalanda",
            ClienteEmpresarialNombre = "Refrigeración Alcor",
            CentroNombre = "Centro Norte",
            Estado = EstadoAcreditacion.Rechazada,
            UltimoMotivoRechazo = "Documento ilegible",
            UrlAccesoCanal = "https://nalanda.example/acceso"
        });
        resultado.Acreditaciones.Single(a => a.Id == enCtaima.Id).Should().BeEquivalentTo(new
        {
            ClienteEmpresarialNombre = "Aceros Lasarte",
            CentroNombre = "Nave Berriz",
            Estado = EstadoAcreditacion.PendienteDeSubir,
            UltimoMotivoRechazo = (string?)null,
            UrlAccesoCanal = (string?)null
        });
    }

    /// <summary>
    /// Opción B (Chris, 2026-11-12): el Usuario de Cliente ve el estado de las acreditaciones de los Centros de su Cliente
    /// empresarial, nunca las de otro Cliente empresarial, y ni el motivo ni la dirección del portal viajan en el DTO.
    /// </summary>
    [Fact]
    public async Task El_rol_Cliente_ve_el_estado_de_sus_Centros_sin_motivo_ni_direccion_y_nada_de_otros_Clientes_empresariales()
    {
        var e = new Escenario();
        var suyo = e.ConEmpresa("Su Cliente empresarial");
        var otro = e.ConEmpresa("Otro Cliente empresarial");
        var centroSuyo = e.ConCentro(suyo, "Centro suyo");
        var centroAjeno = e.ConCentro(otro, "Centro ajeno");
        var nalanda = e.ConPlataforma("Nalanda");
        var enSuyo = e.Acreditar(centroSuyo, nalanda, "https://nalanda.example/acceso");
        e.Rechazar(enSuyo, "Motivo interno");
        e.Acreditar(centroAjeno, nalanda, "https://nalanda.example/otro");

        var usuarioDeCliente = new AlcanceDatosServiceFalso(tieneAccesoTotal: false, empresaIdsVisibles: [e.Contratista.Id],
            centroIdsVisibles: [centroSuyo.Id], centroIdsParaGestion: []);
        var resultado = await e.LeerAsync(usuarioDeCliente);

        resultado!.Acreditaciones.Should().ContainSingle().Which.Should().BeEquivalentTo(new
        {
            Id = enSuyo.Id,
            Estado = EstadoAcreditacion.Rechazada,
            CentroNombre = "Centro suyo",
            UltimoMotivoRechazo = (string?)null,
            UrlAccesoCanal = (string?)null
        });
    }

    [Fact]
    public async Task El_motivo_y_la_direccion_solo_llegan_del_Centro_que_se_gestiona()
    {
        var e = new Escenario();
        var cliente = e.ConEmpresa("Cliente empresarial");
        var centroA = e.ConCentro(cliente, "Centro A");
        var centroB = e.ConCentro(cliente, "Centro B");
        var nalanda = e.ConPlataforma("Nalanda");
        var enA = e.Acreditar(centroA, nalanda, "https://a.example");
        e.Rechazar(enA, "Motivo en A");
        var enB = e.Acreditar(centroB, nalanda, "https://b.example");
        e.Rechazar(enB, "Motivo en B");

        var gestorDeA = new AlcanceDatosServiceFalso(tieneAccesoTotal: false, empresaIdsVisibles: [e.Contratista.Id],
            centroIdsVisibles: [centroA.Id, centroB.Id], centroIdsParaGestion: [centroA.Id]);
        var resultado = await e.LeerAsync(gestorDeA);

        resultado!.Acreditaciones.Select(a => (a.CentroNombre, a.UltimoMotivoRechazo, a.UrlAccesoCanal)).Should().Equal(
            [("Centro A", "Motivo en A", "https://a.example"), ("Centro B", (string?)null, (string?)null)]);
    }

    [Fact]
    public async Task Un_Gestor_CAE_fuera_de_cartera_no_recibe_nada_del_Documento()
    {
        var e = new Escenario();
        var centro = e.ConCentro(e.ConEmpresa("Cliente empresarial"), "Centro");
        var acreditacion = e.Acreditar(centro, e.ConPlataforma("Nalanda"), "https://nalanda.example");
        e.Rechazar(acreditacion, "Motivo");

        var fueraDeCartera = new AlcanceDatosServiceFalso(tieneAccesoTotal: false, empresaIdsVisibles: [],
            centroIdsVisibles: [centro.Id], centroIdsParaGestion: [centro.Id]);

        (await e.LeerAsync(fueraDeCartera)).Should().BeNull("un Documento que no lee responde igual que uno que no existe");
        (await e.LeerAsync()).Should().NotBeNull("control positivo: con alcance sí lo recibe");
    }

    [Fact]
    public async Task Las_acreditaciones_de_Centros_que_no_ve_no_aparecen_aunque_el_Documento_si_sea_legible()
    {
        var e = new Escenario();
        var cliente = e.ConEmpresa("Cliente empresarial");
        var visible = e.ConCentro(cliente, "Visible");
        var oculto = e.ConCentro(cliente, "Oculto");
        var nalanda = e.ConPlataforma("Nalanda");
        e.Acreditar(oculto, nalanda);

        var gestor = new AlcanceDatosServiceFalso(tieneAccesoTotal: false, empresaIdsVisibles: [e.Contratista.Id],
            centroIdsVisibles: [visible.Id]);
        var resultado = await e.LeerAsync(gestor);

        resultado.Should().NotBeNull();
        resultado!.Acreditaciones.Should().BeEmpty();
    }
}
