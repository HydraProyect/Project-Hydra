using CaeManager.Application.Documentos.Queries.ObtenerDocumentos;
using CaeManager.Application.Configuracion;
using CaeManager.Application.Documentos.Queries.ObtenerEntidadesAsociadasDeDocumentos;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Integraciones;
using CaeManager.Application.Tests.Plantillas;
using CaeManager.Application.Tests.Proyectos;
using CaeManager.Application.Tests.TiposDocumento;
using CaeManager.Application.Vehiculos;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Configuracion;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Common;
using CaeManager.Domain.Integraciones;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Proyectos;
using CaeManager.Domain.Trabajadores;
using CaeManager.Domain.Vehiculos;
using FluentAssertions;

namespace CaeManager.Application.Tests.Documentos;

public class ObtenerEntidadesAsociadasDeDocumentosTests
{
    private sealed class ConfiguracionFalsa : IConfiguracionQueryContext
    {
        public IQueryable<ParametroSistema> ParametrosSistema => new TestAsyncQueryable<ParametroSistema>(new[] { new ParametroSistema(30, 7) }.AsQueryable());
        public IQueryable<PreferenciaDashboardUsuario> PreferenciasDashboardUsuario => Array.Empty<PreferenciaDashboardUsuario>().AsQueryable();
        public IQueryable<FiltroGuardado> FiltrosGuardados => Array.Empty<FiltroGuardado>().AsQueryable();
        public IQueryable<OrdenCajasFicha> OrdenesCajasFicha => Array.Empty<OrdenCajasFicha>().AsQueryable();
        public IQueryable<EstadoAutomatizacion> EstadosAutomatizacion => Array.Empty<EstadoAutomatizacion>().AsQueryable();
    }

    private sealed class VehiculosFalsos : IVehiculosQueryContext
    {
        public List<Vehiculo> Lista { get; } = [];
        public IQueryable<Vehiculo> Vehiculos => new TestAsyncQueryable<Vehiculo>(Lista.AsQueryable());
    }

    private sealed class Escenario
    {
        public DocumentosQueryContextFalso Documentos { get; } = new();
        public EmpresasQueryContextFalso Empresas { get; } = new();
        public TrabajadoresQueryContextFalso Trabajadores { get; } = new();
        public ProyectosQueryContextFalso Proyectos { get; } = new();
        public TiposDocumentoQueryContextFalso Tipos { get; } = new();
        public VehiculosFalsos Vehiculos { get; } = new();
        public CentrosQueryContextFalso Centros { get; } = new();
        public ProveedoresPlataformaCaeQueryContextFalso Proveedores { get; } = new();
        public TipoDocumento Tipo { get; } = new("Certificado", null, false, 1, AmbitoAplicacion.Empresa);

        public Escenario() => Tipos.ListaTiposDocumento.Add(Tipo);

        public ObtenerDocumentosQueryHandler Handler(AlcanceDatosServiceFalso? alcance = null) => new(
            new ConfiguracionFalsa(), Documentos, Empresas, Proyectos, Tipos, Trabajadores, Vehiculos,
            alcance ?? new(), Centros, Proveedores);

        public Empresa ConEmpresa(string nombre)
        {
            var empresa = new Empresa(nombre);
            Empresas.ListaEmpresas.Add(empresa);
            Documentos.ListaDocumentos.Add(Documento.DeEmpresa(empresa.Id, Tipo.Id, new(2026, 1, 1), VigenciaDocumento.NoCaduca));
            return empresa;
        }
    }

    [Fact]
    public async Task Las_opciones_no_se_limitan_a_la_pagina_de_documentos_y_deduplican_solo_la_misma_entidad_y_ambito()
    {
        var e = new Escenario();
        var primera = e.ConEmpresa("Alfa");
        var homonima = e.ConEmpresa("Alfa");
        e.ConEmpresa("Beta");
        e.Documentos.ListaDocumentos.Add(Documento.DeEmpresa(primera.Id, e.Tipo.Id, new(2025, 1, 1), VigenciaDocumento.NoCaduca));
        var handler = e.Handler();

        var paginaUno = await handler.Handle(new ObtenerEntidadesAsociadasDeDocumentosQuery(null, TamanoPagina: 2), default);
        var paginaDos = await handler.Handle(new ObtenerEntidadesAsociadasDeDocumentosQuery(null, Pagina: 2, TamanoPagina: 2), default);

        paginaUno.TotalElementos.Should().Be(3);
        paginaUno.Elementos.Select(x => x.Id).Should().BeEquivalentTo([primera.Id, homonima.Id]);
        paginaDos.Elementos.Should().ContainSingle().Which.Nombre.Should().Be("Beta");
        paginaUno.Elementos.Select(x => x.Id).Intersect(paginaDos.Elementos.Select(x => x.Id)).Should().BeEmpty();
        var repetida = await handler.Handle(new ObtenerEntidadesAsociadasDeDocumentosQuery(null, TamanoPagina: 2), default);
        repetida.Elementos.Should().Equal(paginaUno.Elementos);
    }

