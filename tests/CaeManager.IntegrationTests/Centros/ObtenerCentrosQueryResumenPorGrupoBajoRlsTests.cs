using CaeManager.Application.Centros;
using CaeManager.Application.Centros.Queries.ObtenerCentros;
using CaeManager.Application.Common;
using CaeManager.Domain.Asignaciones;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Common;
using CaeManager.Domain.Configuracion;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Tenants;
using CaeManager.Domain.Trabajadores;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Interceptors;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests.Centros;

/// <summary>
/// <c>ObtenerCentrosQuery(ConResumenPorGrupo: true)</c> rellena <c>ResultadoPaginado.ResumenPorGrupo</c>: lo que la
/// cabecera de grupo del listado de Centros enseña como total y como recuento por estado de cada Cliente
/// empresarial. La propiedad que se mide es que ese resumen describe <b>el grupo entero</b> —todas las páginas— y
/// <b>solo</b> lo que el usuario puede listar: ni Centros fuera de su alcance, ni Centros de otro Tenant
/// propietario, ni Centros que los filtros vigentes dejan fuera.
///
/// <para>
/// Se lee <b>como <c>cae_app_runtime</c></b> con los interceptores de sellado y de sesión RLS de producción (mismo
/// arnés que <c>CumplimientoCentroDocumentosDuplicadosBajoRlsTests</c>); la siembra va como propietario de la
/// base, porque no es lo que se mide. Sin ese arnés, «no cuenta los de otro Tenant» lo afirmaría solo el filtro
/// global de EF.
/// </para>
///
/// <para>
/// Datos del Tenant de la sesión: el Cliente empresarial Orion con tres Centros (uno vencido por un documento
/// de su Trabajador, dos vigentes) y Pegaso con dos vigentes. El Tenant ajeno tiene su propio Cliente
/// empresarial con dos Centros.
/// </para>
/// </summary>
public class ObtenerCentrosQueryResumenPorGrupoBajoRlsTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private CaeManagerDbContext _propietario = null!;
    private CaeManagerDbContext _runtime = null!;
    private Sembrado _sesion = null!;
    private Sembrado _ajeno = null!;

    /// <summary>Lo sembrado en un Tenant: sus dos Clientes empresariales y los Centros de cada uno.</summary>
    private sealed record Sembrado(Guid Orion, Guid Pegaso, IReadOnlyList<Guid> CentrosDeOrion, IReadOnlyList<Guid> CentrosDePegaso)
    {
        public string ClaveOrion => Orion.ToString();
        public string ClavePegaso => Pegaso.ToString();
    }

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

        _sesion = await SembrarAsync(tenantSesion.Id);
        _ajeno = await SembrarAsync(tenantAjeno.Id);

        var tenantDeLaPeticion = new TenantActualDeLaPeticion(tenantSesion.Id);
        var usuario = new CurrentUserServiceFalso(Guid.NewGuid(), tenantOrigenId: tenantSesion.Id);
        var opcionesRuntime = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(BaseDatosPostgresDePruebas.CadenaComoRuntime(_cadenaConexion))
            .AddInterceptors(
                new TenantSelladoInterceptor(tenantDeLaPeticion),
                new TenantRlsConnectionInterceptor(tenantDeLaPeticion, new SinClienteActivo(), usuario, BaseDatosPostgresDePruebas.FirmanteContextoRls))
            .Options;
        _runtime = new CaeManagerDbContext(opcionesRuntime, new EphemeralDataProtectionProvider(), tenantDeLaPeticion);
    }

    public async Task DisposeAsync()
    {
        await _runtime.DisposeAsync();
        await _propietario.DisposeAsync();
        await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);
    }

    /// <summary>Control del instrumento: la conexión de lectura está de verdad bajo RLS y la siembra ajena existe.</summary>
    [Fact]
    public async Task La_lectura_va_bajo_RLS_efectiva()
    {
        (await _propietario.Centros.IgnoreQueryFilters().CountAsync()).Should().Be(10,
            "control positivo: los cinco Centros de cada Tenant están sembrados");
        (await _runtime.Centros.IgnoreQueryFilters().CountAsync()).Should().Be(5,
            "sin filtro global de EF, solo RLS puede ocultar los Centros del Tenant ajeno");
    }

    /// <summary>(a) El grupo entero, no la página: con dos filas por página, Orion sigue contando sus tres Centros.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task El_resumen_cuenta_el_grupo_entero_aunque_la_pagina_solo_ensene_parte(int pagina)
    {
        var resultado = await ListarAsync(new ObtenerCentrosQuery(null, null, Pagina: pagina, TamanoPagina: 2, ConResumenPorGrupo: true));

        resultado.TotalElementos.Should().Be(5);
        resultado.Elementos.Count.Should().BeLessThan(3, "control: ninguna página enseña a Orion entero");
        var resumen = resultado.ResumenPorGrupo!;
        resumen.Keys.Should().BeEquivalentTo([_sesion.ClaveOrion, _sesion.ClavePegaso],
            "lleva también el grupo que no tiene ninguna fila en esta página");
        resumen[_sesion.ClaveOrion].Total.Should().Be(3);
        resumen[_sesion.ClaveOrion].PorEstado.Should().BeEquivalentTo(new Dictionary<string, int>
        {
            [nameof(EstadoCentro.Vencido)] = 1,
            [nameof(EstadoCentro.Vigente)] = 2,
        });
        resumen[_sesion.ClavePegaso].Total.Should().Be(2);
        resumen[_sesion.ClavePegaso].PorEstado.Should().BeEquivalentTo(new Dictionary<string, int>
        {
            [nameof(EstadoCentro.Vigente)] = 2,
        });
    }

    /// <summary>(b) Un Centro fuera del alcance del usuario no se lista: tampoco cuenta en el resumen de su grupo.</summary>
    [Fact]
    public async Task El_resumen_no_cuenta_los_Centros_fuera_del_alcance_del_usuario()
    {
        // La cartera deja fuera el Centro vencido de Orion y uno de los dos de Pegaso.
        var alcance = new AlcanceDatosServiceFalso(
            centroIds: [_sesion.CentrosDeOrion[1], _sesion.CentrosDeOrion[2], _sesion.CentrosDePegaso[0]]);

        var resultado = await ListarAsync(new ObtenerCentrosQuery(null, null, ConResumenPorGrupo: true), alcance);

        resultado.TotalElementos.Should().Be(3, "control: el alcance acota la lista");
        var resumen = resultado.ResumenPorGrupo!;
        resumen[_sesion.ClaveOrion].Total.Should().Be(2);
        resumen[_sesion.ClaveOrion].PorEstado.Should().NotContainKey(nameof(EstadoCentro.Vencido),
            "el Centro vencido de Orion no está en la cartera: contarlo diría que existe a quien no puede verlo");
        resumen[_sesion.ClavePegaso].Total.Should().Be(1);
    }

    /// <summary>Con la cartera vacía no hay ningún grupo que resumir.</summary>
    [Fact]
    public async Task Con_un_alcance_vacio_no_hay_ningun_grupo()
    {
        var resultado = await ListarAsync(
            new ObtenerCentrosQuery(null, null, ConResumenPorGrupo: true), new AlcanceDatosServiceFalso(centroIds: []));

        resultado.TotalElementos.Should().Be(0);
        resultado.ResumenPorGrupo.Should().BeEmpty();
    }

    /// <summary>(c) Bajo RLS, los Centros del Tenant ajeno ni forman grupo ni engordan uno propio.</summary>
    [Fact]
    public async Task El_resumen_no_cuenta_los_Centros_de_otro_Tenant()
    {
        var resultado = await ListarAsync(new ObtenerCentrosQuery(null, null, ConResumenPorGrupo: true));

        var resumen = resultado.ResumenPorGrupo!;
        resumen.Keys.Should().NotContain([_ajeno.ClaveOrion, _ajeno.ClavePegaso]);
        resumen.Values.Sum(grupo => grupo.Total).Should().Be(5, "los cinco Centros del Tenant de la sesión y ninguno más");
        resumen.Values.Sum(grupo => grupo.PorEstado.Values.Sum()).Should().Be(5, "los recuentos por estado suman el total");
    }

    /// <summary>(d) La búsqueda acota el resumen igual que la lista.</summary>
    [Fact]
    public async Task El_resumen_respeta_la_busqueda()
    {
        var resultado = await ListarAsync(new ObtenerCentrosQuery("taller", null, ConResumenPorGrupo: true));

        resultado.TotalElementos.Should().Be(1);
        var resumen = resultado.ResumenPorGrupo!;
        resumen.Keys.Should().BeEquivalentTo([_sesion.ClaveOrion], "Pegaso no tiene ningún Centro que case: no forma grupo");
        resumen[_sesion.ClaveOrion].Total.Should().Be(1);
    }

    /// <summary>
    /// (d) El filtro de estado acota el resumen: describe lo que se lista. La franja (<c>RecuentosPorEstado</c>)
    /// sigue contando sin ese filtro, que es su contrato: los dos no se confunden.
    /// </summary>
    [Fact]
    public async Task El_resumen_respeta_el_filtro_de_estado_y_la_franja_no()
    {
        var resultado = await ListarAsync(new ObtenerCentrosQuery(
            null, null, Estados: [EstadoCentro.Vencido], ConRecuentosPorEstado: true, ConResumenPorGrupo: true));

        resultado.TotalElementos.Should().Be(1);
        var resumen = resultado.ResumenPorGrupo!;
        resumen.Keys.Should().BeEquivalentTo([_sesion.ClaveOrion]);
        resumen[_sesion.ClaveOrion].Total.Should().Be(1);
        resumen[_sesion.ClaveOrion].PorEstado.Should().BeEquivalentTo(new Dictionary<string, int> { [nameof(EstadoCentro.Vencido)] = 1 });
        resultado.RecuentosPorEstado![nameof(EstadoCentro.Vigente)].Should().Be(4, "control: la franja cuenta sin el filtro de estado");
    }

    /// <summary>El filtro por Cliente empresarial deja un solo grupo.</summary>
    [Fact]
    public async Task El_resumen_respeta_el_filtro_por_Cliente_empresarial()
    {
        var resultado = await ListarAsync(new ObtenerCentrosQuery(null, _sesion.Pegaso, ConResumenPorGrupo: true));

        resultado.ResumenPorGrupo!.Keys.Should().BeEquivalentTo([_sesion.ClavePegaso]);
        resultado.ResumenPorGrupo[_sesion.ClavePegaso].Total.Should().Be(2);
    }

    /// <summary>Sin pedirlo no se calcula: <c>null</c> no es «ningún grupo».</summary>
    [Fact]
    public async Task Sin_pedirlo_el_resumen_no_viaja()
    {
        var resultado = await ListarAsync(new ObtenerCentrosQuery(null, null, ConRecuentosPorEstado: true));

        resultado.ResumenPorGrupo.Should().BeNull();
    }

    private Task<ResultadoPaginado<CentroListaDto>> ListarAsync(ObtenerCentrosQuery consulta, IAlcanceDatosService? alcance = null)
    {
        var handler = new ObtenerCentrosQueryHandler(
            _runtime, _runtime, alcance ?? new AlcanceDatosServiceFalso(),
            new CalculoEstadoCentroService(_runtime, _runtime, _runtime, _runtime, _runtime, _runtime));

        return handler.Handle(consulta, CancellationToken.None);
    }

    /// <summary>
    /// Dos Clientes empresariales y una Empresa que trabaja en sus cinco Centros. Solo el primer Centro de Orion
    /// tiene un Trabajador asignado, con su único documento obligatorio vencido: ese Centro sale Vencido y los
    /// otros cuatro, sin nada que exigir a nadie, Vigente.
    /// </summary>
    private async Task<Sembrado> SembrarAsync(Guid tenantId)
    {
        using var ambito = AmbitoTenantExplicito.Establecer(tenantId);
        var hoy = DiaDeNegocio.Hoy();

        var orion = Empresa.CrearComoCliente("Astilleros Orion", "B12345674", false, null, null);
        var pegaso = Empresa.CrearComoCliente("Bodegas Pegaso", "B10000032", false, null, null);
        var empresa = new Empresa("Montajes Ebro S.L.", "B87654323");
        _propietario.Empresas.AddRange(orion, pegaso, empresa);
        _propietario.ParametrosSistema.Add(new ParametroSistema(umbralAmbarDias: 30, umbralRojoDias: 15));
        var tipo = new TipoDocumento("Formación 60h", null, aplicaVencimientoAutomatico: false, 1, AmbitoAplicacion.Trabajador, requerido: RequisitoDocumental.Si);
        _propietario.TiposDocumento.Add(tipo);
        await _propietario.SaveChangesAsync();

        Centro[] centrosDeOrion =
        [
            new(orion.Id, empresa.Id, "Orion 1 Almacén"),
            new(orion.Id, empresa.Id, "Orion 2 Planta"),
            new(orion.Id, empresa.Id, "Orion 3 Taller"),
        ];
        Centro[] centrosDePegaso =
        [
            new(pegaso.Id, empresa.Id, "Pegaso 1 Nave"),
            new(pegaso.Id, empresa.Id, "Pegaso 2 Oficina"),
        ];
        var trabajador = Trabajador.DeEmpresa(empresa.Id, "Homer", "Simpson", "77189989B");
        _propietario.Centros.AddRange(centrosDeOrion);
        _propietario.Centros.AddRange(centrosDePegaso);
        _propietario.Trabajadores.Add(trabajador);
        await _propietario.SaveChangesAsync();

        _propietario.Asignaciones.Add(new Asignacion(trabajador.Id, centrosDeOrion[0].Id, hoy));
        _propietario.Documentos.Add(
            Documento.DeTrabajador(trabajador.Id, tipo.Id, hoy.AddYears(-1), VigenciaDocumento.VenceEl(hoy.AddDays(-10))));
        await _propietario.SaveChangesAsync();

        return new Sembrado(
            orion.Id, pegaso.Id, centrosDeOrion.Select(c => c.Id).ToList(), centrosDePegaso.Select(c => c.Id).ToList());
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
}
