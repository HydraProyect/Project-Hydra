using CaeManager.Application.Alertas;
using CaeManager.Application.Alertas.Queries.ObtenerAlertas;
using CaeManager.Application.Asignaciones;
using CaeManager.Application.Clientes.Queries.ObtenerClientes;
using CaeManager.Application.Common;
using CaeManager.Application.Plataforma;
using CaeManager.Domain.Asignaciones;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Common;
using CaeManager.Domain.Configuracion;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Operaciones;
using CaeManager.Domain.Subcontratas;
using CaeManager.Domain.Tenants;
using CaeManager.Domain.Trabajadores;
using CaeManager.Infrastructure.Autorizacion;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Interceptors;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests.Clientes;

/// <summary>
/// Las incidencias de la fila de un Cliente empresarial (<see cref="ClienteListaDto.Incidencias"/>, opt-in con
/// <see cref="ObtenerClientesQuery.ConDesgloseDocumental"/>), leídas <b>como <c>cae_app_runtime</c></b> con los
/// interceptores de sellado y de sesión RLS de producción (mismo arnés que
/// <c>DesgloseDocumentalDeTrabajadoresBajoRlsTests</c>), con el <see cref="ObtenerAlertasQueryHandler"/> real
/// —de sus alertas sale la pastilla y de las mismas tiene que salir el desglose— y, donde se mide el alcance,
/// con el <see cref="AlcanceDatosService"/> real sobre una Asignación de Cartera acotada a un Cliente
/// empresarial.
///
/// <para>
/// Escenario, en el Tenant propietario de la sesión, con cuatro Tipos de documento de Trabajador exigidos:
/// <list type="bullet">
/// <item>«Cervezas Duff Ibérica» (en la cartera del Gestor CAE), con dos Centros. «Nora» trabaja en los dos:
/// aptitud vencida, formación urgente, contrato próximo y sin entrega de EPI, que le falta en cada Centro.
/// Cinco alertas.</item>
/// <item>«Central Nuclear de Springfield» (fuera de la cartera), con un Centro. «Iker»: aptitud vencida y los
/// otros tres Tipos sin documento. Cuatro alertas.</item>
/// <item>«Transportes Tope» (fuera de la cartera), con un Centro y cuatro Trabajadores sin ningún documento:
/// dieciséis alertas, más que el tope de la fila.</item>
/// </list>
/// Otro Tenant propietario tiene un Cliente empresarial gemelo con un Trabajador y un documento vencido. La
/// siembra va como propietario de la base, porque no es lo que se mide.
/// </para>
/// </summary>
public class DesgloseDocumentalDeClientesBajoAlcanceTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _gestor = Guid.NewGuid();
    private CaeManagerDbContext _propietario = null!;
    private CaeManagerDbContext _runtime = null!;
    private CaeManagerDbContext _runtimeDelGestor = null!;
    private TenantActualDeLaPeticion _tenantDeLaPeticion = null!;
    private Guid _tenantSesion;
    private Guid _tenantAjeno;
    private Guid _clienteDentro;
    private Guid _clienteFuera;
    private Guid _clienteTope;
    private Guid _clienteAjeno;
    private Guid _nora;
    private Guid _iker;
    private Guid _aptitudVencidaDeNora;
    private Guid _vencidoDeIker;
    private Guid _documentoAjeno;

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
        (await _runtime.Empresas.IgnoreQueryFilters().CountAsync(e => e.Id == _clienteAjeno)).Should().Be(0);
        (await _runtime.Documentos.IgnoreQueryFilters().CountAsync()).Should().Be(4,
            "control positivo: los cuatro documentos del Tenant de la sesión sí se ven (tres de Nora, uno de Iker)");

        (await _propietario.Documentos.IgnoreQueryFilters().CountAsync(d => d.Id == _documentoAjeno)).Should().Be(1);
        (await _propietario.Empresas.IgnoreQueryFilters().CountAsync(e => e.Id == _clienteAjeno)).Should().Be(1);
    }

    /// <summary>Por los dos caminos del handler: el de la pantalla (orden por estado y recuentos de la franja) y el llano.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Las_incidencias_de_la_fila_son_las_alertas_que_dan_su_pastilla_de_la_mas_grave_a_la_menos(bool comoLaPantalla)
    {
        var resultado = await HandlerSinCartera().Handle(Consulta(comoLaPantalla, conDesglose: true), CancellationToken.None);

        resultado.Elementos.Select(c => c.Id).Should().BeEquivalentTo([_clienteDentro, _clienteFuera, _clienteTope],
            "el Cliente empresarial del Tenant ajeno no es una fila de este listado");

        var dentro = resultado.Elementos.Single(c => c.Id == _clienteDentro);
        (dentro.EstadoDocumentalPeor, dentro.EstadoDocumentalCantidad).Should().Be(((EstadoDocumento?)EstadoDocumento.Vencido, 1), "la pastilla y su motivo");
        dentro.IncidenciasTotales.Should().Be(5);
        dentro.Incidencias.Select(i => (i.Estado, i.TipoDocumentoNombre, i.TrabajadorNombre)).Should().Equal(
            (EstadoDocumento.Vencido, "Aptitud médica", "Nora Dentro"),
            (EstadoDocumento.Faltante, "Entrega de EPI", "Nora Dentro"),
            (EstadoDocumento.Faltante, "Entrega de EPI", "Nora Dentro"),
            (EstadoDocumento.Urgente, "Formación Art. 19", "Nora Dentro"),
            (EstadoDocumento.Proximo, "Contrato de trabajo", "Nora Dentro"));

        var vencida = dentro.Incidencias[0];
        vencida.DocumentoId.Should().Be(_aptitudVencidaDeNora, "la línea lleva el Id del documento que se abre al pulsarla");
        vencida.TrabajadorId.Should().Be(_nora);
        vencida.FechaVencimiento.Should().Be(DiaDeNegocio.Hoy().AddDays(-10));
        vencida.CentroNombre.Should().BeNull("una alerta de vigencia no es de un Centro");

        var faltantes = dentro.Incidencias.Where(i => i.Estado == EstadoDocumento.Faltante).ToList();
        faltantes.Should().OnlyContain(i => i.DocumentoId == null && i.TrabajadorId == _nora && i.FechaVencimiento == null,
            "un faltante no tiene documento: se corrige dando de alta el del Trabajador y el Tipo");
        faltantes.Select(i => i.CentroNombre).Should().BeEquivalentTo(["Fábrica de Springfield", "Almacén de Shelbyville"],
            "el mismo Tipo le falta en los dos Centros: son dos alertas, y el Centro las distingue");
        dentro.Incidencias.Select(i => i.Clave).Should().OnlyHaveUniqueItems("la clave es el @key de la línea: repetida tumba el circuito");
    }

    /// <summary>
    /// El desglose no cuenta otra cosa que la pastilla: en una fila sin tope, el peor estado de sus incidencias es
    /// el de la pastilla y las de ese estado son las que dice el motivo. Con la fila que sí pasa del tope como
    /// control de que el caso se ejercita.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task El_peor_estado_y_el_recuento_de_las_incidencias_coinciden_con_la_pastilla_y_su_motivo(bool comoLaPantalla)
    {
        var resultado = await HandlerSinCartera().Handle(Consulta(comoLaPantalla, conDesglose: true), CancellationToken.None);

        var sinTope = resultado.Elementos.Where(c => c.IncidenciasTotales <= ObtenerClientesQuery.MaximoIncidenciasPorFila).ToList();
        sinTope.Select(c => c.Id).Should().BeEquivalentTo([_clienteDentro, _clienteFuera]);
        foreach (var fila in sinTope)
        {
            fila.Incidencias.Should().HaveCount(fila.IncidenciasTotales);
            fila.Incidencias[0].Estado.Should().Be(fila.EstadoDocumentalPeor!.Value);
            fila.Incidencias.Count(i => i.Estado == fila.EstadoDocumentalPeor).Should().Be(fila.EstadoDocumentalCantidad);
        }

        var fuera = resultado.Elementos.Single(c => c.Id == _clienteFuera);
        (fuera.EstadoDocumentalPeor, fuera.EstadoDocumentalCantidad, fuera.IncidenciasTotales).Should().Be(((EstadoDocumento?)EstadoDocumento.Vencido, 1, 4));
        fuera.Incidencias[0].DocumentoId.Should().Be(_vencidoDeIker);
    }

    [Fact]
    public async Task Con_mas_alertas_que_el_tope_viajan_las_primeras_y_el_total_dice_cuantas_hay()
    {
        var resultado = await HandlerSinCartera().Handle(Consulta(comoLaPantalla: true, conDesglose: true), CancellationToken.None);

        var tope = resultado.Elementos.Single(c => c.Id == _clienteTope);
        (tope.EstadoDocumentalPeor, tope.EstadoDocumentalCantidad).Should().Be(((EstadoDocumento?)EstadoDocumento.Faltante, 16),
            "cuatro Trabajadores sin ninguno de los cuatro Tipos exigidos");
        tope.IncidenciasTotales.Should().Be(16);
        ObtenerClientesQuery.MaximoIncidenciasPorFila.Should().Be(10, "el orden de abajo está escrito para este tope");
        tope.Incidencias.Select(i => (i.TrabajadorNombre, i.TipoDocumentoNombre)).Should().Equal(
            ("Ada Tope", "Aptitud médica"), ("Ada Tope", "Contrato de trabajo"), ("Ada Tope", "Entrega de EPI"), ("Ada Tope", "Formación Art. 19"),
            ("Bea Tope", "Aptitud médica"), ("Bea Tope", "Contrato de trabajo"), ("Bea Tope", "Entrega de EPI"), ("Bea Tope", "Formación Art. 19"),
            ("Cai Tope", "Aptitud médica"), ("Cai Tope", "Contrato de trabajo"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Sin_pedir_el_desglose_las_filas_llegan_sin_incidencias_y_con_la_misma_pastilla(bool comoLaPantalla)
    {
        var sin = await HandlerSinCartera().Handle(Consulta(comoLaPantalla, conDesglose: false), CancellationToken.None);
        var con = await HandlerSinCartera().Handle(Consulta(comoLaPantalla, conDesglose: true), CancellationToken.None);

        sin.Elementos.Should().OnlyContain(c => c.Incidencias.Count == 0 && c.IncidenciasTotales == 0);
        sin.Elementos.Select(c => (c.Id, c.EstadoDocumentalPeor, c.EstadoDocumentalCantidad)).Should().Equal(
            con.Elementos.Select(c => (c.Id, c.EstadoDocumentalPeor, c.EstadoDocumentalCantidad)),
            "pedir el desglose no cambia ni las filas ni su estado");
        con.Elementos.Sum(c => c.IncidenciasTotales).Should().Be(25, "control positivo: pedido, llegan las 5 + 4 + 16 alertas");
    }

    [Fact]
    public async Task La_consulta_por_id_devuelve_las_mismas_incidencias_que_la_fila_de_la_pagina()
    {
        var pagina = await HandlerSinCartera().Handle(Consulta(comoLaPantalla: true, conDesglose: true), CancellationToken.None);
        var porId = await HandlerSinCartera().Handle(
            new ObtenerClientesQuery(Busqueda: null, SoloCriticos: null, Id: _clienteDentro, ConDesgloseDocumental: true), CancellationToken.None);

        var fila = porId.Elementos.Should().ContainSingle().Subject;
        fila.Id.Should().Be(_clienteDentro);
        fila.Incidencias.Should().Equal(pagina.Elementos.Single(c => c.Id == _clienteDentro).Incidencias,
            "el refresco de una fila tras guardar no puede enseñar otra ventana que la de la carga");
        fila.IncidenciasTotales.Should().Be(5);
    }

    /// <summary>
    /// Un Gestor CAE con cartera sobre un solo Cliente empresarial: los que quedan fuera no son fila ni aportan
    /// ninguna incidencia, tampoco a la fila que sí ve.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Un_Cliente_empresarial_fuera_del_alcance_no_es_fila_ni_aporta_incidencias(bool comoLaPantalla)
    {
        var resultado = await HandlerDelGestor().Handle(Consulta(comoLaPantalla, conDesglose: true), CancellationToken.None);

        resultado.Elementos.Select(c => c.Id).Should().Equal([_clienteDentro], "la cartera solo alcanza a ese Cliente empresarial");
        resultado.TotalElementos.Should().Be(1);
        var incidencias = resultado.Elementos.SelectMany(c => c.Incidencias).ToList();
        incidencias.Should().HaveCount(5, "las cinco alertas de Nora y ninguna más");
        incidencias.Should().OnlyContain(i => i.TrabajadorId == _nora);
        incidencias.Select(i => i.DocumentoId).Should().NotContain(_vencidoDeIker);
        resultado.Elementos.Single().IncidenciasTotales.Should().Be(5);

        // Tampoco pidiéndolo por su Id.
        var porId = await HandlerDelGestor().Handle(
            new ObtenerClientesQuery(Busqueda: null, SoloCriticos: null, Id: _clienteFuera, ConDesgloseDocumental: true), CancellationToken.None);
        porId.Elementos.Should().BeEmpty();

        // Control positivo: sin cartera que acote, el de fuera es fila y trae las alertas de Iker.
        var sinCartera = await HandlerSinCartera().Handle(Consulta(comoLaPantalla, conDesglose: true), CancellationToken.None);
        var fuera = sinCartera.Elementos.Single(c => c.Id == _clienteFuera);
        fuera.Incidencias.Should().HaveCount(4).And.OnlyContain(i => i.TrabajadorId == _iker);
        fuera.Incidencias.Select(i => i.DocumentoId).Should().Contain(_vencidoDeIker);
    }

    /// <summary>La consulta de la pantalla: orden por peor estado y recuentos de la franja. La llana, sin nada de eso.</summary>
    private static ObtenerClientesQuery Consulta(bool comoLaPantalla, bool conDesglose) => comoLaPantalla
        ? new ObtenerClientesQuery(
            Busqueda: null, SoloCriticos: null, OrdenarPor: nameof(ClienteListaDto.EstadoDocumentalPeor),
            ConRecuentosPorEstado: true, ConDesgloseDocumental: conDesglose)
        : new ObtenerClientesQuery(Busqueda: null, SoloCriticos: null, ConDesgloseDocumental: conDesglose);

    private ObtenerClientesQueryHandler HandlerSinCartera() => Handler(_runtime, new AlcanceDatosServiceFalso());

    private ObtenerClientesQueryHandler HandlerDelGestor() => Handler(
        _runtimeDelGestor,
        new AlcanceDatosService(_runtimeDelGestor, UsuarioGestor(), _tenantDeLaPeticion, new SesionPrivilegiadaAusente()));

    /// <summary>
    /// El handler del listado con el de alertas real detrás, los dos sobre el mismo contexto y el MISMO servicio
    /// de alcance, como en una petición (el alcance es un servicio por petición).
    /// </summary>
    private static ObtenerClientesQueryHandler Handler(CaeManagerDbContext contexto, IAlcanceDatosService alcance) => new(
        contexto, contexto, contexto,
        new MediadorDeAlertas(new ObtenerAlertasQueryHandler(
            contexto, contexto, contexto, contexto, contexto, contexto, contexto,
            new ResolverClientePrincipalService(contexto, contexto, contexto), alcance,
            new DocumentosFaltantesService(contexto, contexto, contexto))),
        alcance);

    private CurrentUserServiceFalso UsuarioGestor() => new(_gestor, Roles.GestorCae, tenantOrigenId: _tenantSesion);

    private CaeManagerDbContext CrearRuntime(ICurrentUserService usuario)
    {
        var opciones = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(BaseDatosPostgresDePruebas.CadenaComoRuntime(_cadenaConexion))
            .AddInterceptors(
                new TenantSelladoInterceptor(_tenantDeLaPeticion),
                new TenantRlsConnectionInterceptor(_tenantDeLaPeticion, new SinClienteActivo(), usuario, BaseDatosPostgresDePruebas.FirmanteContextoRls))
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
        var clienteTope = Empresa.CrearComoCliente("Transportes Tope", "B10380186", false, null, null);
        var propia = new Empresa("Montajes Springfield S.L.", "B66778895");
        var subcontrataFuera = Empresa.CrearComoSubcontrata("Aislamientos Shelbyville S.L.", null, NivelServicioSubcontrata.Gestionada.ToString());
        _propietario.Empresas.AddRange(clienteDentro, clienteFuera, clienteTope, propia, subcontrataFuera);
        _propietario.ParametrosSistema.Add(new ParametroSistema(umbralAmbarDias: 30, umbralRojoDias: 15));

        var aptitud = TipoDeTrabajador("Aptitud médica");
        var formacion = TipoDeTrabajador("Formación Art. 19");
        var epi = TipoDeTrabajador("Entrega de EPI");
        var contrato = TipoDeTrabajador("Contrato de trabajo");
        _propietario.TiposDocumento.AddRange(aptitud, formacion, epi, contrato);
        await _propietario.SaveChangesAsync();

        var fabrica = new Centro(clienteDentro.Id, propia.Id, "Fábrica de Springfield");
        var almacen = new Centro(clienteDentro.Id, propia.Id, "Almacén de Shelbyville");
        var centroFuera = new Centro(clienteFuera.Id, subcontrataFuera.Id, "Sector 7G");
        var centroTope = new Centro(clienteTope.Id, propia.Id, "Muelle 4");
        var nora = Trabajador.DeEmpresa(propia.Id, "Nora", "Dentro", "22334455Y");
        var iker = Trabajador.DeSubcontrata(subcontrataFuera.Id, "Iker", "Fuera", "33445566R");
        Trabajador[] delTope =
        [
            Trabajador.DeEmpresa(propia.Id, "Ada", "Tope", "77189989B"),
            Trabajador.DeEmpresa(propia.Id, "Bea", "Tope", "11223344B"),
            Trabajador.DeEmpresa(propia.Id, "Cai", "Tope", "55667788Z"),
            Trabajador.DeEmpresa(propia.Id, "Dan", "Tope", "99887766P")
        ];
        _propietario.Centros.AddRange(fabrica, almacen, centroFuera, centroTope);
        _propietario.Trabajadores.AddRange(nora, iker);
        _propietario.Trabajadores.AddRange(delTope);
        await _propietario.SaveChangesAsync();

        _propietario.Asignaciones.AddRange(
            new Asignacion(nora.Id, fabrica.Id, hoy.AddDays(-30)),
            new Asignacion(nora.Id, almacen.Id, hoy.AddDays(-20)),
            new Asignacion(iker.Id, centroFuera.Id, hoy.AddDays(-30)));
        _propietario.Asignaciones.AddRange(delTope.Select(t => new Asignacion(t.Id, centroTope.Id, hoy.AddDays(-30))));

        var aptitudVencida = Documento.DeTrabajador(nora.Id, aptitud.Id, hoy.AddYears(-1), VigenciaDocumento.VenceEl(hoy.AddDays(-10)));
        var vencidoDeIker = Documento.DeTrabajador(iker.Id, aptitud.Id, hoy.AddYears(-1), VigenciaDocumento.VenceEl(hoy.AddDays(-3)));
        _propietario.Documentos.AddRange(
            aptitudVencida,
            Documento.DeTrabajador(nora.Id, formacion.Id, hoy.AddYears(-1), VigenciaDocumento.VenceEl(hoy.AddDays(5))),
            Documento.DeTrabajador(nora.Id, contrato.Id, hoy.AddYears(-1), VigenciaDocumento.VenceEl(hoy.AddDays(25))),
            vencidoDeIker);

        // Cartera del Gestor CAE: el Tenant entero en la cartera, acotado a UN Cliente empresarial en la operación.
        var operacion = AsignacionOperacion.Interna(
            _tenantSesion, ServicioCae.Outbound, AmbitoAsignacion.DeRelacionCliente(clienteDentro.Id), ahora, null, ahora);
        _propietario.AsignacionesOperacion.Add(operacion);
        _propietario.AsignacionesCartera.Add(AsignacionCartera.Interna(operacion, _gestor, AmbitoAsignacion.Universal, ahora, null, ahora));
        await _propietario.SaveChangesAsync();

        (_clienteDentro, _clienteFuera, _clienteTope) = (clienteDentro.Id, clienteFuera.Id, clienteTope.Id);
        (_nora, _iker) = (nora.Id, iker.Id);
        (_aptitudVencidaDeNora, _vencidoDeIker) = (aptitudVencida.Id, vencidoDeIker.Id);
    }

    private async Task SembrarTenantAjenoAsync()
    {
        using var ambito = AmbitoTenantExplicito.Establecer(_tenantAjeno);
        var hoy = DiaDeNegocio.Hoy();

        var gemelo = Empresa.CrearComoCliente("Cervezas Duff Ibérica", "B12345674", false, null, null);
        var propia = new Empresa("Montajes Springfield S.L.", "B66778895");
        _propietario.Empresas.AddRange(gemelo, propia);
        _propietario.ParametrosSistema.Add(new ParametroSistema(umbralAmbarDias: 30, umbralRojoDias: 15));
        var aptitud = TipoDeTrabajador("Aptitud médica");
        _propietario.TiposDocumento.Add(aptitud);
        await _propietario.SaveChangesAsync();

        var centro = new Centro(gemelo.Id, propia.Id, "Fábrica de Springfield");
        var trabajador = Trabajador.DeEmpresa(propia.Id, "Nora", "Dentro", "22334455Y");
        _propietario.Centros.Add(centro);
        _propietario.Trabajadores.Add(trabajador);
        await _propietario.SaveChangesAsync();

        var vencido = Documento.DeTrabajador(trabajador.Id, aptitud.Id, hoy.AddYears(-1), VigenciaDocumento.VenceEl(hoy.AddDays(-7)));
        _propietario.Asignaciones.Add(new Asignacion(trabajador.Id, centro.Id, hoy.AddDays(-30)));
        _propietario.Documentos.Add(vencido);
        await _propietario.SaveChangesAsync();

        _clienteAjeno = gemelo.Id;
        _documentoAjeno = vencido.Id;
    }

    private static TipoDocumento TipoDeTrabajador(string nombre) =>
        new(nombre, null, aplicaVencimientoAutomatico: false, 1, AmbitoAplicacion.Trabajador, requerido: RequisitoDocumental.Si);

    /// <summary>El handler del listado solo envía <see cref="ObtenerAlertasQuery"/>: va directo al handler real.</summary>
    private sealed class MediadorDeAlertas(ObtenerAlertasQueryHandler alertas) : IMediator
    {
        public async Task<T> Send<T>(IRequest<T> request, CancellationToken cancellationToken = default) =>
            request is ObtenerAlertasQuery consulta
                ? (T)(object)await alertas.Handle(consulta, cancellationToken)
                : throw new NotSupportedException(request.GetType().Name);

        public Task Send<T>(T request, CancellationToken cancellationToken = default) where T : IRequest => throw new NotSupportedException();
        public Task<object?> Send(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<T> CreateStream<T>(IStreamRequest<T> request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task Publish(object notification, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task Publish<T>(T notification, CancellationToken cancellationToken = default) where T : INotification => throw new NotSupportedException();
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