    [Fact]
    public async Task Incluye_los_cinco_ambitos_del_universo_documental_y_busca_por_nombre_de_entidad()
    {
        var e = new Escenario();
        var empresa = e.ConEmpresa("Empresa de prueba");
        var clienteEmpresarial = new Empresa("Contraparte");
        e.Empresas.ListaEmpresas.Add(clienteEmpresarial);
        var trabajador = Trabajador.DeEmpresa(empresa.Id, "Paula", "Campos", "12345678Z");
        e.Trabajadores.ListaTrabajadores.Add(trabajador);
        var vehiculo = Vehiculo.DeEmpresa(empresa.Id, "Camión", "Modelo", "9012GHI");
        e.Vehiculos.Lista.Add(vehiculo);
        var proyecto = Proyecto.Crear(clienteEmpresarial.Id, Guid.NewGuid(), "Obra", new(2026, 1, 1), null, null);
        e.Proyectos.ListaProyectos.Add(proyecto);
        e.Documentos.ListaDocumentos.AddRange([
            Documento.DeCliente(clienteEmpresarial.Id, e.Tipo.Id, new(2026, 1, 1), VigenciaDocumento.NoCaduca),
            Documento.DeTrabajador(trabajador.Id, e.Tipo.Id, new(2026, 1, 1), VigenciaDocumento.NoCaduca),
            Documento.DeVehiculo(vehiculo.Id, e.Tipo.Id, new(2026, 1, 1), VigenciaDocumento.NoCaduca),
            Documento.DeProyecto(proyecto.Id, e.Tipo.Id, new(2026, 1, 1), VigenciaDocumento.NoCaduca)]);
        var handler = e.Handler();

        var todos = await handler.Handle(new ObtenerEntidadesAsociadasDeDocumentosQuery(null), default);
        todos.Elementos.Select(x => x.Ambito).Should().BeEquivalentTo([
            AmbitoAplicacion.Empresa, AmbitoAplicacion.Cliente, AmbitoAplicacion.Trabajador,
            AmbitoAplicacion.Vehiculo, AmbitoAplicacion.Proyecto]);
        var filtrada = await handler.Handle(new ObtenerEntidadesAsociadasDeDocumentosQuery("Paula", AmbitoAplicacion.Trabajador), default);
        filtrada.Elementos.Should().ContainSingle().Which.Id.Should().Be(trabajador.Id);
        var otroAmbito = await handler.Handle(new ObtenerEntidadesAsociadasDeDocumentosQuery("Paula", AmbitoAplicacion.Empresa), default);
        otroAmbito.Elementos.Should().BeEmpty();
        var tipoNoEsEntidad = await handler.Handle(new ObtenerEntidadesAsociadasDeDocumentosQuery("Certificado"), default);
        tipoNoEsEntidad.Elementos.Should().BeEmpty();

        var sinCartera = new AlcanceDatosServiceFalso(tieneAccesoTotal: false,
            clienteIdsVisibles: [], empresaIdsVisibles: [], trabajadorIdsVisibles: [], vehiculoIdsVisibles: []);
        var restringida = await e.Handler(sinCartera).Handle(new ObtenerEntidadesAsociadasDeDocumentosQuery(null), default);
        restringida.Elementos.Should().BeEmpty("las cinco ramas conservan las restricciones del listado");

        var soloContraparte = new AlcanceDatosServiceFalso(tieneAccesoTotal: false,
            clienteIdsVisibles: [clienteEmpresarial.Id], empresaIdsVisibles: [], trabajadorIdsVisibles: [], vehiculoIdsVisibles: []);
        var contraparteYProyecto = await e.Handler(soloContraparte).Handle(new ObtenerEntidadesAsociadasDeDocumentosQuery(null), default);
        contraparteYProyecto.Elementos.Select(x => x.Id).Should().BeEquivalentTo([clienteEmpresarial.Id, proyecto.Id]);
    }

