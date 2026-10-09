using CaeManager.Application.Common;
using CaeManager.Application.Documentos.Queries.ObtenerDocumentos;
using CaeManager.Application.Documentos.Queries.ObtenerPlataformasEnUso;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Common;
using CaeManager.Domain.Configuracion;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Integraciones;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests.Documentos;

/// <summary>
/// Filtros «Tipo de documento» y «Plataforma» del listado de Documentos y su orden de inicio.
///
/// Dos Clientes empresariales: uno dentro del alcance del usuario y otro fuera, con un Documento acreditado en
/// la misma plataforma que el de dentro. Es el caso que distingue un filtro que recorta lo visible de uno que lo
/// amplía: filtrar por esa plataforma no puede traer el Documento del Cliente empresarial que el usuario no ve.
/// </summary>
public class DocumentosFiltrosYOrdenDeInicioTests : IAsyncLifetime
{
    private const int UmbralAmbarDias = 30;
    private const int UmbralRojoDias = 15;
    private const string ClienteVisible = "Visible S.L.";
    private const string ClienteAjeno = "Fuera de alcance S.L.";

    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly DateOnly _hoy = DiaDeNegocio.Hoy();

    private Guid _clienteVisibleId;
    private Guid _centroVisibleId;
    private Guid _tipoSeguroId;
    private Guid _tipoHaciendaId;
    private Guid _plataformaCompartidaId;
    private Guid _plataformaSoloAjenaId;

    // Documentos del Cliente empresarial visible, en el orden de inicio esperado.
    private readonly List<Guid> _ordenDeInicioEsperado = [];
    private Guid _vencidoAcreditadoId;
    private Guid _emitidoMasRecienteId;

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto();
        await contexto.Database.MigrateAsync();

        var parametros = await contexto.ParametrosSistema.SingleOrDefaultAsync();
        if (parametros is null)
            contexto.ParametrosSistema.Add(new ParametroSistema(UmbralAmbarDias, UmbralRojoDias));
        else
            parametros.Actualizar(UmbralAmbarDias, UmbralRojoDias);

        var visible = Empresa.CrearComoCliente(ClienteVisible, "B12345674", false, null, null);
        var ajeno = Empresa.CrearComoCliente(ClienteAjeno, "B87654323", false, null, null);
        var propia = new Empresa("Montajes Springfield S.L.", "B10380186");
        contexto.Empresas.AddRange(visible, ajeno, propia);

        var seguro = new TipoDocumento("Seguro RC", 12, aplicaVencimientoAutomatico: true, 1, AmbitoAplicacion.Cliente, requerido: RequisitoDocumental.Si);
        var hacienda = new TipoDocumento("Certificado de Hacienda", 6, aplicaVencimientoAutomatico: true, 2, AmbitoAplicacion.Cliente, requerido: RequisitoDocumental.Si);
        contexto.TiposDocumento.AddRange(seguro, hacienda);

        var compartida = new ProveedorPlataformaCae($"PLAT-{Guid.NewGuid():N}"[..14], "Plataforma compartida");
        var soloAjena = new ProveedorPlataformaCae($"PLAT-{Guid.NewGuid():N}"[..14], "Plataforma solo del ajeno");
        contexto.ProveedoresPlataformaCae.AddRange(compartida, soloAjena);
        await contexto.SaveChangesAsync();

        (_clienteVisibleId, _tipoSeguroId, _tipoHaciendaId) = (visible.Id, seguro.Id, hacienda.Id);
        (_plataformaCompartidaId, _plataformaSoloAjenaId) = (compartida.Id, soloAjena.Id);

        var centroVisible = new Centro(visible.Id, propia.Id, "Centro visible");
        var centroAjeno = new Centro(ajeno.Id, propia.Id, "Centro ajeno");
        contexto.Centros.AddRange(centroVisible, centroAjeno);
        await contexto.SaveChangesAsync();
        _centroVisibleId = centroVisible.Id;

        var canalVisible = CanalGestionDocumental.DePlataforma(centroVisible.Id, "Acceso", compartida.Id, null, null, null);
        var canalAjenoCompartida = CanalGestionDocumental.DePlataforma(centroAjeno.Id, "Acceso", compartida.Id, null, null, null);
        var canalAjenoSolo = CanalGestionDocumental.DePlataforma(centroAjeno.Id, "Otro acceso", soloAjena.Id, null, null, null);
        contexto.CanalesGestionDocumental.AddRange(canalVisible, canalAjenoCompartida, canalAjenoSolo);

