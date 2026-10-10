using System.Data.Common;
using CaeManager.Application.Common;
using CaeManager.Application.Documentos;
using CaeManager.Application.Plataforma;
using CaeManager.Application.Vehiculos.Queries.ObtenerVehiculos;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Common;
using CaeManager.Domain.Configuracion;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Operaciones;
using CaeManager.Domain.RelacionesEmpresariales;
using CaeManager.Domain.Subcontratas;
using CaeManager.Domain.Tenants;
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

namespace CaeManager.IntegrationTests.Vehiculos;

/// <summary>
/// El desglose documental de la fila de un Vehículo (sus incidencias), leído <b>como <c>cae_app_runtime</c></b>
/// con los interceptores de sellado y de sesión RLS de producción (mismo arnés que
/// <c>DesgloseDocumentalDeTrabajadoresBajoRlsTests</c>) y, donde se mide el alcance, con el
/// <see cref="AlcanceDatosService"/> real sobre una Asignación de Cartera acotada a un Cliente empresarial.
///
/// <para>
/// Escenario, en el Tenant propietario de la sesión: «Camión grúa» (de la Empresa propia) con un documento
/// vencido, uno urgente, uno sin confirmar, uno vigente, uno eliminado y uno sustituido por su renovación;
/// «Remolque» (Empresa propia), sin documentos; «Plataforma elevadora», de una Subcontrata con Relación
/// Empresarial vigente con el Cliente empresarial de la cartera, con un documento próximo a vencer; y
/// «Furgoneta», de una Subcontrata cuya única Relación Empresarial es con un Cliente empresarial que la
/// cartera no alcanza, con un vencido. Otro Tenant propietario tiene un Vehículo gemelo del camión con dos
/// documentos vencidos. La siembra va como propietario de la base, porque no es lo que se mide.
/// </para>
/// </summary>
public class DesgloseDocumentalDeVehiculosBajoRlsTests : IAsyncLifetime
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
    private Guid _grua;
    private Guid _remolque;
    private Guid _plataforma;
    private Guid _furgoneta;
    private Guid _gemeloAjeno;
    private Guid _documentoAjeno;
    private Guid _itvVencida;

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
        (await _runtime.Vehiculos.IgnoreQueryFilters().CountAsync(v => v.Id == _gemeloAjeno)).Should().Be(0);
        (await _runtime.Documentos.IgnoreQueryFilters().CountAsync()).Should().Be(8,
            "control positivo: los ocho documentos del Tenant de la sesión sí se ven (seis del camión, uno de la plataforma, uno de la furgoneta)");

        // Con la conexión de propietario, lo que el desglose no debe contar está ahí.
        (await _propietario.Documentos.IgnoreQueryFilters().CountAsync(d => d.VehiculoId == _gemeloAjeno)).Should().Be(2);
        (await _propietario.Documentos.IgnoreQueryFilters().CountAsync(d => d.VehiculoId == _grua && d.EstaEliminado)).Should().Be(1);
        (await _propietario.Documentos.IgnoreQueryFilters().CountAsync(d => d.VehiculoId == _grua && d.SustituidoEnUtc != null)).Should().Be(1);
        (await _propietario.Documentos.IgnoreQueryFilters().CountAsync(d => d.VehiculoId == _furgoneta)).Should().Be(1);
    }

    /// <summary>
    /// (c): el desglose del camión no cuenta el documento eliminado ni el sustituido por su renovación (ni los
    /// del gemelo del otro Tenant). Por los dos caminos del handler (con recuentos por estado, que es el de la
    /// pantalla, y sin ellos). Al eliminado lo dejan fuera dos barreras (el filtro global del contexto y
    /// <c>Operativos()</c>) y este test no las distingue; al sustituido, solo <c>Operativos()</c>.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task El_desglose_cuenta_solo_los_documentos_operativos_del_Tenant_de_la_sesion(bool conRecuentosPorEstado)
    {
        var resultado = await HandlerSinCartera().Handle(
            new ObtenerVehiculosQuery(null, ConRecuentosPorEstado: conRecuentosPorEstado, ConDesgloseDocumental: true),
            CancellationToken.None);

        resultado.Elementos.Select(v => v.Id).Should().BeEquivalentTo([_grua, _remolque, _plataforma, _furgoneta],
            "el gemelo del Tenant ajeno no es una fila de este listado");

        var grua = resultado.Elementos.Single(v => v.Id == _grua);
        // Ni la ITV eliminada (vencida) ni la tarjeta de transporte sustituida (vencida) son incidencias.
        grua.Incidencias.Select(i => (i.TipoDocumentoNombre, i.Estado)).Should().Equal(
            ("ITV", EstadoDocumento.Vencido),
            ("Seguro del vehículo", EstadoDocumento.Urgente),
            ("Permiso de circulación", EstadoDocumento.SinConfirmar));
        grua.Incidencias[0].DocumentoId.Should().Be(_itvVencida, "la incidencia lleva el Id del documento que se abre al pulsarla");
        grua.Incidencias[1].FechaVencimiento.Should().Be(DiaDeNegocio.Hoy().AddDays(5));
        grua.EstadoDocumental.Should().Be(EstadoDocumento.Vencido);

        resultado.Elementos.Single(v => v.Id == _remolque).Incidencias.Should().BeEmpty("sin documentos no hay incidencias");
        resultado.Elementos.Single(v => v.Id == _plataforma).Incidencias.Should().ContainSingle()
            .Which.Estado.Should().Be(EstadoDocumento.Proximo);
    }

    /// <summary>
    /// (a): el aislamiento de <c>Documentos</c> por Tenant, observado en el servicio mismo y por la rama del
    /// ámbito Vehículo. El test de arriba no lo observa: el handler solo pasa al servicio los Ids de las filas de
    /// su página, y el gemelo del Tenant ajeno nunca es una de ellas. Aquí el Id ajeno SÍ entra en la lista
    /// pedida, junto al de un Vehículo propio.
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
    public async Task El_desglose_pedido_con_el_Id_de_un_Vehiculo_de_otro_Tenant_no_devuelve_nada_suyo()
    {
        var servicio = new CalculoEstadoDocumentalService(_runtime, _runtime, _runtime);

        var desgloses = await servicio.CalcularDesgloseAsync(
            AmbitoAplicacion.Vehiculo, [_gemeloAjeno, _grua], CancellationToken.None);

        // Control positivo: lo que no debe salir existe, es operativo (contaría si se leyera) y cuelga de ese Id.
        var documentosAjenos = await _propietario.Documentos.IgnoreQueryFilters().Operativos()
            .Where(d => d.VehiculoId == _gemeloAjeno).Select(d => d.Id).ToListAsync();
        documentosAjenos.Should().HaveCount(2).And.Contain(_documentoAjeno);

        desgloses.Should().NotContainKey(_gemeloAjeno, "el Vehículo de otro Tenant no tiene entrada: ni contadores ni incidencias");
        desgloses.Keys.Should().Equal([_grua], "solo el Vehículo propio");
        desgloses.Values.SelectMany(d => d.Incidencias).Select(i => i.DocumentoId).Should().NotIntersectWith(documentosAjenos);
        var grua = desgloses[_grua];
        grua.Incidencias.Should().HaveCount(3, "control positivo: el propio sí trae su desglose");
        (grua.DocumentosRegistrados, grua.DocumentosVigentes).Should().Be((4, 2), "los suyos, sin sumar los dos del gemelo");

        (await servicio.CalcularDesgloseAsync(AmbitoAplicacion.Vehiculo, [_gemeloAjeno], CancellationToken.None))
            .Should().BeEmpty("pedido solo, el Id ajeno tampoco devuelve nada");
    }

    /// <summary>
    /// La otra barrera, sola: con la conexión de propietario de la base (que RLS no acota) y el Tenant de la
    /// sesión como Tenant actual, lo único que separa al servicio de los documentos del Tenant ajeno es el filtro
    /// global de EF sobre <c>Documentos</c>. Es el test que se pone rojo si la lectura del desglose de Vehículos
    /// lo ignora.
    /// </summary>
    [Fact]
    public async Task Sin_RLS_el_filtro_global_de_Tenant_basta_para_que_el_desglose_no_cuente_al_Tenant_ajeno()
    {
        using var ambito = AmbitoTenantExplicito.Establecer(_tenantSesion);

        // Control del instrumento: en esta conexión no hay RLS que oculte los documentos del gemelo.
        (await _propietario.Documentos.IgnoreQueryFilters().Operativos().CountAsync(d => d.VehiculoId == _gemeloAjeno))
            .Should().Be(2, "si RLS los ocultara aquí, este test no mediría el filtro de EF");

        var desgloses = await new CalculoEstadoDocumentalService(_propietario, _propietario, _propietario)
            .CalcularDesgloseAsync(AmbitoAplicacion.Vehiculo, [_gemeloAjeno, _grua], CancellationToken.None);

        desgloses.Keys.Should().Equal([_grua], "el filtro global de Tenant deja fuera los documentos del gemelo ajeno");
        (desgloses[_grua].DocumentosRegistrados, desgloses[_grua].DocumentosVigentes).Should().Be((4, 2));
    }

    /// <summary>
    /// (e) Coherencia: el peor estado de las incidencias de una fila es el estado de la fila, y una fila sin
    /// incidencias está en un estado que no pide acción (o no tiene estado).
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task El_peor_estado_de_las_incidencias_coincide_con_el_estado_de_la_fila(bool conRecuentosPorEstado)
    {
        var resultado = await HandlerSinCartera().Handle(
            new ObtenerVehiculosQuery(null, ConRecuentosPorEstado: conRecuentosPorEstado, ConDesgloseDocumental: true),
            CancellationToken.None);

        resultado.Elementos.Should().HaveCount(4, "control positivo: hay filas con y sin incidencias");
        resultado.Elementos.Count(v => v.Incidencias.Count > 0).Should().Be(3, "el camión, la plataforma y la furgoneta");

        foreach (var fila in resultado.Elementos)
        {
            if (fila.Incidencias.Count > 0)
            {
                fila.Incidencias.MinBy(i => SeveridadEstadoDocumento.Rango(i.Estado))!.Estado
                    .Should().Be(fila.EstadoDocumental, $"la incidencia más grave de {fila.Nombre} es la que da su estado");
                fila.Incidencias.Select(i => SeveridadEstadoDocumento.Rango(i.Estado)).Should().BeInAscendingOrder();
            }
            else
            {
                (fila.EstadoDocumental is null or EstadoDocumento.Vigente or EstadoDocumento.SinCaducidad).Should().BeTrue(
                    $"{fila.Nombre} no tiene incidencias y su estado es {fila.EstadoDocumental}");
            }
        }
    }

    /// <summary>
    /// (d): el desglose es de quien lo pide. Una consulta que no dice nada de él (la del panel del Trabajador, la
    /// exportación, las comprobaciones de coherencia) no paga la consulta de documentos y recibe las filas sin
    /// incidencias. Por los dos caminos del handler.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Sin_pedir_el_desglose_no_se_consulta_y_las_filas_llegan_sin_el(bool conRecuentosPorEstado)
    {
        _contador.Reiniciar();

        // Sin ConDesgloseDocumental: lo que se mide es el valor por defecto de la consulta.
        var resultado = await HandlerSinCartera().Handle(
            new ObtenerVehiculosQuery(null, ConRecuentosPorEstado: conRecuentosPorEstado), CancellationToken.None);

        _contador.Consultas.Should().Be(0, "nadie pidió el desglose: no se lanza su consulta de documentos");
        resultado.Elementos.Should().HaveCount(4);
        resultado.Elementos.SelectMany(v => v.Incidencias).Should().BeEmpty();
        resultado.Elementos.Single(v => v.Id == _grua).EstadoDocumental.Should().Be(EstadoDocumento.Vencido, "el estado de la fila no depende del desglose");

        // Control positivo del contador y del escenario: la misma pregunta, pidiéndolo, sí lanza la consulta y
        // sí trae desglose.
        var pidiendolo = await HandlerSinCartera().Handle(
            new ObtenerVehiculosQuery(null, ConRecuentosPorEstado: conRecuentosPorEstado, ConDesgloseDocumental: true),
            CancellationToken.None);
        _contador.Consultas.Should().Be(1, "una sola consulta de desglose para toda la página, no una por Vehículo");
        pidiendolo.Elementos.Count(v => v.Incidencias.Count > 0).Should().Be(3, "control positivo: tres Vehículos con incidencias en la página");
    }

    /// <summary>
    /// (f): la consulta por id (la que la página usa para refrescar UNA fila tras guardar en la vista rápida)
    /// devuelve el mismo desglose que la consulta de página: la fila refrescada no pierde el motivo. Y sigue
    /// acotada por el alcance: el id de un Vehículo que el usuario no ve no devuelve nada.
    /// </summary>
    [Fact]
    public async Task La_consulta_por_id_devuelve_el_mismo_desglose_que_la_fila_de_la_pagina()
    {
        var consultaDeFila = new ObtenerVehiculosQuery(
            Busqueda: null, ConRecuentosPorEstado: true, VehiculoId: _grua, ConDesgloseDocumental: true);
        var pagina = await HandlerSinCartera().Handle(
            new ObtenerVehiculosQuery(null, ConRecuentosPorEstado: true, ConDesgloseDocumental: true), CancellationToken.None);
        var enLaPagina = pagina.Elementos.Single(v => v.Id == _grua);
        enLaPagina.Incidencias.Should().HaveCount(3, "control positivo: la fila de la página lleva desglose que comparar");

        var porId = await HandlerSinCartera().Handle(consultaDeFila, CancellationToken.None);

        var fila = porId.Elementos.Should().ContainSingle().Subject;
        fila.Should().BeEquivalentTo(enLaPagina, o => o.WithStrictOrdering(),
            "la fila refrescada es la misma fila, con sus incidencias en el mismo orden");

        var fueraDelAlcance = await HandlerDelGestor().Handle(
            new ObtenerVehiculosQuery(Busqueda: null, ConRecuentosPorEstado: true, VehiculoId: _furgoneta, ConDesgloseDocumental: true),
            CancellationToken.None);
        fueraDelAlcance.Elementos.Should().BeEmpty("pedir por id no salta la cartera");
        var dentroDelAlcance = await HandlerDelGestor().Handle(consultaDeFila, CancellationToken.None);
        dentroDelAlcance.Elementos.Should().ContainSingle("control positivo: el mismo Gestor sí recibe el camión por id")
            .Which.Incidencias.Should().HaveCount(3);
    }

    /// <summary>
    /// (b): un Gestor CAE con cartera sobre un solo Cliente empresarial recibe los Vehículos de la Empresa propia
    /// y los de la Subcontrata que trabaja para ese Cliente; no recibe el de la Subcontrata que solo trabaja para
    /// otro, ni su desglose.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Un_Gestor_CAE_con_cartera_acotada_no_recibe_filas_ni_desglose_de_fuera_de_su_alcance(bool conRecuentosPorEstado)
    {
        var resultado = await HandlerDelGestor().Handle(
            new ObtenerVehiculosQuery(null, ConRecuentosPorEstado: conRecuentosPorEstado, ConDesgloseDocumental: true),
            CancellationToken.None);

        resultado.Elementos.Select(v => v.Id).Should().BeEquivalentTo([_grua, _remolque, _plataforma],
            "la furgoneta es de una Subcontrata que solo trabaja para un Cliente empresarial que la cartera no alcanza");
        resultado.Elementos.SelectMany(v => v.Incidencias).Should().HaveCount(4, "las tres del camión y la de la plataforma");
        resultado.TotalElementos.Should().Be(3);

        // Control positivo: sin cartera que acote, la furgoneta y su vencido salen.
        var sinCartera = await HandlerSinCartera().Handle(
            new ObtenerVehiculosQuery(null, ConRecuentosPorEstado: conRecuentosPorEstado, ConDesgloseDocumental: true),
            CancellationToken.None);
        sinCartera.Elementos.Single(v => v.Id == _furgoneta).Incidencias.Should().ContainSingle()
            .Which.Estado.Should().Be(EstadoDocumento.Vencido);
    }

    private ObtenerVehiculosQueryHandler HandlerSinCartera() => new(
        _runtime, _runtime, new AlcanceDatosServiceFalso(), _runtime, _runtime,
        new CalculoEstadoDocumentalService(_runtime, _runtime, _runtime));

    private ObtenerVehiculosQueryHandler HandlerDelGestor() => new(
        _runtimeDelGestor, _runtimeDelGestor,
        new AlcanceDatosService(_runtimeDelGestor, UsuarioGestor(), _tenantDeLaPeticion, new SesionPrivilegiadaAusente()),
        _runtimeDelGestor, _runtimeDelGestor,
        new CalculoEstadoDocumentalService(_runtimeDelGestor, _runtimeDelGestor, _runtimeDelGestor));

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
        var propia = new Empresa("Montajes Springfield S.L.", "B10380186");
        var subcontrataDentro = Empresa.CrearComoSubcontrata("Elevaciones Ogdenville S.L.", null, NivelServicioSubcontrata.Gestionada.ToString());
        var subcontrataFuera = Empresa.CrearComoSubcontrata("Aislamientos Shelbyville S.L.", null, NivelServicioSubcontrata.Gestionada.ToString());
        _propietario.Empresas.AddRange(clienteDentro, clienteFuera, propia, subcontrataDentro, subcontrataFuera);
        _propietario.ParametrosSistema.Add(new ParametroSistema(umbralAmbarDias: 30, umbralRojoDias: 15));

        var itv = TipoDeVehiculo("ITV");
        var seguro = TipoDeVehiculo("Seguro del vehículo");
        var permiso = TipoDeVehiculo("Permiso de circulación");
        var tarjeta = TipoDeVehiculo("Tarjeta de transporte");
        _propietario.TiposDocumento.AddRange(itv, seguro, permiso, tarjeta);
        await _propietario.SaveChangesAsync();

        // Cada Subcontrata trabaja para un Cliente empresarial: una, para el de la cartera; la otra, para el que
        // la cartera no alcanza. La Empresa propia tiene además un Centro del Cliente empresarial de la cartera.
        _propietario.RelacionesEmpresariales.AddRange(
            RelacionEmpresarial.Crear(subcontrataDentro.Id, clienteDentro.Id, ahora),
            RelacionEmpresarial.Crear(subcontrataFuera.Id, clienteFuera.Id, ahora));
        _propietario.Centros.AddRange(
            new Centro(clienteDentro.Id, propia.Id, "Fábrica de Springfield"),
            new Centro(clienteFuera.Id, subcontrataFuera.Id, "Sector 7G"));

        var grua = Vehiculo.DeEmpresa(propia.Id, "Camión grúa", "Iveco Daily", "9012GHI");
        var remolque = Vehiculo.DeEmpresa(propia.Id, "Remolque", "Lecitrailer", "R1234BCD");
        var plataforma = Vehiculo.DeSubcontrata(subcontrataDentro.Id, "Plataforma elevadora", "Genie Z-45", "E5678DEF");
        var furgoneta = Vehiculo.DeSubcontrata(subcontrataFuera.Id, "Furgoneta", "Ford Transit", "1234ABC");
        _propietario.Vehiculos.AddRange(grua, remolque, plataforma, furgoneta);
        await _propietario.SaveChangesAsync();

        var itvVencida = Documento.DeVehiculo(grua.Id, itv.Id, hoy.AddYears(-1), VigenciaDocumento.VenceEl(hoy.AddDays(-10)));
        var seguroUrgente = Documento.DeVehiculo(grua.Id, seguro.Id, hoy.AddYears(-1), VigenciaDocumento.VenceEl(hoy.AddDays(5)));
        var permisoSinConfirmar = Documento.DeVehiculo(grua.Id, permiso.Id, hoy.AddDays(-20), VigenciaDocumento.SinConfirmar);
        var tarjetaAnterior = Documento.DeVehiculo(grua.Id, tarjeta.Id, hoy.AddYears(-2), VigenciaDocumento.VenceEl(hoy.AddDays(-40)));
        var tarjetaVigente = Documento.DeVehiculo(grua.Id, tarjeta.Id, hoy.AddDays(-5), VigenciaDocumento.VenceEl(hoy.AddYears(1)));
        var itvEliminada = Documento.DeVehiculo(grua.Id, itv.Id, hoy.AddYears(-2), VigenciaDocumento.VenceEl(hoy.AddDays(-200)));
        itvEliminada.MarcarComoEliminado(Guid.NewGuid());
        var seguroProximo = Documento.DeVehiculo(plataforma.Id, seguro.Id, hoy.AddYears(-1), VigenciaDocumento.VenceEl(hoy.AddDays(25)));
        var vencidoDeLaFurgoneta = Documento.DeVehiculo(furgoneta.Id, itv.Id, hoy.AddYears(-1), VigenciaDocumento.VenceEl(hoy.AddDays(-3)));
        _propietario.Documentos.AddRange(
            itvVencida, seguroUrgente, permisoSinConfirmar, tarjetaAnterior, tarjetaVigente, itvEliminada,
            seguroProximo, vencidoDeLaFurgoneta);
        await _propietario.SaveChangesAsync();

        tarjetaAnterior.SustituirPor(tarjetaVigente, MotivoSustitucionDocumento.Renovacion, ahora);

        // Cartera del Gestor CAE: el Tenant entero en la cartera, acotado a UN Cliente empresarial en la operación.
        var operacion = AsignacionOperacion.Interna(
            _tenantSesion, ServicioCae.Outbound, AmbitoAsignacion.DeRelacionCliente(clienteDentro.Id), ahora, null, ahora);
        _propietario.AsignacionesOperacion.Add(operacion);
        _propietario.AsignacionesCartera.Add(AsignacionCartera.Interna(operacion, _gestor, AmbitoAsignacion.Universal, ahora, null, ahora));
        await _propietario.SaveChangesAsync();

        (_grua, _remolque, _plataforma, _furgoneta) = (grua.Id, remolque.Id, plataforma.Id, furgoneta.Id);
        _itvVencida = itvVencida.Id;
    }

    private async Task SembrarTenantAjenoAsync()
    {
        using var ambito = AmbitoTenantExplicito.Establecer(_tenantAjeno);
        var hoy = DiaDeNegocio.Hoy();

        var propia = new Empresa("Montajes Springfield S.L.", "B10380186");
        _propietario.Empresas.Add(propia);
        _propietario.ParametrosSistema.Add(new ParametroSistema(umbralAmbarDias: 30, umbralRojoDias: 15));
        var itv = TipoDeVehiculo("ITV");
        var seguro = TipoDeVehiculo("Seguro del vehículo");
        _propietario.TiposDocumento.AddRange(itv, seguro);
        await _propietario.SaveChangesAsync();

        var gemelo = Vehiculo.DeEmpresa(propia.Id, "Camión grúa", "Iveco Daily", "9012GHI");
        _propietario.Vehiculos.Add(gemelo);
        await _propietario.SaveChangesAsync();

        var vencido = Documento.DeVehiculo(gemelo.Id, itv.Id, hoy.AddYears(-1), VigenciaDocumento.VenceEl(hoy.AddDays(-7)));
        _propietario.Documentos.AddRange(
            vencido,
            Documento.DeVehiculo(gemelo.Id, seguro.Id, hoy.AddYears(-1), VigenciaDocumento.VenceEl(hoy.AddDays(-8))));
        await _propietario.SaveChangesAsync();

        _gemeloAjeno = gemelo.Id;
        _documentoAjeno = vencido.Id;
    }

    private static TipoDocumento TipoDeVehiculo(string nombre) =>
        new(nombre, null, aplicaVencimientoAutomatico: false, 1, AmbitoAplicacion.Vehiculo, requerido: RequisitoDocumental.Si);

    /// <summary>
    /// Cuenta las consultas del desglose: las únicas de este handler que unen <c>Documentos</c> con
    /// <c>TiposDocumento</c>.
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