    [Fact]
    public async Task El_selector_no_ofrece_entidades_fuera_de_cartera_ni_entidades_sin_documentos()
    {
        var e = new Escenario();
        var visible = e.ConEmpresa("Visible");
        e.ConEmpresa("Fuera de cartera");
        e.Empresas.ListaEmpresas.Add(new Empresa("Sin documentos"));
        e.ConEmpresa("Documento eliminado");
        e.Documentos.ListaDocumentos.Last().MarcarComoEliminado(Guid.NewGuid());
        var alcance = new AlcanceDatosServiceFalso(tieneAccesoTotal: false, empresaIdsVisibles: [visible.Id]);

        var resultado = await e.Handler(alcance).Handle(new ObtenerEntidadesAsociadasDeDocumentosQuery(null), default);

        resultado.TotalElementos.Should().Be(1);
        resultado.Elementos.Should().ContainSingle().Which.Id.Should().Be(visible.Id);

        var sinRestriccion = await e.Handler().Handle(new ObtenerEntidadesAsociadasDeDocumentosQuery(null), default);
        sinRestriccion.Elementos.Select(x => x.Nombre).Should().NotContain("Documento eliminado").And.NotContain("Sin documentos");
    }

    [Fact]
    public async Task Acota_el_tamano_del_selector_y_normaliza_la_pagina()
    {
        var e = new Escenario();
        e.ConEmpresa("Visible");
        var resultado = await e.Handler().Handle(new ObtenerEntidadesAsociadasDeDocumentosQuery(null, Pagina: 0, TamanoPagina: int.MaxValue), default);

        resultado.Pagina.Should().Be(1);
        resultado.TamanoPagina.Should().Be(50);
        resultado.Elementos.Should().ContainSingle();
    }

    [Fact]
    public async Task La_agrupacion_conserva_los_bloques_y_el_orden_elegido_dentro_de_cada_ambito()
    {
        var e = new Escenario();
        var empresa = e.ConEmpresa("Empresa");
        var tipoZ = new TipoDocumento("Zulu", null, false, 2, AmbitoAplicacion.Empresa);
        e.Tipos.ListaTiposDocumento.Add(tipoZ);
        var trabajador = Trabajador.DeEmpresa(empresa.Id, "Paula", "Campos", "12345678Z");
        e.Trabajadores.ListaTrabajadores.Add(trabajador);
        e.Documentos.ListaDocumentos.AddRange([
            Documento.DeEmpresa(empresa.Id, tipoZ.Id, new(2026, 1, 1), VigenciaDocumento.NoCaduca),
            Documento.DeTrabajador(trabajador.Id, e.Tipo.Id, new(2026, 1, 1), VigenciaDocumento.NoCaduca),
            Documento.DeTrabajador(trabajador.Id, tipoZ.Id, new(2026, 1, 1), VigenciaDocumento.NoCaduca)]);
        var handler = e.Handler();
        var query = new ObtenerDocumentosQuery(null, null, null,
            OrdenarPor: nameof(DocumentoListaDto.TipoDocumentoNombre), Descendente: true, AgruparPorAmbito: true);

        var agrupada = await handler.Handle(query, default);

        agrupada.Elementos.Select(x => x.Ambito).Should().BeInAscendingOrder();
        foreach (var grupo in agrupada.Elementos.GroupBy(x => x.Ambito))
            grupo.Select(x => x.TipoDocumentoNombre).Should().Equal("Zulu", "Certificado");
        agrupada.ResumenPorGrupo.Should().NotBeNull();
        agrupada.ResumenPorGrupo![AmbitoAplicacion.Empresa.ToString()].Total.Should().Be(2);
        agrupada.ResumenPorGrupo[AmbitoAplicacion.Trabajador.ToString()].Total.Should().Be(2);

        var sinAgrupar = await handler.Handle(query with { AgruparPorAmbito = false }, default);
        sinAgrupar.ResumenPorGrupo.Should().BeNull();
        sinAgrupar.Elementos.Select(x => x.TipoDocumentoNombre).Should().Equal("Zulu", "Zulu", "Certificado", "Certificado");
    }