        // Cliente empresarial visible. Se siembran desordenados respecto al orden de inicio y con la emisión al
        // revés que la urgencia: el vigente es el emitido más recientemente, así que el orden por emisión
        // (el de siempre sin OrdenarPor) y el de inicio dan listas distintas.
        var sinCaducidad = Documento.DeCliente(visible.Id, seguro.Id, _hoy.AddDays(-50), VigenciaDocumento.NoCaduca);
        var vigente = Documento.DeCliente(visible.Id, seguro.Id, _hoy.AddDays(-1), VigenciaDocumento.VenceEl(_hoy.AddDays(UmbralAmbarDias + 60)));
        var sinConfirmar = Documento.DeCliente(visible.Id, hacienda.Id, _hoy.AddDays(-60), VigenciaDocumento.SinConfirmar);
        var proximo = Documento.DeCliente(visible.Id, seguro.Id, _hoy.AddDays(-70), VigenciaDocumento.VenceEl(_hoy.AddDays(UmbralAmbarDias - 1)));
        var urgente = Documento.DeCliente(visible.Id, hacienda.Id, _hoy.AddDays(-80), VigenciaDocumento.VenceEl(_hoy.AddDays(UmbralRojoDias - 1)));
        var vencidoReciente = Documento.DeCliente(visible.Id, seguro.Id, _hoy.AddDays(-90), VigenciaDocumento.VenceEl(_hoy.AddDays(-2)));
        var vencidoAntiguo = Documento.DeCliente(visible.Id, hacienda.Id, _hoy.AddDays(-100), VigenciaDocumento.VenceEl(_hoy.AddDays(-40)));
        contexto.Documentos.AddRange(sinCaducidad, vigente, sinConfirmar, proximo, urgente, vencidoReciente, vencidoAntiguo);

        // Cliente empresarial fuera de alcance.
        var ajenoVencido = Documento.DeCliente(ajeno.Id, seguro.Id, _hoy.AddDays(-90), VigenciaDocumento.VenceEl(_hoy.AddDays(-5)));
        var ajenoVigente = Documento.DeCliente(ajeno.Id, hacienda.Id, _hoy.AddDays(-10), VigenciaDocumento.VenceEl(_hoy.AddDays(200)));
        contexto.Documentos.AddRange(ajenoVencido, ajenoVigente);
        await contexto.SaveChangesAsync();

        contexto.AcreditacionesDocumentoPlataforma.AddRange(
            new AcreditacionDocumentoPlataforma(vencidoReciente.Id, canalVisible.Id),
            new AcreditacionDocumentoPlataforma(ajenoVencido.Id, canalAjenoCompartida.Id),
            new AcreditacionDocumentoPlataforma(ajenoVigente.Id, canalAjenoSolo.Id));
        await contexto.SaveChangesAsync();

