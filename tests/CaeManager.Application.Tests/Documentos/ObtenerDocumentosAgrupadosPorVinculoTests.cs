using CaeManager.Application.Common;
using CaeManager.Application.Configuracion;
using CaeManager.Application.Documentos.Queries.ObtenerDocumentos;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Integraciones;
using CaeManager.Application.Tests.Plantillas;
using CaeManager.Application.Tests.Proyectos;
using CaeManager.Application.Tests.TiposDocumento;
using CaeManager.Application.Vehiculos;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Common;
using CaeManager.Domain.Configuracion;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Integraciones;
using CaeManager.Domain.Vehiculos;
using FluentAssertions;

namespace CaeManager.Application.Tests.Documentos;

/// <summary>
/// P-1 (2026-11-12): el listado de Documentos agrupado por Plataforma CAE y por Cliente empresarial, y el filtro
/// «Estado en la plataforma». Observa el handler con contextos en memoria: el alcance de cartera (Centros visibles), las
/// filas por vínculo, el grupo «sin», la dirección del portal y que los recuentos cubren todo el universo filtrado.
/// No observa la traducción a SQL de PostgreSQL (join y GroupBy sobre la unión), que solo ve la suite de integración.
/// </summary>
public class ObtenerDocumentosAgrupadosPorVinculoTests
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
        public IQueryable<Vehiculo> Vehiculos => new TestAsyncQueryable<Vehiculo>(Array.Empty<Vehiculo>().AsQueryable());
    }

    private sealed class Escenario
    {
        public DocumentosQueryContextFalso Documentos { get; } = new();
        public EmpresasQueryContextFalso Empresas { get; } = new();
        public TrabajadoresQueryContextFalso Trabajadores { get; } = new();
        public ProyectosQueryContextFalso Proyectos { get; } = new();
        public TiposDocumentoQueryContextFalso Tipos { get; } = new();
        public CentrosQueryContextFalso Centros { get; } = new();
        public ProveedoresPlataformaCaeQueryContextFalso Proveedores { get; } = new();
        public TipoDocumento Tipo { get; } = new("Certificado", null, false, 1, AmbitoAplicacion.Empresa);

        /// <summary>La contratista dueña de los Documentos; el Gestor CAE la tiene en su cartera.</summary>
        public Empresa Contratista { get; }

        public Escenario()
        {
            Tipos.ListaTiposDocumento.Add(Tipo);
            Contratista = ConEmpresa("Contratista");
        }

        public ObtenerDocumentosQueryHandler Handler(AlcanceDatosServiceFalso? alcance = null) => new(
            new ConfiguracionFalsa(), Documentos, Empresas, Proyectos, Tipos, Trabajadores, new VehiculosFalsos(),
            alcance ?? new(), Centros, Proveedores);

        public Empresa ConEmpresa(string nombre)
        {
            var empresa = new Empresa(nombre);
            Empresas.ListaEmpresas.Add(empresa);
            return empresa;
        }

        public Documento ConDocumento()
        {
            var documento = Documento.DeEmpresa(Contratista.Id, Tipo.Id, new(2026, 1, 1), VigenciaDocumento.NoCaduca);
            Documentos.ListaDocumentos.Add(documento);
            return documento;
        }

        public ProveedorPlataformaCae ConPlataforma(string nombre)
        {
            var proveedor = new ProveedorPlataformaCae(nombre.ToLowerInvariant(), nombre);
            Proveedores.ListaProveedores.Add(proveedor);
            return proveedor;
        }

        /// <summary>Un Centro del <paramref name="clienteEmpresarial"/> donde trabaja la contratista.</summary>
        public Centro ConCentro(Empresa clienteEmpresarial, string nombre)
        {
            var centro = new Centro(clienteEmpresarial.Id, Contratista.Id, nombre);
            Centros.ListaCentros.Add(centro);
            return centro;
        }

        public AcreditacionDocumentoPlataforma Acreditar(Documento documento, Centro centro, ProveedorPlataformaCae? proveedor,
            string? url = null)
        {
            var canal = proveedor is null
                ? CanalGestionDocumental.PorEmail(centro.Id, "Documentación", "cae@example.com", null)
                : CanalGestionDocumental.DePlataforma(centro.Id, "Documentación", proveedor.Id, url, null, null);
            Centros.ListaCanalesGestionDocumental.Add(canal);
            var acreditacion = new AcreditacionDocumentoPlataforma(documento.Id, canal.Id);
            Documentos.ListaAcreditacionesDocumentoPlataforma.Add(acreditacion);
            return acreditacion;
        }

        public void Rechazar(AcreditacionDocumentoPlataforma acreditacion, string motivo, DateTime fechaUtc)
        {
            acreditacion.Rechazar(CausaRechazoAcreditacion.Ilegible, motivo, fechaUtc);
            Documentos.ListaRechazosAcreditacionDocumentoPlataforma.Add(acreditacion.HistorialRechazos[^1]);
        }
    }

    private static ObtenerDocumentosQuery Agrupado(AgrupacionDocumentos agrupacion,
        params EstadoEnPlataformaFiltro[] estados) =>
        new(null, null, null, TamanoPagina: 100, AgruparPor: agrupacion, EstadosEnPlataforma: estados.Length > 0 ? estados : null);

    private static ObtenerDocumentosQuery Filtrado(params EstadoEnPlataformaFiltro[] estados) =>
        new(null, null, null, TamanoPagina: 100, EstadosEnPlataforma: estados);

    private static ResumenDeGrupoDocumental Resumen(ResultadoPaginado<DocumentoListaDto> resultado, string clave) =>
        resultado.ResumenPorGrupo.Should().ContainKey(clave).WhoseValue.Should().BeOfType<ResumenDeGrupoDocumental>().Subject;

    // ------------------------------------------------------------- cartera del Gestor CAE

    /// <summary>
    /// Un Gestor CAE con la contratista en cartera pero solo con un Centro visible: lo acreditado en un Centro fuera de
    /// su cartera no le crea grupos, ni filas, ni recuentos, ni le sirve de oráculo a través del filtro de estado.
    /// </summary>
    [Fact]
    public async Task Un_gestor_fuera_de_cartera_no_ve_grupos_filas_ni_recuentos_de_Centros_que_no_ve()
    {
        var e = new Escenario();
        var clienteVisible = e.ConEmpresa("Cliente visible");
        var clienteAjeno = e.ConEmpresa("Cliente ajeno");
        var centroVisible = e.ConCentro(clienteVisible, "Centro visible");
        var centroAjeno = e.ConCentro(clienteAjeno, "Centro ajeno");
        var nalanda = e.ConPlataforma("Nalanda");
        var ctaima = e.ConPlataforma("Ctaima");

        var enLosDos = e.ConDocumento();
        e.Acreditar(enLosDos, centroVisible, nalanda);
        e.Rechazar(e.Acreditar(enLosDos, centroAjeno, ctaima, "https://ajeno.example.com"), "Motivo ajeno", DateTime.UtcNow);
        var soloEnElAjeno = e.ConDocumento();
        e.Acreditar(soloEnElAjeno, centroAjeno, nalanda, "https://ajeno.example.com").MarcarAceptada(VigenciaEnPlataforma.SinConfirmar);

        var gestor = new AlcanceDatosServiceFalso(tieneAccesoTotal: false, empresaIdsVisibles: [e.Contratista.Id],
            centroIdsVisibles: [centroVisible.Id]);
        var handler = e.Handler(gestor);

        var porPlataforma = await handler.Handle(Agrupado(AgrupacionDocumentos.PlataformaCae), default);

        porPlataforma.TotalElementos.Should().Be(2, "los dos Documentos son de una contratista de su cartera");
        porPlataforma.ResumenPorGrupo!.Keys.Should().BeEquivalentTo([nalanda.Id.ToString(), ObtenerDocumentosQuery.GrupoSin]);
        var nalandaVisible = Resumen(porPlataforma, nalanda.Id.ToString());
        nalandaVisible.Total.Should().Be(1, "la acreditación de Nalanda en el Centro ajeno no cuenta");
        nalandaVisible.EnPlataforma.Should().BeEquivalentTo(new Dictionary<EstadoEnPlataformaFiltro, int>
            { [EstadoEnPlataformaFiltro.PendienteDeSubir] = 1 });
        nalandaVisible.UrlPortal.Should().BeNull("la única dirección es la del canal del Centro ajeno");
        Resumen(porPlataforma, ObtenerDocumentosQuery.GrupoSin).Total.Should().Be(1);
        porPlataforma.Elementos.Should().HaveCount(2);
        porPlataforma.Elementos.Single(x => x.Id == enLosDos.Id).Grupo!.Acreditacion!.ClienteEmpresarialNombre.Should().Be("Cliente visible");
        porPlataforma.Elementos.Single(x => x.Id == soloEnElAjeno.Id).Grupo.Should().Be(new GrupoDeFilaDocumentalDto(ObtenerDocumentosQuery.GrupoSin));
        porPlataforma.Elementos.Select(x => x.Grupo!.Acreditacion?.UltimoMotivoRechazo).Should().NotContain("Motivo ajeno");

        var porCliente = await handler.Handle(Agrupado(AgrupacionDocumentos.ClienteEmpresarial), default);

        porCliente.ResumenPorGrupo!.Keys.Should().BeEquivalentTo([clienteVisible.Id.ToString(), ObtenerDocumentosQuery.GrupoSin]);
        porCliente.ResumenPorGrupo.Values.OfType<ResumenDeGrupoDocumental>().Select(x => x.Nombre).Should().NotContain("Cliente ajeno");
        Resumen(porCliente, clienteVisible.Id.ToString()).Total.Should().Be(1);
        porCliente.Elementos.Select(x => x.Grupo!.Clave).Should().BeEquivalentTo(
            [clienteVisible.Id.ToString(), ObtenerDocumentosQuery.GrupoSin]);

        var rechazados = await handler.Handle(Filtrado(EstadoEnPlataformaFiltro.Rechazado), default);
        rechazados.TotalElementos.Should().Be(0, "el único rechazo está en un Centro que no ve: el filtro no puede delatarlo");
        rechazados.Elementos.Should().BeEmpty();
        var sinVigencia = await handler.Handle(Agrupado(AgrupacionDocumentos.PlataformaCae, EstadoEnPlataformaFiltro.SinVigencia), default);
        sinVigencia.TotalElementos.Should().Be(0);
        sinVigencia.ResumenPorGrupo.Should().BeEmpty();
        var pendientes = await handler.Handle(Agrupado(AgrupacionDocumentos.ClienteEmpresarial, EstadoEnPlataformaFiltro.PendienteDeSubir), default);
        pendientes.Elementos.Should().ContainSingle().Which.Id.Should().Be(enLosDos.Id);
        pendientes.ResumenPorGrupo!.Keys.Should().BeEquivalentTo([clienteVisible.Id.ToString()]);
    }

    // ------------------------------------------------------------- filas por vínculo

    [Fact]
    public async Task Un_documento_en_varias_Plataformas_CAE_aparece_en_cada_grupo_y_cuenta_una_vez_en_el_total()
    {
        var e = new Escenario();
        var cliente = e.ConEmpresa("Cliente empresarial");
        var otroCliente = e.ConEmpresa("Otro cliente empresarial");
        var centro = e.ConCentro(cliente, "Centro");
        var otroCentro = e.ConCentro(otroCliente, "Otro centro");
        var nalanda = e.ConPlataforma("Nalanda");
        var ctaima = e.ConPlataforma("Ctaima");
        var documento = e.ConDocumento();
        e.Acreditar(documento, centro, nalanda);
        e.Acreditar(documento, otroCentro, nalanda);
        e.Acreditar(documento, centro, ctaima);

        var porPlataforma = await e.Handler().Handle(Agrupado(AgrupacionDocumentos.PlataformaCae), default);

        porPlataforma.TotalElementos.Should().Be(1, "el total sigue contando Documentos, no filas");
        porPlataforma.Elementos.Should().HaveCount(3).And.OnlyContain(x => x.Id == documento.Id);
        porPlataforma.Elementos.Select(x => x.Grupo!.Clave).Should().Equal(
            ctaima.Id.ToString(), nalanda.Id.ToString(), nalanda.Id.ToString());
        porPlataforma.Elementos.Skip(1).Select(x => x.Grupo!.Acreditacion!.ClienteEmpresarialNombre).Should().Equal(
            "Cliente empresarial", "Otro cliente empresarial");
        Resumen(porPlataforma, nalanda.Id.ToString()).Should().Match<ResumenDeGrupoDocumental>(r => r.Total == 2 && r.Nombre == "Nalanda");
        Resumen(porPlataforma, ctaima.Id.ToString()).Total.Should().Be(1);
        porPlataforma.ResumenPorGrupo.Should().NotContainKey(ObtenerDocumentosQuery.GrupoSin);

        var porCliente = await e.Handler().Handle(Agrupado(AgrupacionDocumentos.ClienteEmpresarial), default);

        porCliente.Elementos.Select(x => x.Grupo!.Clave).Should().Equal(cliente.Id.ToString(), otroCliente.Id.ToString());
        Resumen(porCliente, cliente.Id.ToString()).Total.Should().Be(1, "un Documento cuenta una vez por Cliente empresarial");
        Resumen(porCliente, cliente.Id.ToString()).PorEstado.Should().BeEquivalentTo(
            new Dictionary<string, int> { [nameof(EstadoDocumento.SinCaducidad)] = 1 });
        Resumen(porCliente, otroCliente.Id.ToString()).Nombre.Should().Be("Otro cliente empresarial");
    }

    [Fact]
    public async Task El_grupo_sin_recoge_lo_que_no_tiene_vinculo_y_por_Cliente_empresarial_cuenta_cualquier_canal()
    {
        var e = new Escenario();
        var cliente = e.ConEmpresa("Cliente empresarial");
        var centro = e.ConCentro(cliente, "Centro");
        var sinNada = e.ConDocumento();
        var porCorreo = e.ConDocumento();
        e.Acreditar(porCorreo, centro, proveedor: null);

        var porPlataforma = await e.Handler().Handle(Agrupado(AgrupacionDocumentos.PlataformaCae), default);

        porPlataforma.ResumenPorGrupo!.Keys.Should().BeEquivalentTo([ObtenerDocumentosQuery.GrupoSin],
            "un canal por correo no es una Plataforma CAE");
        Resumen(porPlataforma, ObtenerDocumentosQuery.GrupoSin).Should().Match<ResumenDeGrupoDocumental>(
            r => r.Total == 2 && r.UrlPortal == null && r.EnPlataforma.Count == 0);
        porPlataforma.Elementos.Should().HaveCount(2).And.OnlyContain(x => x.Grupo!.Clave == ObtenerDocumentosQuery.GrupoSin
            && x.Grupo.Acreditacion == null);

        var porCliente = await e.Handler().Handle(Agrupado(AgrupacionDocumentos.ClienteEmpresarial), default);

        porCliente.Elementos.Single(x => x.Id == porCorreo.Id).Grupo!.Clave.Should().Be(cliente.Id.ToString());
        porCliente.Elementos.Single(x => x.Id == sinNada.Id).Grupo!.Clave.Should().Be(ObtenerDocumentosQuery.GrupoSin);
        Resumen(porCliente, ObtenerDocumentosQuery.GrupoSin).Total.Should().Be(1);
        Resumen(porCliente, cliente.Id.ToString()).Total.Should().Be(1);

        var sinAgrupar = await e.Handler().Handle(new ObtenerDocumentosQuery(null, null, null), default);
        sinAgrupar.ResumenPorGrupo.Should().BeNull();
        sinAgrupar.Elementos.Should().OnlyContain(x => x.Grupo == null);
    }

    // ------------------------------------------------------------- Abrir portal

    [Fact]
    public async Task Abrir_portal_solo_lleva_direccion_si_es_unica_y_nunca_para_quien_no_gestiona()
    {
        var e = new Escenario();
        var cliente = e.ConEmpresa("Cliente empresarial");
        var centroA = e.ConCentro(cliente, "Centro A");
        var centroB = e.ConCentro(cliente, "Centro B");
        var nalanda = e.ConPlataforma("Nalanda");
        var ctaima = e.ConPlataforma("Ctaima");
        var uno = e.ConDocumento();
        var dos = e.ConDocumento();
        e.Acreditar(uno, centroA, nalanda, "https://nalanda.example.com");
        e.Acreditar(dos, centroB, nalanda, " https://NALANDA.example.com ");
        e.Acreditar(uno, centroA, ctaima, "https://ctaima.example.com/a");
        e.Acreditar(dos, centroB, ctaima, "https://ctaima.example.com/b");

        var resultado = await e.Handler().Handle(Agrupado(AgrupacionDocumentos.PlataformaCae), default);

        Resumen(resultado, nalanda.Id.ToString()).UrlPortal.Should().BeEquivalentTo("https://nalanda.example.com",
            "dos canales con la misma dirección, salvo espacios y mayúsculas, son una sola");
        Resumen(resultado, ctaima.Id.ToString()).UrlPortal.Should().BeNull("con dos direcciones distintas no hay un portal que abrir");

        var usuarioDeCliente = new AlcanceDatosServiceFalso(tieneAccesoTotal: false, empresaIdsVisibles: [e.Contratista.Id],
            centroIdsVisibles: [centroA.Id, centroB.Id], centroIdsParaGestion: []);
        var deLectura = await e.Handler(usuarioDeCliente).Handle(Agrupado(AgrupacionDocumentos.PlataformaCae), default);

        Resumen(deLectura, nalanda.Id.ToString()).Total.Should().Be(2, "ve los grupos y sus recuentos");
        deLectura.ResumenPorGrupo!.Values.OfType<ResumenDeGrupoDocumental>().Should().OnlyContain(r => r.UrlPortal == null,
            "el rol Cliente lee y no opera: no recibe la dirección del portal");

        var porCliente = await e.Handler().Handle(Agrupado(AgrupacionDocumentos.ClienteEmpresarial), default);
        porCliente.ResumenPorGrupo!.Values.OfType<ResumenDeGrupoDocumental>().Should().OnlyContain(r => r.UrlPortal == null);
    }

    [Fact]
    public async Task Abrir_portal_cuenta_solo_las_direcciones_de_los_Centros_que_se_gestionan()
    {
        var e = new Escenario();
        var cliente = e.ConEmpresa("Cliente empresarial");
        var gestionado = e.ConCentro(cliente, "Centro gestionado");
        var soloVisible = e.ConCentro(cliente, "Centro solo visible");
        var ctaima = e.ConPlataforma("Ctaima");
        var uno = e.ConDocumento();
        var dos = e.ConDocumento();
        e.Acreditar(uno, gestionado, ctaima, "https://ctaima.example.com/gestionado");
        e.Acreditar(dos, soloVisible, ctaima, "https://ctaima.example.com/visible");

        var gestor = new AlcanceDatosServiceFalso(tieneAccesoTotal: false, empresaIdsVisibles: [e.Contratista.Id],
            centroIdsVisibles: [gestionado.Id, soloVisible.Id], centroIdsParaGestion: [gestionado.Id]);
        var resultado = await e.Handler(gestor).Handle(Agrupado(AgrupacionDocumentos.PlataformaCae), default);

        Resumen(resultado, ctaima.Id.ToString()).Should().Match<ResumenDeGrupoDocumental>(
            r => r.Total == 2 && r.UrlPortal == "https://ctaima.example.com/gestionado",
            "el grupo cuenta los dos Centros visibles, pero la dirección solo sale de los que gestiona");
    }

    [Fact]
    public async Task Un_Centro_sin_gestion_CAE_no_aporta_grupo_ni_recuento_ni_portal()
    {
        var e = new Escenario();
        var cliente = e.ConEmpresa("Cliente empresarial");
        var conGestion = e.ConCentro(cliente, "Centro con gestión");
        var sinGestion = e.ConCentro(e.ConEmpresa("Cliente sin gestión"), "Centro sin gestión CAE");
        sinGestion.EstablecerGestionCae(ModalidadGestionCae.SinGestionCae);
        var nalanda = e.ConPlataforma("Nalanda");
        var ctaima = e.ConPlataforma("Ctaima");
        var documento = e.ConDocumento();
        e.Acreditar(documento, conGestion, nalanda, "https://nalanda.example.com");
        e.Acreditar(documento, sinGestion, ctaima, "https://ctaima.example.com");

        var porPlataforma = await e.Handler().Handle(Agrupado(AgrupacionDocumentos.PlataformaCae), default);

        porPlataforma.ResumenPorGrupo!.Keys.Should().BeEquivalentTo([nalanda.Id.ToString()],
            "una acreditación de un Centro sin gestión CAE no es exigible y no abre grupo");
        porPlataforma.Elementos.Should().ContainSingle();

        var porCliente = await e.Handler().Handle(Agrupado(AgrupacionDocumentos.ClienteEmpresarial), default);
        porCliente.ResumenPorGrupo!.Keys.Should().BeEquivalentTo([cliente.Id.ToString()]);
    }

    // ------------------------------------------------------------- recuentos del universo

    [Fact]
    public async Task Los_recuentos_de_grupo_cubren_todo_el_universo_filtrado_y_no_solo_la_pagina()
    {
        var e = new Escenario();
        var cliente = e.ConEmpresa("Cliente empresarial");
        var centro = e.ConCentro(cliente, "Centro");
        var nalanda = e.ConPlataforma("Nalanda");
        var pendiente = e.ConDocumento();
        e.Acreditar(pendiente, centro, nalanda);
        var rechazado = e.ConDocumento();
        e.Rechazar(e.Acreditar(rechazado, centro, nalanda), "Ilegible", DateTime.UtcNow);
        var sinVigencia = e.ConDocumento();
        e.Acreditar(sinVigencia, centro, nalanda).MarcarAceptada(VigenciaEnPlataforma.SinConfirmar);
        var conVigencia = e.ConDocumento();
        e.Acreditar(conVigencia, centro, nalanda).MarcarAceptada(VigenciaEnPlataforma.NoVenceAqui);
        e.ConDocumento();
        var handler = e.Handler();
        var query = Agrupado(AgrupacionDocumentos.PlataformaCae) with { TamanoPagina = 1 };

        var primera = await handler.Handle(query, default);
        var tercera = await handler.Handle(query with { Pagina = 3 }, default);

        primera.TotalElementos.Should().Be(5);
        primera.Elementos.Select(x => x.Id).Distinct().Should().ContainSingle("la página trae un Documento");
        var nalandaResumen = Resumen(primera, nalanda.Id.ToString());
        nalandaResumen.Total.Should().Be(4, "cuenta las acreditaciones de las cinco páginas, no las de la visible");
        nalandaResumen.EnPlataforma.Should().BeEquivalentTo(new Dictionary<EstadoEnPlataformaFiltro, int>
        {
            [EstadoEnPlataformaFiltro.PendienteDeSubir] = 1,
            [EstadoEnPlataformaFiltro.Rechazado] = 1,
            [EstadoEnPlataformaFiltro.SinVigencia] = 1,
        }, "la aceptada con vigencia no es ninguno de los tres, y los ceros no viajan");
        nalandaResumen.PorEstado.Should().BeEmpty();
        Resumen(primera, ObtenerDocumentosQuery.GrupoSin).Total.Should().Be(1);
        tercera.ResumenPorGrupo.Should().BeEquivalentTo(primera.ResumenPorGrupo);

        var buscado = await handler.Handle(query with { DocumentoId = rechazado.Id }, default);
        Resumen(buscado, nalanda.Id.ToString()).Total.Should().Be(1, "los recuentos se cuentan después de los demás filtros");
        buscado.ResumenPorGrupo.Should().NotContainKey(ObtenerDocumentosQuery.GrupoSin);
    }

    [Fact]
    public async Task Por_Cliente_empresarial_la_cabecera_cuenta_por_estado_documental_todo_el_universo()
    {
        var e = new Escenario();
        var cliente = e.ConEmpresa("Cliente empresarial");
        var centro = e.ConCentro(cliente, "Centro");
        var hoy = DiaDeNegocio.Hoy();
        for (var i = 0; i < 3; i++)
        {
            var vencido = e.ConDocumento();
            vencido.CorregirVigencia(hoy.AddDays(-30), VigenciaDocumento.VenceEl(hoy.AddDays(-1)));
            e.Acreditar(vencido, centro, proveedor: null);
        }
        e.Acreditar(e.ConDocumento(), centro, proveedor: null);

        var resultado = await e.Handler().Handle(Agrupado(AgrupacionDocumentos.ClienteEmpresarial) with { TamanoPagina = 1 }, default);

        var resumen = Resumen(resultado, cliente.Id.ToString());
        resumen.Total.Should().Be(4);
        resumen.PorEstado.Should().BeEquivalentTo(new Dictionary<string, int>
        {
            [nameof(EstadoDocumento.Vencido)] = 3,
            [nameof(EstadoDocumento.SinCaducidad)] = 1,
        });
        resumen.EnPlataforma.Should().BeEmpty();
    }

    // ------------------------------------------------------------- filtro «Estado en la plataforma»

    [Fact]
    public async Task El_filtro_Estado_en_la_plataforma_separa_sus_tres_estados()
    {
        var e = new Escenario();
        var cliente = e.ConEmpresa("Cliente empresarial");
        var centro = e.ConCentro(cliente, "Centro");
        var nalanda = e.ConPlataforma("Nalanda");
        var pendiente = e.ConDocumento();
        e.Acreditar(pendiente, centro, nalanda);
        var rechazado = e.ConDocumento();
        var acreditacionRechazada = e.Acreditar(rechazado, centro, nalanda);
        e.Rechazar(acreditacionRechazada, "Primer motivo", new DateTime(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc));
        e.Rechazar(acreditacionRechazada, "Último motivo", new DateTime(2026, 11, 1, 9, 0, 0, DateTimeKind.Utc));
        var sinVigencia = e.ConDocumento();
        e.Acreditar(sinVigencia, centro, nalanda).MarcarAceptada(VigenciaEnPlataforma.SinConfirmar);
        var conVigencia = e.ConDocumento();
        e.Acreditar(conVigencia, centro, nalanda).MarcarAceptada(VigenciaEnPlataforma.VenceEl(new DateOnly(2027, 1, 1)));
        var subida = e.ConDocumento();
        e.Acreditar(subida, centro, nalanda).MarcarSubida();
        var porCorreoPendiente = e.ConDocumento();
        e.Acreditar(porCorreoPendiente, centro, proveedor: null);
        var handler = e.Handler();

        (await handler.Handle(Filtrado(EstadoEnPlataformaFiltro.PendienteDeSubir), default)).Elementos.Select(x => x.Id)
            .Should().BeEquivalentTo([pendiente.Id], "una pendiente en un canal por correo no está en ninguna Plataforma CAE");
        (await handler.Handle(Filtrado(EstadoEnPlataformaFiltro.Rechazado), default)).Elementos.Select(x => x.Id)
            .Should().BeEquivalentTo([rechazado.Id]);
        (await handler.Handle(Filtrado(EstadoEnPlataformaFiltro.SinVigencia), default)).Elementos.Select(x => x.Id)
            .Should().BeEquivalentTo([sinVigencia.Id], "aceptada con la vigencia confirmada no es «Sin vigencia»");
        var dos = await handler.Handle(Filtrado(EstadoEnPlataformaFiltro.PendienteDeSubir, EstadoEnPlataformaFiltro.SinVigencia), default);
        dos.Elementos.Select(x => x.Id).Should().BeEquivalentTo([pendiente.Id, sinVigencia.Id]);
        dos.TotalElementos.Should().Be(2);

        var agrupados = await handler.Handle(Agrupado(AgrupacionDocumentos.PlataformaCae, EstadoEnPlataformaFiltro.Rechazado), default);
        var fila = agrupados.Elementos.Should().ContainSingle().Which;
        fila.Grupo!.Acreditacion!.Should().Match<AcreditacionEnGrupoDto>(a =>
            a.Id == acreditacionRechazada.Id && a.Estado == EstadoAcreditacion.Rechazada && a.UltimoMotivoRechazo == "Último motivo");
        Resumen(agrupados, nalanda.Id.ToString()).EnPlataforma.Should().BeEquivalentTo(
            new Dictionary<EstadoEnPlataformaFiltro, int> { [EstadoEnPlataformaFiltro.Rechazado] = 1 },
            "con el filtro, el grupo solo cuenta las acreditaciones que lo pasan");
        agrupados.ResumenPorGrupo.Should().NotContainKey(ObtenerDocumentosQuery.GrupoSin);
    }

    [Fact]
    public async Task El_filtro_de_estado_y_el_de_Plataforma_CAE_se_aplican_a_la_misma_acreditacion()
    {
        var e = new Escenario();
        var cliente = e.ConEmpresa("Cliente empresarial");
        var centro = e.ConCentro(cliente, "Centro");
        var nalanda = e.ConPlataforma("Nalanda");
        var ctaima = e.ConPlataforma("Ctaima");
        var documento = e.ConDocumento();
        e.Acreditar(documento, centro, nalanda).MarcarAceptada(VigenciaEnPlataforma.NoVenceAqui);
        e.Acreditar(documento, centro, ctaima);
        var handler = e.Handler();

        var cruzado = await handler.Handle(Filtrado(EstadoEnPlataformaFiltro.PendienteDeSubir) with { ProveedorPlataformaCaeId = nalanda.Id }, default);
        cruzado.Elementos.Should().BeEmpty("la pendiente es de Ctaima, no de Nalanda");

        var mismo = await handler.Handle(Agrupado(AgrupacionDocumentos.PlataformaCae, EstadoEnPlataformaFiltro.PendienteDeSubir)
            with { ProveedorPlataformaCaeId = ctaima.Id }, default);
        mismo.Elementos.Should().ContainSingle().Which.Grupo!.Clave.Should().Be(ctaima.Id.ToString());
        mismo.ResumenPorGrupo!.Keys.Should().BeEquivalentTo([ctaima.Id.ToString()]);
    }
}