    [Fact]
    public async Task El_resumen_agrupado_cuenta_todas_las_paginas_y_solo_el_universo_filtrado_y_visible()
    {
        var e = new Escenario();
        var visible = e.ConEmpresa("Visible");
        e.ConEmpresa("Fuera de cartera");
        e.Documentos.ListaDocumentos.Add(Documento.DeEmpresa(visible.Id, e.Tipo.Id, new(2025, 1, 1), VigenciaDocumento.NoCaduca));
        var alcance = new AlcanceDatosServiceFalso(tieneAccesoTotal: false, empresaIdsVisibles: [visible.Id]);
        var handler = e.Handler(alcance);
        var query = new ObtenerDocumentosQuery(null, null, "Visible", Estado: EstadoDocumento.SinCaducidad,
            TamanoPagina: 1, AgruparPorAmbito: true);

        var primera = await handler.Handle(query, default);
        var segunda = await handler.Handle(query with { Pagina = 2 }, default);

        primera.Elementos.Should().ContainSingle();
        segunda.Elementos.Should().ContainSingle();
        primera.TotalElementos.Should().Be(2);
        var resumen = primera.ResumenPorGrupo.Should().ContainSingle().Which.Value;
        resumen.Total.Should().Be(2, "el resumen incluye la segunda página");
        resumen.PorEstado.Should().ContainSingle().Which.Should().Be(new KeyValuePair<string, int>(nameof(EstadoDocumento.SinCaducidad), 2));
        segunda.ResumenPorGrupo![AmbitoAplicacion.Empresa.ToString()].Should().BeEquivalentTo(resumen);
        var otroEstado = await handler.Handle(query with { Estado = EstadoDocumento.Vencido }, default);
        otroEstado.ResumenPorGrupo.Should().BeEmpty("el resumen se calcula después del filtro de estado");
    }

    [Fact]
    public async Task Los_destinos_usan_visibilidad_de_lectura_y_no_exponen_centros_fuera_de_cartera()
    {
        var e = new Escenario();
        var empresa = e.ConEmpresa("Empresa");
        var documento = e.Documentos.ListaDocumentos.Single();
        var contraparte = new Empresa("Cliente empresarial contraparte");
        e.Empresas.ListaEmpresas.Add(contraparte);
        var visible = new Centro(contraparte.Id, empresa.Id, "Centro visible");
        var ajeno = new Centro(contraparte.Id, empresa.Id, "Centro ajeno");
        e.Centros.ListaCentros.AddRange([visible, ajeno]);
        var proveedor = new ProveedorPlataformaCae("nalanda", "Nalanda");
        e.Proveedores.ListaProveedores.Add(proveedor);
        foreach (var centro in new[] { visible, ajeno })
        {
            var canal = CanalGestionDocumental.DePlataforma(centro.Id, "Documentación", proveedor.Id, null, null, null);
            e.Centros.ListaCanalesGestionDocumental.Add(canal);
            e.Documentos.ListaAcreditacionesDocumentoPlataforma.Add(new(documento.Id, canal.Id));
        }
        // Un usuario de solo lectura, como Consulta, puede leer el Centro sin poder gestionarlo.
        var alcance = new AlcanceDatosServiceFalso(tieneAccesoTotal: false, empresaIdsVisibles: [empresa.Id],
            centroIdsVisibles: [visible.Id], centroIdsParaGestion: []);
        var handler = e.Handler(alcance);
        var query = new ObtenerDocumentosQuery(null, null, null, ConDestinosEnCentros: true);

        var resultado = await handler.Handle(query, default);

        var destino = resultado.Elementos.Should().ContainSingle().Which.DestinosEnCentros.Should().ContainSingle().Which;
        destino.CentroId.Should().Be(visible.Id);
        destino.CentroNombre.Should().Be("Centro visible");
        destino.ClienteNombre.Should().Be(contraparte.RazonSocial);
        destino.PlataformaNombre.Should().Be("Nalanda");
        destino.Estado.Should().Be(EstadoDocumento.SinCaducidad,
            "una acreditación PendienteDeSubir no altera el estado documental contextual");
        var sinSolicitar = await handler.Handle(query with { ConDestinosEnCentros = false }, default);
        sinSolicitar.Elementos.Single().DestinosEnCentros.Should().BeNull();
    }

