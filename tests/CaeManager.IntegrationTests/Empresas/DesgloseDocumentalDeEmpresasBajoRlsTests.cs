using System.Data.Common;
using CaeManager.Application.Centros;
using CaeManager.Application.Common;
using CaeManager.Application.Documentos;
using CaeManager.Application.Empresas.Queries.ObtenerEmpresas;
using CaeManager.Application.Plataforma;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Common;
using CaeManager.Domain.Configuracion;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Operaciones;
using CaeManager.Domain.Subcontratas;
using CaeManager.Domain.Tenants;
using CaeManager.Domain.Trabajadores;
using CaeManager.Domain.Vehiculos;
using CaeManager.Infrastructure.Autorizacion;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Interceptors;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace CaeManager.IntegrationTests.Empresas;

/// <summary>
/// El desglose documental de la fila de una Empresa (sus incidencias), leído <b>como <c>cae_app_runtime</c></b>
/// con los interceptores de sellado y de sesión RLS de producción (mismo arnés que
/// <c>DesgloseDocumentalDeVehiculosBajoRlsTests</c>) y, donde se mide el alcance, con el
/// <see cref="AlcanceDatosService"/> real sobre una Asignación de Cartera acotada a un Cliente empresarial.
///
/// <para>
/// El desglose de una Empresa son sus documentos de ámbito Empresa, los mismos que deciden el
/// <see cref="EmpresaListaDto.EstadoDocumental"/> de la fila: no los de sus Trabajadores ni los de sus Vehículos.
/// </para>
///
/// <para>
/// Escenario, en el Tenant propietario de la sesión: «Montajes Springfield» (Empresa propia) con un documento
/// vencido, uno urgente, uno sin confirmar, uno vigente, uno eliminado y uno sustituido por su renovación, más
/// un Trabajador y un Vehículo suyos con un documento vencido cada uno; «Reformas Evergreen» (Empresa propia),
/// sin documentos; «Elevaciones Ogdenville», Empresa contraparte con un Centro del Cliente empresarial de la
/// cartera, con un documento próximo a vencer; y «Aislamientos Shelbyville», Empresa contraparte cuyo único
/// Centro es de un Cliente empresarial que la cartera no alcanza, con un vencido. Otro Tenant propietario tiene
/// una Empresa gemela de la primera con dos documentos vencidos. La siembra va como propietario de la base,
/// porque no es lo que se mide.
/// </para>
/// </summary>
public class DesgloseDocumentalDeEmpresasBajoRlsTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _gestor = Guid.NewGuid();
    private readonly ContadorDeConsultasDeDesglose _contador = new();
    private CaeManagerDbContext _propietario = null!;
    private CaeManagerDbContext _runtime = null!;
    private CaeManagerDbContext _runtimeDelGestor = null!;
    private TenantActualDeLaPeticion _tenantDeLaPeticion = null!;
    private Guid _tenantSesion;
    private Guid _tenantAjeno;
    private Guid _montajes;
    private Guid _reformas;
    private Guid _elevaciones;
    private Guid _aislamientos;
    private Guid _clienteDentro;
    private Guid _clienteFuera;
    private Guid _gemelaAjena;
    private Guid _documentoAjeno;
    private Guid _seguroVencido;
    private Guid _trabajadorDeMontajes;
    private Guid _vehiculoDeMontajes;

    public async Task InitializeAsync()
    {
        var tenantPorAmbito = new TenantActualPorAmbito();
        var opcionesPropietario = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantPorAmbito))
            .Options;
        _propietario = new CaeManagerDbContext(opcionesPropietario, new EphemeralDataProtectionProvider(), tenantPorAmbito);
        await _propietario.Database.MigrateAsync();

        var tenantSesion = new Tenant("Tenant propietario de la sesión");
        var tenantAjeno = new Tenant("Tenant propietario ajeno");
        _propietario.Tenants.AddRange(tenantSesion, tenantAjeno);
        await _propietario.SaveChangesAsync();
        _tenantSesion = tenantSesion.Id;
        _tenantAjeno = tenantAjeno.Id;

        await SembrarTenantDeLaSesionAsync();
        await SembrarTenantAjenoAsync();

        _tenantDeLaPeticion = new TenantActualDeLaPeticion(_tenantSesion);
        _runtime = CrearRuntime(new CurrentUserServiceFalso(Guid.NewGuid(), Roles.Administrador, tenantOrigenId: _tenantSesion));
        _runtimeDelGestor = CrearRuntime(UsuarioGestor());
    }

    public async Task DisposeAsync()
    {
        await _runtimeDelGestor.DisposeAsync();
        await _runtime.DisposeAsync();
        await _propietario.DisposeAsync();
        await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);
    }

    /// <summary>Control del instrumento: la conexión de lectura está de verdad bajo RLS, y lo «de fuera» existe.</summary>
    [Fact]
    public async Task La_lectura_va_bajo_RLS_efectiva_y_lo_que_no_debe_contar_existe()
    {
        (await _runtime.Documentos.IgnoreQueryFilters().CountAsync(d => d.Id == _documentoAjeno)).Should().Be(0,
            "sin filtro global de EF, solo RLS puede ocultar el Documento del Tenant ajeno");
        (await _runtime.Empresas.IgnoreQueryFilters().CountAsync(e => e.Id == _gemelaAjena)).Should().Be(0);
        (await _runtime.Documentos.IgnoreQueryFilters().CountAsync()).Should().Be(10,
            "control positivo: los diez documentos del Tenant de la sesión sí se ven (seis de Montajes, uno de Elevaciones, "
            + "uno de Aislamientos, uno del Trabajador y uno del Vehículo)");

        // Con la conexión de propietario, lo que el desglose no debe contar está ahí.
        (await _propietario.Documentos.IgnoreQueryFilters().CountAsync(d => d.EmpresaId == _gemelaAjena)).Should().Be(2);
        (await _propietario.Documentos.IgnoreQueryFilters().CountAsync(d => d.EmpresaId == _montajes && d.EstaEliminado)).Should().Be(1);
        (await _propietario.Documentos.IgnoreQueryFilters().CountAsync(d => d.EmpresaId == _montajes && d.SustituidoEnUtc != null)).Should().Be(1);
        (await _propietario.Documentos.IgnoreQueryFilters().CountAsync(d => d.EmpresaId == _aislamientos)).Should().Be(1);
        (await _propietario.Documentos.IgnoreQueryFilters().Operativos().CountAsync(d => d.TrabajadorId == _trabajadorDeMontajes)).Should().Be(1);
        (await _propietario.Documentos.IgnoreQueryFilters().Operativos().CountAsync(d => d.VehiculoId == _vehiculoDeMontajes)).Should().Be(1);
    }

    /// <summary>
    /// (c): el desglose de la Empresa no cuenta el documento eliminado ni el sustituido por su renovación (ni los
    /// de la gemela del otro Tenant), ni los documentos de su Trabajador y de su Vehículo, que son de otro ámbito.
    /// Por los dos caminos del handler (con recuentos por estado, que es el de la pantalla, y sin ellos). Al
    /// eliminado lo dejan fuera dos barreras (el filtro global del contexto y <c>Operativos()</c>) y este test no
    /// las distingue; al sustituido, solo <c>Operativos()</c>.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task El_desglose_cuenta_solo_los_documentos_operativos_de_ambito_Empresa_del_Tenant_de_la_sesion(bool conRecuentosPorEstado)
    {
        var resultado = await HandlerSinCartera().Handle(
            new ObtenerEmpresasQuery(null, ConRecuentosPorEstado: conRecuentosPorEstado, ConDesgloseDocumental: true),
            CancellationToken.None);

        resultado.Elementos.Select(e => e.Id).Should().BeEquivalentTo(
            [_montajes, _reformas, _elevaciones, _aislamientos, _clienteDentro, _clienteFuera],
            "la gemela del Tenant ajeno no es una fila de este listado");

        var montajes = resultado.Elementos.Single(e => e.Id == _montajes);
        // Ni el seguro eliminado (vencido) ni el certificado sustituido (vencido) son incidencias; tampoco la
        // aptitud vencida de su Trabajador ni la ITV vencida de su Vehículo.
        montajes.Incidencias.Select(i => (i.TipoDocumentoNombre, i.Estado)).Should().Equal(
            ("Seguro de responsabilidad civil", EstadoDocumento.Vencido),
            ("Certificado de la Seguridad Social", EstadoDocumento.Urgente),
            ("Modalidad preventiva", EstadoDocumento.SinConfirmar));
        montajes.Incidencias[0].DocumentoId.Should().Be(_seguroVencido, "la incidencia lleva el Id del documento que se abre al pulsarla");
        montajes.Incidencias[1].FechaVencimiento.Should().Be(DiaDeNegocio.Hoy().AddDays(5));
        montajes.EstadoDocumental.Should().Be(EstadoDocumento.Vencido);

        resultado.Elementos.Single(e => e.Id == _reformas).Incidencias.Should().BeEmpty("sin documentos no hay incidencias");
        resultado.Elementos.Single(e => e.Id == _elevaciones).Incidencias.Should().ContainSingle()
            .Which.Estado.Should().Be(EstadoDocumento.Proximo);
    }

    /// <summary>
    /// (a): el aislamiento de <c>Documentos</c> por Tenant, observado en el servicio mismo y por la rama del
    /// ámbito Empresa. El test de arriba no lo observa: el handler solo pasa al servicio los Ids de las filas de
    /// su página, y la gemela del Tenant ajeno nunca es una de ellas. Aquí el Id ajeno SÍ entra en la lista
    /// pedida, junto al de una Empresa propia.
    ///
    /// <para>
    /// Barrera que observa: en esta conexión (<c>cae_app_runtime</c>) actúan a la vez el filtro global de EF y
    /// RLS, y cualquiera de las dos basta. Cada una por separado: RLS sola, en
    /// <see cref="La_lectura_va_bajo_RLS_efectiva_y_lo_que_no_debe_contar_existe"/> (misma conexión, sin filtro
    /// de EF); el filtro de EF solo, en
    /// <see cref="Sin_RLS_el_filtro_global_de_Tenant_basta_para_que_el_desglose_no_cuente_al_Tenant_ajeno"/>.
    /// </para>
    /// </summary>
    [Fact]
    public async Task El_desglose_pedido_con_el_Id_de_una_Empresa_de_otro_Tenant_no_devuelve_nada_suyo()
    {
        var servicio = new CalculoEstadoDocumentalService(_runtime, _runtime, _runtime);

        var desgloses = await servicio.CalcularDesgloseAsync(
            AmbitoAplicacion.Empresa, [_gemelaAjena, _montajes], CancellationToken.None);

        // Control positivo: lo que no debe salir existe, es operativo (contaría si se leyera) y cuelga de ese Id.
        var documentosAjenos = await _propietario.Documentos.IgnoreQueryFilters().Operativos()
            .Where(d => d.EmpresaId == _gemelaAjena).Select(d => d.Id).ToListAsync();
        documentosAjenos.Should().HaveCount(2).And.Contain(_documentoAjeno);

        desgloses.Should().NotContainKey(_gemelaAjena, "la Empresa de otro Tenant no tiene entrada: ni contadores ni incidencias");
        desgloses.Keys.Should().Equal([_montajes], "solo la Empresa propia");
        desgloses.Values.SelectMany(d => d.Incidencias).Select(i => i.DocumentoId).Should().NotIntersectWith(documentosAjenos);
        var montajes = desgloses[_montajes];
        montajes.Incidencias.Should().HaveCount(3, "control positivo: la propia sí trae su desglose");
        (montajes.DocumentosRegistrados, montajes.DocumentosVigentes).Should().Be((4, 2), "los suyos, sin sumar los dos de la gemela");

        (await servicio.CalcularDesgloseAsync(AmbitoAplicacion.Empresa, [_gemelaAjena], CancellationToken.None))
            .Should().BeEmpty("pedido solo, el Id ajeno tampoco devuelve nada");
    }

    /// <summary>
    /// La otra barrera, sola: con la conexión de propietario de la base (que RLS no acota) y el Tenant de la
    /// sesión como Tenant actual, lo único que separa al servicio de los documentos del Tenant ajeno es el filtro
    /// global de EF sobre <c>Documentos</c>. Es el test que se pone rojo si la lectura del desglose de Empresas
    /// lo ignora.
    /// </summary>
    [Fact]
    public async Task Sin_RLS_el_filtro_global_de_Tenant_basta_para_que_el_desglose_no_cuente_al_Tenant_ajeno()
    {
        using var ambito = AmbitoTenantExplicito.Establecer(_tenantSesion);

        // Control del instrumento: en esta conexión no hay RLS que oculte los documentos de la gemela.
        (await _propietario.Documentos.IgnoreQueryFilters().Operativos().CountAsync(d => d.EmpresaId == _gemelaAjena))
            .Should().Be(2, "si RLS los ocultara aquí, este test no mediría el filtro de EF");

        var desgloses = await new CalculoEstadoDocumentalService(_propietario, _propietario, _propietario)
            .CalcularDesgloseAsync(AmbitoAplicacion.Empresa, [_gemelaAjena, _montajes], CancellationToken.None);

        desgloses.Keys.Should().Equal([_montajes], "el filtro global de Tenant deja fuera los documentos de la gemela ajena");
        (desgloses[_montajes].DocumentosRegistrados, desgloses[_montajes].DocumentosVigentes).Should().Be((4, 2));
    }

    /// <summary>
    /// (e) Coherencia: el peor estado de las incidencias de una fila es el estado de la fila, y una fila sin
    /// incidencias está en un estado que no pide acción (o no tiene estado: sin documentos, el camino con
    /// recuentos da «Sin caducidad» y el otro no da estado; el desglose no decide esa diferencia).
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task El_peor_estado_de_las_incidencias_coincide_con_el_estado_de_la_fila(bool conRecuentosPorEstado)
    {
        var resultado = await HandlerSinCartera().Handle(
            new ObtenerEmpresasQuery(null, ConRecuentosPorEstado: conRecuentosPorEstado, ConDesgloseDocumental: true),
            CancellationToken.None);

        resultado.Elementos.Should().HaveCount(6, "control positivo: hay filas con y sin incidencias");
        resultado.Elementos.Count(e => e.Incidencias.Count > 0).Should().Be(3, "Montajes, Elevaciones y Aislamientos");

        foreach (var fila in resultado.Elementos)
        {
            if (fila.Incidencias.Count > 0)
            {
                fila.Incidencias.MinBy(i => SeveridadEstadoDocumento.Rango(i.Estado))!.Estado
                    .Should().Be(fila.EstadoDocumental, $"la incidencia más grave de {fila.RazonSocial} es la que da su estado");
                fila.Incidencias.Select(i => SeveridadEstadoDocumento.Rango(i.Estado)).Should().BeInAscendingOrder();
            }
            else
            {
                (fila.EstadoDocumental is null or EstadoDocumento.Vigente or EstadoDocumento.SinCaducidad).Should().BeTrue(
                    $"{fila.RazonSocial} no tiene incidencias y su estado es {fila.EstadoDocumental}");
            }
        }
    }

    /// <summary>
    /// (d): el desglose es de quien lo pide. Una consulta que no dice nada de él (la exportación, la siembra, las
    /// comprobaciones de coherencia) no paga la consulta de documentos y recibe las filas sin incidencias. Por los
    /// dos caminos del handler.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Sin_pedir_el_desglose_no_se_consulta_y_las_filas_llegan_sin_el(bool conRecuentosPorEstado)
    {
        _contador.Reiniciar();

        // Sin ConDesgloseDocumental: lo que se mide es el valor por defecto de la consulta.
        var resultado = await HandlerSinCartera().Handle(
            new ObtenerEmpresasQuery(null, ConRecuentosPorEstado: conRecuentosPorEstado), CancellationToken.None);

        _contador.Consultas.Should().Be(0, "nadie pidió el desglose: no se lanza su consulta de documentos");
        resultado.Elementos.Should().HaveCount(6);
        resultado.Elementos.SelectMany(e => e.Incidencias).Should().BeEmpty();
        resultado.Elementos.Single(e => e.Id == _montajes).EstadoDocumental.Should().Be(EstadoDocumento.Vencido, "el estado de la fila no depende del desglose");

        // Control positivo del contador y del escenario: la misma pregunta, pidiéndolo, sí lanza la consulta y
        // sí trae desglose.
        var pidiendolo = await HandlerSinCartera().Handle(
            new ObtenerEmpresasQuery(null, ConRecuentosPorEstado: conRecuentosPorEstado, ConDesgloseDocumental: true),
            CancellationToken.None);
        _contador.Consultas.Should().Be(1, "una sola consulta de desglose para toda la página, no una por Empresa");
        pidiendolo.Elementos.Count(e => e.Incidencias.Count > 0).Should().Be(3, "control positivo: tres Empresas con incidencias en la página");
    }

    /// <summary>
    /// (f): la consulta por id (la que la página usa para refrescar UNA fila tras guardar en la vista rápida)
    /// devuelve el mismo desglose que la consulta de página: la fila refrescada no pierde el motivo. Y sigue
    /// acotada por el alcance: el id de una Empresa que el usuario no ve no devuelve nada.
    /// </summary>
    [Fact]
    public async Task La_consulta_por_id_devuelve_el_mismo_desglose_que_la_fila_de_la_pagina()
    {
        var consultaDeFila = new ObtenerEmpresasQuery(
            Busqueda: null, ConRecuentosPorEstado: true, EmpresaId: _montajes, ConDesgloseDocumental: true);
        var pagina = await HandlerSinCartera().Handle(
            new ObtenerEmpresasQuery(null, ConRecuentosPorEstado: true, ConDesgloseDocumental: true), CancellationToken.None);
        var enLaPagina = pagina.Elementos.Single(e => e.Id == _montajes);
        enLaPagina.Incidencias.Should().HaveCount(3, "control positivo: la fila de la página lleva desglose que comparar");

        var porId = await HandlerSinCartera().Handle(consultaDeFila, CancellationToken.None);

        var fila = porId.Elementos.Should().ContainSingle().Subject;
        fila.Should().BeEquivalentTo(enLaPagina, o => o.WithStrictOrdering(),
            "la fila refrescada es la misma fila, con sus incidencias en el mismo orden");

        var fueraDelAlcance = await HandlerDelGestor().Handle(
            new ObtenerEmpresasQuery(Busqueda: null, ConRecuentosPorEstado: true, EmpresaId: _aislamientos, ConDesgloseDocumental: true),
            CancellationToken.None);
        fueraDelAlcance.Elementos.Should().BeEmpty("pedir por id no salta la cartera");
        var dentroDelAlcance = await HandlerDelGestor().Handle(consultaDeFila, CancellationToken.None);
        dentroDelAlcance.Elementos.Should().ContainSingle("control positivo: el mismo Gestor CAE sí recibe Montajes por id")
            .Which.Incidencias.Should().HaveCount(3);
    }

    /// <summary>
    /// (b): un Gestor CAE con cartera sobre un solo Cliente empresarial recibe las Empresas propias del Tenant y
    /// la Empresa contraparte que tiene un Centro de ese Cliente; no recibe la que solo tiene Centro de otro, ni
    /// su desglose.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Un_Gestor_CAE_con_cartera_acotada_no_recibe_filas_ni_desglose_de_fuera_de_su_alcance(bool conRecuentosPorEstado)
    {
        var resultado = await HandlerDelGestor().Handle(
            new ObtenerEmpresasQuery(null, ConRecuentosPorEstado: conRecuentosPorEstado, ConDesgloseDocumental: true),
            CancellationToken.None);

        resultado.Elementos.Select(e => e.Id).Should().BeEquivalentTo([_montajes, _reformas, _elevaciones],
            "Aislamientos solo tiene Centro de un Cliente empresarial que la cartera no alcanza");
        resultado.Elementos.SelectMany(e => e.Incidencias).Should().HaveCount(4, "las tres de Montajes y la de Elevaciones");
        resultado.TotalElementos.Should().Be(3);

        // Control positivo: sin cartera que acote, Aislamientos y su vencido salen.
        var sinCartera = await HandlerSinCartera().Handle(
            new ObtenerEmpresasQuery(null, ConRecuentosPorEstado: conRecuentosPorEstado, ConDesgloseDocumental: true),
            CancellationToken.None);
        sinCartera.Elementos.Single(e => e.Id == _aislamientos).Incidencias.Should().ContainSingle()
            .Which.Estado.Should().Be(EstadoDocumento.Vencido);
    }

    private ObtenerEmpresasQueryHandler HandlerSinCartera() => new(
        _runtime, new AlcanceDatosServiceFalso(),
        new CalculoEstadoDocumentalService(_runtime, _runtime, _runtime),
        _runtime, _runtime, _runtime, _runtime,
        new CalculoEstadoCentroService(_runtime, _runtime, _runtime, _runtime, _runtime, _runtime));

    private ObtenerEmpresasQueryHandler HandlerDelGestor() => new(
        _runtimeDelGestor,
        new AlcanceDatosService(_runtimeDelGestor, UsuarioGestor(), _tenantDeLaPeticion, new SesionPrivilegiadaAusente()),
        new CalculoEstadoDocumentalService(_runtimeDelGestor, _runtimeDelGestor, _runtimeDelGestor),
        _runtimeDelGestor, _runtimeDelGestor, _runtimeDelGestor, _runtimeDelGestor,
        new CalculoEstadoCentroService(
            _runtimeDelGestor, _runtimeDelGestor, _runtimeDelGestor, _runtimeDelGestor, _runtimeDelGestor, _runtimeDelGestor));

    private CurrentUserServiceFalso UsuarioGestor() => new(_gestor, Roles.GestorCae, tenantOrigenId: _tenantSesion);

    private CaeManagerDbContext CrearRuntime(ICurrentUserService usuario)
    {
        var opciones = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(BaseDatosPostgresDePruebas.CadenaComoRuntime(_cadenaConexion))
            .AddInterceptors(
                new TenantSelladoInterceptor(_tenantDeLaPeticion),
                new TenantRlsConnectionInterceptor(_tenantDeLaPeticion, new SinClienteActivo(), usuario, BaseDatosPostgresDePruebas.FirmanteContextoRls),
                _contador)
            .Options;
        return new CaeManagerDbContext(opciones, new EphemeralDataProtectionProvider(), _tenantDeLaPeticion);
    }

    private async Task SembrarTenantDeLaSesionAsync()
    {
        using var ambito = AmbitoTenantExplicito.Establecer(_tenantSesion);
        var hoy = DiaDeNegocio.Hoy();
        var ahora = DateTime.UtcNow;

        var clienteDentro = Empresa.CrearComoCliente("Cervezas Duff Ibérica", "B12345674", false, null, null);
        var clienteFuera = Empresa.CrearComoCliente("Central Nuclear de Springfield", "B87654323", false, null, null);
        var montajes = new Empresa("Montajes Springfield S.L.", "B10380186");
        var reformas = new Empresa("Reformas Evergreen S.L.", null);
        var elevaciones = Empresa.CrearComoSubcontrata("Elevaciones Ogdenville S.L.", null, NivelServicioSubcontrata.Gestionada.ToString());
        var aislamientos = Empresa.CrearComoSubcontrata("Aislamientos Shelbyville S.L.", null, NivelServicioSubcontrata.Gestionada.ToString());
        _propietario.Empresas.AddRange(clienteDentro, clienteFuera, montajes, reformas, elevaciones, aislamientos);
        _propietario.ParametrosSistema.Add(new ParametroSistema(umbralAmbarDias: 30, umbralRojoDias: 15));

        var seguro = Tipo("Seguro de responsabilidad civil", AmbitoAplicacion.Empresa);
        var certificado = Tipo("Certificado de la Seguridad Social", AmbitoAplicacion.Empresa);
        var modalidad = Tipo("Modalidad preventiva", AmbitoAplicacion.Empresa);
        var hacienda = Tipo("Certificado de Hacienda", AmbitoAplicacion.Empresa);
        var aptitud = Tipo("Aptitud médica", AmbitoAplicacion.Trabajador);
        var itv = Tipo("ITV", AmbitoAplicacion.Vehiculo);
        _propietario.TiposDocumento.AddRange(seguro, certificado, modalidad, hacienda, aptitud, itv);
        await _propietario.SaveChangesAsync();

        // Cada Empresa contraparte tiene un Centro: una, del Cliente empresarial de la cartera; la otra, del que
        // la cartera no alcanza. Las Empresas propias no necesitan Centro para estar al alcance del Gestor CAE.
        _propietario.Centros.AddRange(
            new Centro(clienteDentro.Id, elevaciones.Id, "Fábrica de Springfield"),
            new Centro(clienteFuera.Id, aislamientos.Id, "Sector 7G"));

        // Sin Asignación a ningún Centro: su documento existe, pero no es de ámbito Empresa.
        var trabajador = Trabajador.DeEmpresa(montajes.Id, "Nora", "Dentro", "22334455Y");
        var vehiculo = Vehiculo.DeEmpresa(montajes.Id, "Camión grúa", "Iveco Daily", "9012GHI");
        _propietario.Trabajadores.Add(trabajador);
        _propietario.Vehiculos.Add(vehiculo);
        await _propietario.SaveChangesAsync();

        var seguroVencido = Documento.DeEmpresa(montajes.Id, seguro.Id, hoy.AddYears(-1), VigenciaDocumento.VenceEl(hoy.AddDays(-10)));
        var certificadoUrgente = Documento.DeEmpresa(montajes.Id, certificado.Id, hoy.AddYears(-1), VigenciaDocumento.VenceEl(hoy.AddDays(5)));
        var modalidadSinConfirmar = Documento.DeEmpresa(montajes.Id, modalidad.Id, hoy.AddDays(-20), VigenciaDocumento.SinConfirmar);
        var haciendaAnterior = Documento.DeEmpresa(montajes.Id, hacienda.Id, hoy.AddYears(-2), VigenciaDocumento.VenceEl(hoy.AddDays(-40)));
        var haciendaVigente = Documento.DeEmpresa(montajes.Id, hacienda.Id, hoy.AddDays(-5), VigenciaDocumento.VenceEl(hoy.AddYears(1)));
        var seguroEliminado = Documento.DeEmpresa(montajes.Id, seguro.Id, hoy.AddYears(-2), VigenciaDocumento.VenceEl(hoy.AddDays(-200)));
        seguroEliminado.MarcarComoEliminado(Guid.NewGuid());
        var seguroProximo = Documento.DeEmpresa(elevaciones.Id, seguro.Id, hoy.AddYears(-1), VigenciaDocumento.VenceEl(hoy.AddDays(25)));
        var vencidoDeAislamientos = Documento.DeEmpresa(aislamientos.Id, seguro.Id, hoy.AddYears(-1), VigenciaDocumento.VenceEl(hoy.AddDays(-3)));
        var aptitudVencida = Documento.DeTrabajador(trabajador.Id, aptitud.Id, hoy.AddYears(-1), VigenciaDocumento.VenceEl(hoy.AddDays(-15)));
        var itvVencida = Documento.DeVehiculo(vehiculo.Id, itv.Id, hoy.AddYears(-1), VigenciaDocumento.VenceEl(hoy.AddDays(-15)));
        _propietario.Documentos.AddRange(
            seguroVencido, certificadoUrgente, modalidadSinConfirmar, haciendaAnterior, haciendaVigente, seguroEliminado,
            seguroProximo, vencidoDeAislamientos, aptitudVencida, itvVencida);
        await _propietario.SaveChangesAsync();

        haciendaAnterior.SustituirPor(haciendaVigente, MotivoSustitucionDocumento.Renovacion, ahora);

        // Cartera del Gestor CAE: el Tenant entero en la cartera, acotado a UN Cliente empresarial en la operación.
        var operacion = AsignacionOperacion.Interna(
            _tenantSesion, ServicioCae.Outbound, AmbitoAsignacion.DeRelacionCliente(clienteDentro.Id), ahora, null, ahora);
        _propietario.AsignacionesOperacion.Add(operacion);
        _propietario.AsignacionesCartera.Add(AsignacionCartera.Interna(operacion, _gestor, AmbitoAsignacion.Universal, ahora, null, ahora));
        await _propietario.SaveChangesAsync();

        (_montajes, _reformas, _elevaciones, _aislamientos) = (montajes.Id, reformas.Id, elevaciones.Id, aislamientos.Id);
        (_clienteDentro, _clienteFuera) = (clienteDentro.Id, clienteFuera.Id);
        (_trabajadorDeMontajes, _vehiculoDeMontajes) = (trabajador.Id, vehiculo.Id);
        _seguroVencido = seguroVencido.Id;
    }

    private async Task SembrarTenantAjenoAsync()
    {
        using var ambito = AmbitoTenantExplicito.Establecer(_tenantAjeno);
        var hoy = DiaDeNegocio.Hoy();

        var gemela = new Empresa("Montajes Springfield S.L.", "B10380186");
        _propietario.Empresas.Add(gemela);
        _propietario.ParametrosSistema.Add(new ParametroSistema(umbralAmbarDias: 30, umbralRojoDias: 15));
        var seguro = Tipo("Seguro de responsabilidad civil", AmbitoAplicacion.Empresa);
        var certificado = Tipo("Certificado de la Seguridad Social", AmbitoAplicacion.Empresa);
        _propietario.TiposDocumento.AddRange(seguro, certificado);
        await _propietario.SaveChangesAsync();

        var vencido = Documento.DeEmpresa(gemela.Id, seguro.Id, hoy.AddYears(-1), VigenciaDocumento.VenceEl(hoy.AddDays(-7)));
        _propietario.Documentos.AddRange(
            vencido,
            Documento.DeEmpresa(gemela.Id, certificado.Id, hoy.AddYears(-1), VigenciaDocumento.VenceEl(hoy.AddDays(-8))));
        await _propietario.SaveChangesAsync();

        _gemelaAjena = gemela.Id;
        _documentoAjeno = vencido.Id;
    }

    private static TipoDocumento Tipo(string nombre, AmbitoAplicacion ambito) =>
        new(nombre, null, aplicaVencimientoAutomatico: false, 1, ambito, requerido: RequisitoDocumental.Si);

    /// <summary>
    /// Cuenta las consultas del desglose: las únicas de este handler que unen <c>Documentos</c> con
    /// <c>TiposDocumento</c> en este escenario (ningún Trabajador tiene Asignación a un Centro, así que el
    /// cálculo de cumplimiento de la página no llega a leer documentos).
    /// </summary>
    private sealed class ContadorDeConsultasDeDesglose : DbCommandInterceptor
    {
        private int _consultas;

        public int Consultas => Volatile.Read(ref _consultas);

        public void Reiniciar() => Interlocked.Exchange(ref _consultas, 0);

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("\"Documentos\"", StringComparison.Ordinal)
                && command.CommandText.Contains("\"TiposDocumento\"", StringComparison.Ordinal))
                Interlocked.Increment(ref _consultas);

            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private sealed class TenantActualPorAmbito : ITenantActual
    {
        public Guid? TenantId => AmbitoTenantExplicito.TenantIdActual;
    }

    /// <summary>Como el <c>TenantActual</c> real: primero el ámbito explícito, luego el Tenant de la sesión.</summary>
    private sealed class TenantActualDeLaPeticion(Guid tenantDeLaSesion) : ITenantActual
    {
        public Guid? TenantId => AmbitoTenantExplicito.TenantIdActual ?? tenantDeLaSesion;
    }

    private sealed class SinClienteActivo : IClienteActivoSeleccionado
    {
        public Guid? TenantIdSeleccionado => null;
        public Guid? AsignacionOperacionIdSeleccionada => null;
        public Guid? SesionPrivilegiadaIdSeleccionada => null;
    }

    private sealed class SesionPrivilegiadaAusente : ISesionPrivilegiadaActual
    {
        public Task<SesionPrivilegiadaActiva?> ObtenerAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<SesionPrivilegiadaActiva?>(null);

        public Task<SesionPrivilegiadaActiva?> RevalidarAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<SesionPrivilegiadaActiva?>(null);
    }
}