        _ordenDeInicioEsperado.AddRange(
            [vencidoAntiguo.Id, vencidoReciente.Id, urgente.Id, proximo.Id, sinConfirmar.Id, vigente.Id, sinCaducidad.Id]);
        _vencidoAcreditadoId = vencidoReciente.Id;
        _emitidoMasRecienteId = vigente.Id;
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Fact]
    public async Task El_orden_de_inicio_pone_primero_lo_que_mas_urge_y_dentro_de_cada_estado_lo_que_antes_vence()
    {
        var resultado = await ConsultarAsync(
            new ObtenerDocumentosQuery(null, null, null, OrdenarPor: ObtenerDocumentosQuery.OrdenPorSeveridad, TamanoPagina: 50),
            SoloElClienteVisible());

        resultado.Elementos.Select(d => d.Id).Should().Equal(_ordenDeInicioEsperado);
        resultado.Elementos.Select(d => d.Estado).Should().Equal(
            EstadoDocumento.Vencido, EstadoDocumento.Vencido, EstadoDocumento.Urgente, EstadoDocumento.Proximo,
            EstadoDocumento.SinConfirmar, EstadoDocumento.Vigente, EstadoDocumento.SinCaducidad);
    }

    [Fact]
    public async Task Sin_orden_pedido_la_consulta_sigue_ordenando_por_emision_mas_reciente()
    {
        var resultado = await ConsultarAsync(
            new ObtenerDocumentosQuery(null, null, null, TamanoPagina: 50), SoloElClienteVisible());

        resultado.Elementos.First().Id.Should().Be(_emitidoMasRecienteId);
        resultado.Elementos.Select(d => d.FechaEmision).Should().BeInDescendingOrder();
    }

    [Fact]
    public async Task El_filtro_por_tipo_de_documento_deja_solo_los_de_ese_tipo()
    {
        var resultado = await ConsultarAsync(
            new ObtenerDocumentosQuery(null, null, null, TamanoPagina: 50, TipoDocumentoId: _tipoHaciendaId),
            SoloElClienteVisible());

        resultado.TotalElementos.Should().Be(3);
        resultado.Elementos.Should().HaveCount(3);
        resultado.Elementos.Select(d => d.TipoDocumentoId).Distinct().Should().Equal(_tipoHaciendaId);
    }

    [Fact]
    public async Task El_filtro_por_plataforma_deja_solo_los_documentos_acreditados_en_ella()
    {
        var resultado = await ConsultarAsync(
            new ObtenerDocumentosQuery(null, null, null, TamanoPagina: 50, ProveedorPlataformaCaeId: _plataformaCompartidaId),
            SoloElClienteVisible());

        resultado.TotalElementos.Should().Be(1);
        resultado.Elementos.Select(d => d.Id).Should().Equal(_vencidoAcreditadoId);
    }

    [Fact]
    public async Task Los_recuentos_de_la_franja_cuentan_ya_con_los_filtros_nuevos()
    {
        var resultado = await ConsultarAsync(
            new ObtenerDocumentosQuery(null, null, null, TamanoPagina: 50, ConRecuentosPorEstado: true, TipoDocumentoId: _tipoHaciendaId),
            SoloElClienteVisible());

        resultado.RecuentosPorEstado.Should().NotBeNull();
        resultado.RecuentosPorEstado!.Values.Sum().Should().Be(3);
    }

    /// <summary>
    /// El Documento del Cliente empresarial ajeno es del mismo tipo y está acreditado en la misma plataforma que
    /// el del visible: si alguno de los dos filtros sustituyera el alcance en vez de recortarlo, aparecería.
    /// </summary>
    [Fact]
    public async Task Ningun_filtro_trae_documentos_de_un_Cliente_empresarial_fuera_del_alcance()
    {
        var sinFiltro = await ConsultarAsync(new ObtenerDocumentosQuery(null, null, null, TamanoPagina: 50), SoloElClienteVisible());
        var visibles = sinFiltro.Elementos.Select(d => d.Id).ToHashSet();
        visibles.Should().HaveCount(7);

        var porTipo = await ConsultarAsync(
            new ObtenerDocumentosQuery(null, null, null, TamanoPagina: 50, TipoDocumentoId: _tipoSeguroId), SoloElClienteVisible());
        var porPlataforma = await ConsultarAsync(
            new ObtenerDocumentosQuery(null, null, null, TamanoPagina: 50, ProveedorPlataformaCaeId: _plataformaCompartidaId), SoloElClienteVisible());
        var porPlataformaAjena = await ConsultarAsync(
            new ObtenerDocumentosQuery(null, null, null, TamanoPagina: 50, ProveedorPlataformaCaeId: _plataformaSoloAjenaId), SoloElClienteVisible());

        porTipo.Elementos.Should().HaveCount(4);
        porTipo.Elementos.Select(d => d.Id).Should().BeSubsetOf(visibles);
        porTipo.Elementos.Select(d => d.PropietarioNombre).Should().NotContain(ClienteAjeno);
        porPlataforma.Elementos.Select(d => d.Id).Should().BeSubsetOf(visibles);
        porPlataforma.Elementos.Select(d => d.PropietarioNombre).Should().NotContain(ClienteAjeno);
        porPlataformaAjena.TotalElementos.Should().Be(0);
        porPlataformaAjena.Elementos.Should().BeEmpty();

        // Control positivo: sin alcance restringido, el filtro sí encuentra el Documento del otro Cliente
        // empresarial. Sin esto, las ausencias de arriba no distinguirían «el alcance lo excluye» de «no existe».
        var sinRestriccion = await ConsultarAsync(
            new ObtenerDocumentosQuery(null, null, null, TamanoPagina: 50, ProveedorPlataformaCaeId: _plataformaCompartidaId),
            new AlcanceDatosServiceFalso());
        sinRestriccion.Elementos.Select(d => d.PropietarioNombre).Should().BeEquivalentTo([ClienteVisible, ClienteAjeno]);
    }

    [Fact]
    public async Task Las_opciones_del_filtro_de_plataforma_son_las_de_los_centros_que_el_usuario_ve()
    {
        await using var contexto = CrearContexto();

        var conCartera = await new ObtenerPlataformasEnUsoQueryHandler(
                contexto, contexto, new AlcanceDatosServiceFalso(clienteIds: [_clienteVisibleId], centroIds: [_centroVisibleId]))
            .Handle(new ObtenerPlataformasEnUsoQuery(), CancellationToken.None);
        var sinRestriccion = await new ObtenerPlataformasEnUsoQueryHandler(contexto, contexto, new AlcanceDatosServiceFalso())
            .Handle(new ObtenerPlataformasEnUsoQuery(), CancellationToken.None);

        conCartera.Select(p => p.Id).Should().Equal(_plataformaCompartidaId);
        sinRestriccion.Select(p => p.Id).Should().BeEquivalentTo([_plataformaCompartidaId, _plataformaSoloAjenaId]);
    }

    private AlcanceDatosServiceFalso SoloElClienteVisible() =>
        new(clienteIds: [_clienteVisibleId], centroIds: [_centroVisibleId]);

    private async Task<ResultadoPaginado<DocumentoListaDto>> ConsultarAsync(
        ObtenerDocumentosQuery consulta, AlcanceDatosServiceFalso alcance)
    {
        await using var contexto = CrearContexto();
        var handler = new ObtenerDocumentosQueryHandler(
            contexto, contexto, contexto, contexto, contexto, contexto, contexto, alcance, contexto, contexto);
        return await handler.Handle(consulta, CancellationToken.None);
    }

    private CaeManagerDbContext CrearContexto()
    {
        var tenantActual = new TenantActualAmbiental { TenantId = _tenant };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual))
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
    }
}