    [Fact]
    public async Task El_estado_del_destino_reutiliza_tolerancia_del_centro_y_herencia_del_cliente_empresarial()
    {
        var e = new Escenario();
        var empresa = e.ConEmpresa("Empresa");
        var documento = e.Documentos.ListaDocumentos.Single();
        var hoy = DiaDeNegocio.Hoy();
        documento.CorregirVigencia(hoy.AddDays(-30), VigenciaDocumento.VenceEl(hoy.AddDays(-1)));
        var contraparte = new Empresa("Contraparte");
        e.Empresas.ListaEmpresas.Add(contraparte);
        e.Tipos.ListaToleranciasDocumentoClienteEmpresarial.Add(new(contraparte.Id, e.Tipo.Id, 5));
        var hereda = new Centro(contraparte.Id, empresa.Id, "Hereda tolerancia");
        var sinTolerancia = new Centro(contraparte.Id, empresa.Id, "Tolerancia cero");
        e.Centros.ListaCentros.AddRange([hereda, sinTolerancia]);
        e.Tipos.ListaTiposDocumentoCentros.Add(new(e.Tipo.Id, sinTolerancia.Id, toleranciaDias: 0));
        foreach (var centro in new[] { hereda, sinTolerancia })
        {
            var canal = CanalGestionDocumental.PorEmail(centro.Id, "Documentación", "cae@example.com", null);
            e.Centros.ListaCanalesGestionDocumental.Add(canal);
            e.Documentos.ListaAcreditacionesDocumentoPlataforma.Add(new(documento.Id, canal.Id));
        }

        var resultado = await e.Handler().Handle(new ObtenerDocumentosQuery(null, null, null, ConDestinosEnCentros: true), default);

        var fila = resultado.Elementos.Single();
        fila.Estado.Should().Be(EstadoDocumento.Vencido, "el contexto de Centro no cambia el estado general");
        var destinos = fila.DestinosEnCentros!;
        destinos.Single(x => x.CentroId == hereda.Id).Estado.Should().Be(EstadoDocumento.EnTolerancia);
        destinos.Single(x => x.CentroId == hereda.Id).EnToleranciaHasta.Should().Be(hoy.AddDays(4));
        destinos.Single(x => x.CentroId == sinTolerancia.Id).Estado.Should().Be(EstadoDocumento.Vencido);
        destinos.Single(x => x.CentroId == sinTolerancia.Id).EnToleranciaHasta.Should().BeNull();
        destinos.Should().OnlyContain(x => x.PlataformaNombre == "Sin portal");
    }

    [Fact]
    public async Task Las_acreditaciones_conservan_nombre_estado_y_canales_sin_proveedor_en_la_pagina()
    {
        var e = new Escenario();
        e.ConEmpresa("Empresa");
        var documento = e.Documentos.ListaDocumentos.Single();
        var proveedor = new ProveedorPlataformaCae("nalanda", "Nalanda");
        e.Proveedores.ListaProveedores.Add(proveedor);
        var centroId = Guid.NewGuid();
        var canales = new[]
        {
            CanalGestionDocumental.DePlataforma(centroId, "Portal", proveedor.Id, null, null, null),
            CanalGestionDocumental.PorEmail(centroId, "Correo", "cae@example.com", null)
        };
        e.Centros.ListaCanalesGestionDocumental.AddRange(canales);
        foreach (var canal in canales)
            e.Documentos.ListaAcreditacionesDocumentoPlataforma.Add(new(documento.Id, canal.Id));

        var resultado = await e.Handler().Handle(new ObtenerDocumentosQuery(null, null, null), default);

        var fila = resultado.Elementos.Should().ContainSingle().Which;
        fila.Acreditaciones.Select(x => x.NombrePlataforma).Should().BeEquivalentTo("Nalanda", "Plataforma");
        fila.Acreditaciones.Select(x => x.Id).Should().BeEquivalentTo(
            e.Documentos.ListaAcreditacionesDocumentoPlataforma.Select(x => x.Id));
        fila.Acreditaciones.Should().OnlyContain(x => x.Estado == EstadoAcreditacion.PendienteDeSubir);
    }
}
