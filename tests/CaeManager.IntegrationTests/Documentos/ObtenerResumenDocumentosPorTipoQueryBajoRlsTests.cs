using System.Data.Common;
using CaeManager.Application.Common;
using CaeManager.Application.Documentos.Queries.ObtenerDocumentos;
using CaeManager.Application.Documentos.Queries.ObtenerResumenDocumentosPorTipo;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Common;
using CaeManager.Domain.Configuracion;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Integraciones;
using CaeManager.Domain.Proyectos;
using CaeManager.Domain.Tenants;
using CaeManager.Domain.Trabajadores;
using CaeManager.Domain.Vehiculos;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Interceptors;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace CaeManager.IntegrationTests.Documentos;

/// <summary>
/// <c>ObtenerResumenDocumentosPorTipoQuery</c> alimenta las cabeceras del listado de Documentos agrupado por Tipo de
/// documento: por cada tipo, cuántos Documentos tiene y cuántos en cada estado. La propiedad que se mide es que
/// cada cabecera cuenta <b>exactamente lo que se lista al abrir el grupo</b> —<c>ObtenerDocumentosQuery</c> con los
/// mismos filtros y ese tipo— y <b>solo</b> lo que el usuario puede listar: ni Documentos fuera de su alcance, ni
/// de otro Tenant propietario, ni borrados, ni los que los filtros vigentes dejan fuera.
///
/// <para>
/// Se lee <b>como <c>cae_app_runtime</c></b> con los interceptores de sellado y de sesión RLS de producción (mismo
/// arnés que <c>ObtenerCentrosQueryResumenPorGrupoBajoRlsTests</c>); la siembra va como propietario de la base,
/// porque no es lo que se mide.
/// </para>
///
/// <para>
/// Datos de cada Tenant, uno por ámbito de Documento: «Formación PRL» (Trabajador: Homer, uno vencido y veintiuno
/// vigentes; Lisa, uno urgente), «Seguro RC» (Cliente empresarial: Orion, uno vigente y uno sin caducidad; Pegaso,
/// uno vencido), «Certificado de Hacienda» (Empresa Ebro: uno próximo y uno sin confirmar), «ITV» (Vehículo: uno
/// urgente y uno vencido borrado) y «Plan de seguridad» (Proyecto: el de Orion vigente, el de Pegaso vencido).
/// Treinta y un Documentos vivos que caen en once pares (tipo, estado).
/// </para>
/// </summary>
public class ObtenerResumenDocumentosPorTipoQueryBajoRlsTests : IAsyncLifetime
{
    private const int UmbralAmbarDias = 30;
    private const int UmbralRojoDias = 15;
    private const int VigentesDeHomer = 21;
    private const int DocumentosVivosPorTenant = 31;
    private const int ParesTipoEstadoPorTenant = 11;

    private const string Formacion = "Formación PRL";
    private const string Seguro = "Seguro RC";
    private const string Hacienda = "Certificado de Hacienda";
    private const string Itv = "ITV";
    private const string Plan = "Plan de seguridad";

    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly RegistroDeLecturas _lecturas = new();
    private readonly DateOnly _hoy = DiaDeNegocio.Hoy();
    private CaeManagerDbContext _propietario = null!;
    private CaeManagerDbContext _runtime = null!;
    private Sembrado _sesion = null!;
    private Sembrado _ajeno = null!;

    private sealed record Sembrado(
        Guid Orion, Guid Pegaso, Guid Ebro, Guid Homer, Guid Lisa, Guid Camion,
        Guid TipoFormacion, Guid TipoSeguro, Guid TipoHacienda, Guid TipoItv, Guid TipoPlan,
        Guid Plataforma, Guid DocumentoVencidoDeHomer)
    {
        public IReadOnlyList<Guid> Tipos => [TipoFormacion, TipoSeguro, TipoHacienda, TipoItv, TipoPlan];
    }

    /// <summary>Los filtros de vista, una sola vez, para pedir con ellos el resumen y el listado.</summary>
    private sealed record Filtros(
        Guid? TrabajadorId = null, AmbitoAplicacion? Ambito = null, string? Busqueda = null, EstadoDocumento? Estado = null,
        IReadOnlyCollection<EstadoDocumento>? Estados = null, Guid? PropietarioId = null,
        DateOnly? FechaVencimientoHasta = null, Guid? DocumentoId = null, Guid? TipoDocumentoId = null,
        Guid? ProveedorPlataformaCaeId = null)
    {
        public ObtenerResumenDocumentosPorTipoQuery Resumen() => new(
            TrabajadorId, Ambito, Busqueda, Estado, Estados, PropietarioId, FechaVencimientoHasta, DocumentoId,
            TipoDocumentoId, ProveedorPlataformaCaeId);

        public ObtenerDocumentosQuery Listado() => new(
            TrabajadorId, Ambito, Busqueda, Estado, Pagina: 1, TamanoPagina: 1, PropietarioId: PropietarioId,
            FechaVencimientoHasta: FechaVencimientoHasta, Estados: Estados, DocumentoId: DocumentoId,
            TipoDocumentoId: TipoDocumentoId, ProveedorPlataformaCaeId: ProveedorPlataformaCaeId);

        public IReadOnlyCollection<EstadoDocumento> EstadosPedidos() =>
            [.. (Estados ?? []), .. (Estado is { } uno ? new[] { uno } : [])];
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
                new TenantRlsConnectionInterceptor(tenantDeLaPeticion, new SinClienteActivo(), usuario, BaseDatosPostgresDePruebas.FirmanteContextoRls),
                _lecturas)
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
        (await _propietario.Documentos.IgnoreQueryFilters().CountAsync()).Should().Be(2 * (DocumentosVivosPorTenant + 1),
            "control positivo: los treinta y un Documentos vivos y el borrado de cada Tenant están sembrados");
        (await _runtime.Documentos.IgnoreQueryFilters().CountAsync()).Should().Be(DocumentosVivosPorTenant + 1,
            "sin filtro global de EF, solo RLS puede ocultar los Documentos del Tenant ajeno");
    }

    /// <summary>(a) Un grupo por tipo, con Documentos de los cinco ámbitos, su total y su recuento por estado.</summary>
    [Fact]
    public async Task Agrupa_por_tipo_los_documentos_de_los_cinco_ambitos_con_su_total_y_su_recuento_por_estado()
    {
        var resumen = await ResumirAsync(new Filtros());

        Textos(resumen).Should().BeEquivalentTo(
        [
            $"{Formacion}: 23 [Urgente=1, Vencido=1, Vigente=21]",
            $"{Seguro}: 3 [SinCaducidad=1, Vencido=1, Vigente=1]",
            $"{Hacienda}: 2 [Proximo=1, SinConfirmar=1]",
            $"{Itv}: 1 [Urgente=1]",
            $"{Plan}: 2 [Vencido=1, Vigente=1]",
        ]);
        resumen.Select(grupo => grupo.TipoDocumentoId).Should().BeEquivalentTo(_sesion.Tipos,
            "el identificador del grupo es el del tipo: es el filtro con el que el listado carga sus Documentos");
        resumen.Sum(grupo => grupo.Total).Should().Be(DocumentosVivosPorTenant);
    }

    /// <summary>
    /// (b) Lo que el alcance del usuario no deja listar tampoco cuenta: fuera de la cartera quedan Lisa (Trabajador),
    /// Pegaso (Cliente empresarial) y, por ser de Pegaso, su Proyecto.
    /// </summary>
    [Fact]
    public async Task No_cuenta_los_documentos_fuera_del_alcance_del_usuario()
    {
        var resumen = await ResumirAsync(new Filtros(), Cartera());

        Textos(resumen).Should().BeEquivalentTo(
        [
            $"{Formacion}: 22 [Vencido=1, Vigente=21]",
            $"{Seguro}: 2 [SinCaducidad=1, Vigente=1]",
            $"{Hacienda}: 2 [Proximo=1, SinConfirmar=1]",
            $"{Itv}: 1 [Urgente=1]",
            $"{Plan}: 1 [Vigente=1]",
        ],
            "contar el urgente de Lisa o los vencidos de Pegaso diría que existen a quien no puede verlos");
    }

    /// <summary>(b) Cada ámbito se acota por su propia lista: sin Empresas ni Vehículos visibles, sus tipos no forman grupo.</summary>
    [Fact]
    public async Task Un_ambito_sin_nada_visible_no_forma_grupo_y_con_el_alcance_vacio_no_hay_ninguno()
    {
        var sinEmpresasNiVehiculos = new AlcanceDatosServiceFalso(
            clienteIds: [_sesion.Orion], trabajadorIds: [_sesion.Homer], empresaIds: [], vehiculoIds: []);

        var resumen = await ResumirAsync(new Filtros(), sinEmpresasNiVehiculos);

        resumen.Select(grupo => grupo.TipoDocumentoNombre).Should().BeEquivalentTo([Formacion, Seguro, Plan]);

        var alcanceVacio = new AlcanceDatosServiceFalso(clienteIds: [], trabajadorIds: [], empresaIds: [], vehiculoIds: []);
        (await ResumirAsync(new Filtros(), alcanceVacio)).Should().BeEmpty();
    }

    /// <summary>(c) Bajo RLS, los Documentos del Tenant ajeno ni forman grupo ni engordan uno propio.</summary>
    [Fact]
    public async Task No_cuenta_los_documentos_de_otro_Tenant()
    {
        (await _propietario.Documentos.IgnoreQueryFilters().CountAsync(d => _ajeno.Tipos.Contains(d.TipoDocumentoId)))
            .Should().Be(DocumentosVivosPorTenant + 1, "control positivo: los Documentos del Tenant ajeno existen");

        var resumen = await ResumirAsync(new Filtros());

        resumen.Select(grupo => grupo.TipoDocumentoId).Should().NotIntersectWith(_ajeno.Tipos);
        resumen.Sum(grupo => grupo.Total).Should().Be(DocumentosVivosPorTenant, "los del Tenant de la sesión y ninguno más");
        resumen.Sum(grupo => grupo.PorEstado.Values.Sum()).Should().Be(DocumentosVivosPorTenant, "los recuentos por estado suman el total");
    }

    /// <summary>(d) La búsqueda acota el resumen como la lista: por tipo (sin acentos ni mayúsculas) y por propietario.</summary>
    [Fact]
    public async Task Respeta_la_busqueda()
    {
        Textos(await ResumirAsync(new Filtros(Busqueda: "formacion prl"))).Should().BeEquivalentTo(
            [$"{Formacion}: 23 [Urgente=1, Vencido=1, Vigente=21]"]);

        Textos(await ResumirAsync(new Filtros(Busqueda: "ORION"))).Should().BeEquivalentTo(
            [$"{Seguro}: 2 [SinCaducidad=1, Vigente=1]"],
            "solo casan los Documentos cuyo propietario es Orion: el vencido de Pegaso no cuenta en su grupo");
    }

    /// <summary>(d) El ámbito deja solo los tipos con Documentos de ese ámbito.</summary>
    [Fact]
    public async Task Respeta_el_filtro_de_ambito()
    {
        Textos(await ResumirAsync(new Filtros(Ambito: AmbitoAplicacion.Vehiculo))).Should().BeEquivalentTo(
            [$"{Itv}: 1 [Urgente=1]"]);

        Textos(await ResumirAsync(new Filtros(Ambito: AmbitoAplicacion.Proyecto))).Should().BeEquivalentTo(
            [$"{Plan}: 2 [Vencido=1, Vigente=1]"]);
    }

    /// <summary>
    /// (d) El resumen se calcula DESPUÉS del filtro de estado: describe lo que se lista. Un tipo sin ningún
    /// Documento en los estados pedidos no forma grupo, y los demás solo cuentan esos estados.
    /// </summary>
    [Fact]
    public async Task Respeta_el_filtro_de_estado()
    {
        Textos(await ResumirAsync(new Filtros(Estados: [EstadoDocumento.Vencido]))).Should().BeEquivalentTo(
        [
            $"{Formacion}: 1 [Vencido=1]",
            $"{Seguro}: 1 [Vencido=1]",
            $"{Plan}: 1 [Vencido=1]",
        ]);

        // Estado y Estados se suman, como en el listado.
        Textos(await ResumirAsync(new Filtros(Estado: EstadoDocumento.Vigente, Estados: [EstadoDocumento.SinCaducidad])))
            .Should().BeEquivalentTo(
            [
                $"{Formacion}: 21 [Vigente=21]",
                $"{Seguro}: 2 [SinCaducidad=1, Vigente=1]",
                $"{Plan}: 1 [Vigente=1]",
            ]);

        (await ResumirAsync(new Filtros(Estados: [EstadoDocumento.EnTolerancia]))).Should().BeEmpty(
            "un estado que un Documento nunca tiene no devuelve nada, igual que en el listado");
    }

    /// <summary>(d) Con el filtro de tipo queda un solo grupo.</summary>
    [Fact]
    public async Task Respeta_el_filtro_de_tipo()
    {
        Textos(await ResumirAsync(new Filtros(TipoDocumentoId: _sesion.TipoSeguro))).Should().BeEquivalentTo(
            [$"{Seguro}: 3 [SinCaducidad=1, Vencido=1, Vigente=1]"]);
    }

    /// <summary>(d) Con el filtro de plataforma solo cuentan los Documentos acreditados en ella.</summary>
    [Fact]
    public async Task Respeta_el_filtro_de_plataforma()
    {
        Textos(await ResumirAsync(new Filtros(ProveedorPlataformaCaeId: _sesion.Plataforma))).Should().BeEquivalentTo(
        [
            $"{Formacion}: 1 [Vencido=1]",
            $"{Itv}: 1 [Urgente=1]",
        ]);
    }

    /// <summary>(d) Los filtros que no salen de la barra del listado, pero que la consulta también lleva.</summary>
    [Fact]
    public async Task Respeta_los_filtros_de_trabajador_propietario_ventana_de_vencimiento_y_documento()
    {
        Textos(await ResumirAsync(new Filtros(TrabajadorId: _sesion.Homer))).Should().BeEquivalentTo(
            [$"{Formacion}: 22 [Vencido=1, Vigente=21]"]);

        Textos(await ResumirAsync(new Filtros(PropietarioId: _sesion.Orion))).Should().BeEquivalentTo(
            [$"{Seguro}: 2 [SinCaducidad=1, Vigente=1]"]);

        Textos(await ResumirAsync(new Filtros(FechaVencimientoHasta: _hoy.AddDays(UmbralRojoDias + 5)))).Should().BeEquivalentTo(
        [
            $"{Formacion}: 1 [Urgente=1]",
            $"{Itv}: 1 [Urgente=1]",
        ]);

        Textos(await ResumirAsync(new Filtros(DocumentoId: _sesion.DocumentoVencidoDeHomer))).Should().BeEquivalentTo(
            [$"{Formacion}: 1 [Vencido=1]"]);
    }

    /// <summary>
    /// (e) Coherencia con el listado, que es la propiedad que la pantalla necesita: con los mismos filtros y el
    /// mismo alcance, la suma de los grupos es el total del listado; cada grupo cuenta lo que el listado lista con
    /// ese tipo; y cada cifra de estado es la del listado con ese tipo y ese estado. También al revés: un tipo
    /// del que el listado no saca nada no forma grupo.
    /// </summary>
    [Fact]
    public async Task Cada_grupo_cuenta_exactamente_lo_que_el_listado_lista_al_abrirlo()
    {
        var casos = new (string Nombre, Filtros Filtros, IAlcanceDatosService? Alcance)[]
        {
            ("sin filtros", new Filtros(), null),
            ("sin filtros, con cartera", new Filtros(), Cartera()),
            ("búsqueda por tipo", new Filtros(Busqueda: "formacion"), null),
            ("búsqueda por propietario", new Filtros(Busqueda: "orion"), null),
            ("ámbito Trabajador", new Filtros(Ambito: AmbitoAplicacion.Trabajador), null),
            ("ámbito Cliente", new Filtros(Ambito: AmbitoAplicacion.Cliente), null),
            ("ámbito Empresa", new Filtros(Ambito: AmbitoAplicacion.Empresa), null),
            ("ámbito Vehículo", new Filtros(Ambito: AmbitoAplicacion.Vehiculo), null),
            ("ámbito Proyecto", new Filtros(Ambito: AmbitoAplicacion.Proyecto), null),
            ("estados: vencido", new Filtros(Estados: [EstadoDocumento.Vencido]), null),
            ("estado y estados", new Filtros(Estado: EstadoDocumento.Vigente, Estados: [EstadoDocumento.SinCaducidad, EstadoDocumento.Urgente]), null),
            ("estado que nadie tiene", new Filtros(Estados: [EstadoDocumento.EnTolerancia]), null),
            ("tipo", new Filtros(TipoDocumentoId: _sesion.TipoSeguro), null),
            ("plataforma", new Filtros(ProveedorPlataformaCaeId: _sesion.Plataforma), null),
            ("trabajador", new Filtros(TrabajadorId: _sesion.Homer), null),
            ("propietario", new Filtros(PropietarioId: _sesion.Orion), null),
            ("ventana de vencimiento", new Filtros(FechaVencimientoHasta: _hoy.AddDays(UmbralRojoDias + 5)), null),
            ("documento", new Filtros(DocumentoId: _sesion.DocumentoVencidoDeHomer), null),
            ("ámbito Cliente y estado, con cartera", new Filtros(Ambito: AmbitoAplicacion.Cliente, Estados: [EstadoDocumento.Vigente, EstadoDocumento.Vencido]), Cartera()),
            ("búsqueda y plataforma", new Filtros(Busqueda: "simpson", ProveedorPlataformaCaeId: _sesion.Plataforma), null),
        };

        var gruposVistos = 0;
        foreach (var (nombre, filtros, alcance) in casos)
        {
            var resumen = await ResumirAsync(filtros, alcance);
            gruposVistos += resumen.Count;

            (await TotalDelListadoAsync(filtros, alcance)).Should().Be(resumen.Sum(grupo => grupo.Total),
                $"[{nombre}] la suma de los grupos es el total del listado con los mismos filtros");

            var pedidos = filtros.EstadosPedidos();
            foreach (var tipo in _sesion.Tipos)
            {
                var grupo = resumen.SingleOrDefault(g => g.TipoDocumentoId == tipo);
                if (filtros.TipoDocumentoId is { } tipoFiltrado && tipoFiltrado != tipo)
                {
                    grupo.Should().BeNull($"[{nombre}] el filtro de tipo deja fuera a los demás tipos");
                    continue;
                }

                var delListado = await TotalDelListadoAsync(filtros with { TipoDocumentoId = tipo }, alcance);
                (grupo?.Total ?? 0).Should().Be(delListado,
                    $"[{nombre}] el grupo cuenta lo que el listado lista con ese tipo");
                if (delListado == 0)
                    grupo.Should().BeNull($"[{nombre}] un tipo del que el listado no saca nada no forma grupo");

                foreach (var estado in Enum.GetValues<EstadoDocumento>())
                {
                    // El listado suma Estado y Estados: para medir UN estado dentro del filtro vigente se pide solo
                    // ese, y un estado que el filtro vigente excluye no puede aparecer en el grupo.
                    var esperado = pedidos.Count == 0 || pedidos.Contains(estado)
                        ? await TotalDelListadoAsync(filtros with { TipoDocumentoId = tipo, Estado = null, Estados = [estado] }, alcance)
                        : 0;
                    (grupo?.PorEstado.GetValueOrDefault(estado) ?? 0).Should().Be(esperado,
                        $"[{nombre}] {estado}: la cifra del grupo es la del listado con ese tipo y ese estado");
                }
            }
        }

        gruposVistos.Should().BeGreaterThan(casos.Length, "control: los casos no comparan vacío con vacío");
    }

    /// <summary>
    /// (f) Primero el tipo cuyo peor Documento es más grave (la escala del orden por severidad del listado) y, a
    /// igual gravedad, por nombre. El orden alfabético sería Certificado, Formación, ITV, Plan, Seguro.
    /// </summary>
    [Fact]
    public async Task Ordena_primero_los_tipos_con_peor_severidad_y_luego_por_nombre()
    {
        (await ResumirAsync(new Filtros())).Select(grupo => grupo.TipoDocumentoNombre).Should().Equal(
            [Formacion, Plan, Seguro, Itv, Hacienda],
            "tres tipos tienen un vencido (por nombre entre sí), luego el que tiene un urgente y al final el próximo");

        // La gravedad se mide sobre lo que se lista: con la cartera, Plan y Seguro pierden su vencido.
        (await ResumirAsync(new Filtros(), Cartera())).Select(grupo => grupo.TipoDocumentoNombre).Should().Equal(
            Formacion, Itv, Hacienda, Plan, Seguro);

        (await ResumirAsync(new Filtros(Estados: [EstadoDocumento.Vigente]))).Select(grupo => grupo.TipoDocumentoNombre).Should().Equal(
            [Formacion, Plan, Seguro], "con la misma gravedad en todos, solo queda el nombre");
    }

    /// <summary>(g) Un Documento borrado no cuenta: la ITV vencida está en la papelera y su grupo solo tiene la urgente.</summary>
    [Fact]
    public async Task Un_documento_borrado_no_cuenta()
    {
        (await _propietario.Documentos.IgnoreQueryFilters()
                .CountAsync(d => d.TipoDocumentoId == _sesion.TipoItv && d.EstaEliminado))
            .Should().Be(1, "control positivo: la ITV borrada existe");

        var resumen = await ResumirAsync(new Filtros(TipoDocumentoId: _sesion.TipoItv));

        Textos(resumen).Should().BeEquivalentTo([$"{Itv}: 1 [Urgente=1]"]);
        (await ResumirAsync(new Filtros(TipoDocumentoId: _sesion.TipoItv, Estados: [EstadoDocumento.Vencido]))).Should().BeEmpty();
    }

    /// <summary>
    /// El resumen se cuenta en SQL: una sola orden lee Documentos, lleva un <c>GROUP BY</c> y devuelve una fila por
    /// par (tipo, estado), no una por Documento. Control del instrumento: la misma medida sobre el listado, que sí
    /// trae Documentos, ve sus treinta y una filas.
    /// </summary>
    [Fact]
    public async Task El_resumen_se_cuenta_en_SQL_sin_materializar_documentos()
    {
        _lecturas.Reiniciar();
        var resumen = await ResumirAsync(new Filtros());

        resumen.Sum(grupo => grupo.Total).Should().Be(DocumentosVivosPorTenant);
        var lectura = _lecturas.DeDocumentos().Should().ContainSingle("el resumen es una sola consulta sobre Documentos").Subject;
        lectura.Sql.Should().Contain("GROUP BY").And.Contain("count(", "agrupa y cuenta en la base");
        // RelationalDataReader cuenta también la lectura que devuelve «no hay más filas».
        lectura.Filas.Should().BeInRange(ParesTipoEstadoPorTenant, ParesTipoEstadoPorTenant + 1,
            "vuelve una fila por par (tipo, estado): traer los treinta y un Documentos para contarlos en memoria leería más");

        _lecturas.Reiniciar();
        var listado = await ListarAsync(new Filtros().Listado() with { TamanoPagina = 50 });

        listado.Elementos.Should().HaveCount(DocumentosVivosPorTenant);
        _lecturas.DeDocumentos().Max(l => l.Filas).Should().BeGreaterThanOrEqualTo(DocumentosVivosPorTenant,
            "control positivo: el instrumento ve las filas de una consulta que sí materializa Documentos");
    }

    private AlcanceDatosServiceFalso Cartera() => new(
        clienteIds: [_sesion.Orion], trabajadorIds: [_sesion.Homer], empresaIds: [_sesion.Ebro], vehiculoIds: [_sesion.Camion]);

    private static IEnumerable<string> Textos(IEnumerable<ResumenDocumentosDeTipoDto> resumen) =>
        resumen.Select(grupo =>
            $"{grupo.TipoDocumentoNombre}: {grupo.Total} [" +
            string.Join(", ", grupo.PorEstado.OrderBy(par => par.Key.ToString(), StringComparer.Ordinal).Select(par => $"{par.Key}={par.Value}")) +
            "]");

    private Task<IReadOnlyList<ResumenDocumentosDeTipoDto>> ResumirAsync(Filtros filtros, IAlcanceDatosService? alcance = null)
    {
        var handler = new ObtenerResumenDocumentosPorTipoQueryHandler(
            _runtime, _runtime, _runtime, _runtime, _runtime, _runtime, _runtime,
            alcance ?? new AlcanceDatosServiceFalso(), _runtime);

        return handler.Handle(filtros.Resumen(), CancellationToken.None);
    }

    private Task<ResultadoPaginado<DocumentoListaDto>> ListarAsync(ObtenerDocumentosQuery consulta, IAlcanceDatosService? alcance = null)
    {
        var handler = new ObtenerDocumentosQueryHandler(
            _runtime, _runtime, _runtime, _runtime, _runtime, _runtime, _runtime,
            alcance ?? new AlcanceDatosServiceFalso(), _runtime, _runtime);

        return handler.Handle(consulta, CancellationToken.None);
    }

    private async Task<int> TotalDelListadoAsync(Filtros filtros, IAlcanceDatosService? alcance) =>
        (await ListarAsync(filtros.Listado(), alcance)).TotalElementos;

    private async Task<Sembrado> SembrarAsync(Guid tenantId)
    {
        using var ambito = AmbitoTenantExplicito.Establecer(tenantId);

        var orion = Empresa.CrearComoCliente("Astilleros Orion", "B12345674", false, null, null);
        var pegaso = Empresa.CrearComoCliente("Bodegas Pegaso", "B10000032", false, null, null);
        var ebro = new Empresa("Montajes Ebro S.L.", "B87654323");
        _propietario.Empresas.AddRange(orion, pegaso, ebro);
        _propietario.ParametrosSistema.Add(new ParametroSistema(UmbralAmbarDias, UmbralRojoDias));

        var formacion = new TipoDocumento(Formacion, null, aplicaVencimientoAutomatico: false, 1, AmbitoAplicacion.Trabajador, requerido: RequisitoDocumental.Si);
        var seguro = new TipoDocumento(Seguro, 12, aplicaVencimientoAutomatico: true, 2, AmbitoAplicacion.Cliente, requerido: RequisitoDocumental.Si);
        var hacienda = new TipoDocumento(Hacienda, 6, aplicaVencimientoAutomatico: true, 3, AmbitoAplicacion.Empresa, requerido: RequisitoDocumental.Si);
        var itv = new TipoDocumento(Itv, 12, aplicaVencimientoAutomatico: true, 4, AmbitoAplicacion.Vehiculo, requerido: RequisitoDocumental.Si);
        var plan = new TipoDocumento(Plan, null, aplicaVencimientoAutomatico: false, 5, AmbitoAplicacion.Proyecto, requerido: RequisitoDocumental.Si);
        _propietario.TiposDocumento.AddRange(formacion, seguro, hacienda, itv, plan);

        var plataforma = new ProveedorPlataformaCae($"PLAT-{Guid.NewGuid():N}"[..14], "Plataforma del resumen");
        _propietario.ProveedoresPlataformaCae.Add(plataforma);
        await _propietario.SaveChangesAsync();

        var centroDeOrion = new Centro(orion.Id, ebro.Id, "Dársena norte");
        var centroDePegaso = new Centro(pegaso.Id, ebro.Id, "Nave de barricas");
        var homer = Trabajador.DeEmpresa(ebro.Id, "Homer", "Simpson", "77189989B");
        var lisa = Trabajador.DeEmpresa(ebro.Id, "Lisa", "Bouvier", "12345678Z");
        var camion = Vehiculo.DeEmpresa(ebro.Id, "Furgón taller", "Citroën Jumper", "1234BCD");
        _propietario.Centros.AddRange(centroDeOrion, centroDePegaso);
        _propietario.Trabajadores.AddRange(homer, lisa);
        _propietario.Vehiculos.Add(camion);
        await _propietario.SaveChangesAsync();

        var proyectoDeOrion = Proyecto.Crear(orion.Id, centroDeOrion.Id, "Ampliación de dársena", _hoy, null, null);
        var proyectoDePegaso = Proyecto.Crear(pegaso.Id, centroDePegaso.Id, "Reforma de bodega", _hoy, null, null);
        _propietario.Proyectos.AddRange(proyectoDeOrion, proyectoDePegaso);
        var canal = CanalGestionDocumental.DePlataforma(centroDeOrion.Id, "Acceso", plataforma.Id, null, null, null);
        _propietario.CanalesGestionDocumental.Add(canal);
        await _propietario.SaveChangesAsync();

        // Las fechas dejan margen dentro de cada franja: el estado no cambia si el test cruza la medianoche.
        var emision = _hoy.AddDays(-100);
        var vencido = VigenciaDocumento.VenceEl(_hoy.AddDays(-10));
        var urgente = VigenciaDocumento.VenceEl(_hoy.AddDays(UmbralRojoDias - 3));
        var proximo = VigenciaDocumento.VenceEl(_hoy.AddDays(UmbralAmbarDias - 3));
        var vigente = VigenciaDocumento.VenceEl(_hoy.AddDays(UmbralAmbarDias + 60));

        var vencidoDeHomer = Documento.DeTrabajador(homer.Id, formacion.Id, emision, vencido);
        var urgenteDelCamion = Documento.DeVehiculo(camion.Id, itv.Id, emision, urgente);
        var vencidoBorradoDelCamion = Documento.DeVehiculo(camion.Id, itv.Id, emision.AddDays(-30), vencido);
        vencidoBorradoDelCamion.MarcarComoEliminado(Guid.NewGuid());

        _propietario.Documentos.AddRange(
            vencidoDeHomer,
            Documento.DeTrabajador(lisa.Id, formacion.Id, emision, urgente),
            Documento.DeCliente(orion.Id, seguro.Id, emision, vigente),
            Documento.DeCliente(orion.Id, seguro.Id, emision, VigenciaDocumento.NoCaduca),
            Documento.DeCliente(pegaso.Id, seguro.Id, emision, vencido),
            Documento.DeEmpresa(ebro.Id, hacienda.Id, emision, proximo),
            Documento.DeEmpresa(ebro.Id, hacienda.Id, emision, VigenciaDocumento.SinConfirmar),
            urgenteDelCamion,
            vencidoBorradoDelCamion,
            Documento.DeProyecto(proyectoDeOrion.Id, plan.Id, emision, vigente),
            Documento.DeProyecto(proyectoDePegaso.Id, plan.Id, emision, vencido));
        _propietario.Documentos.AddRange(Enumerable.Range(0, VigentesDeHomer)
            .Select(i => Documento.DeTrabajador(homer.Id, formacion.Id, emision.AddDays(i), vigente)));
        await _propietario.SaveChangesAsync();

        _propietario.AcreditacionesDocumentoPlataforma.AddRange(
            new AcreditacionDocumentoPlataforma(vencidoDeHomer.Id, canal.Id),
            new AcreditacionDocumentoPlataforma(urgenteDelCamion.Id, canal.Id));
        await _propietario.SaveChangesAsync();

        return new Sembrado(
            orion.Id, pegaso.Id, ebro.Id, homer.Id, lisa.Id, camion.Id,
            formacion.Id, seguro.Id, hacienda.Id, itv.Id, plan.Id, plataforma.Id, vencidoDeHomer.Id);
    }

    /// <summary>
    /// Cada lectura que EF hace por la conexión de runtime: su SQL y cuántas filas llegó a leer. Es lo que
    /// distingue «la base contó» de «se trajeron los Documentos y se contaron en memoria».
    /// </summary>
    private sealed class RegistroDeLecturas : DbCommandInterceptor
    {
        private readonly List<(string Sql, int Filas)> _lecturas = [];

        public void Reiniciar()
        {
            lock (_lecturas) _lecturas.Clear();
        }

        /// <summary>Las lecturas cuyo SQL nombra la tabla de Documentos.</summary>
        public IReadOnlyList<(string Sql, int Filas)> DeDocumentos()
        {
            lock (_lecturas)
                return _lecturas.Where(lectura => lectura.Sql.Contains("\"Documentos\"", StringComparison.Ordinal)).ToList();
        }

        public override InterceptionResult DataReaderDisposing(DbCommand command, DataReaderDisposingEventData eventData, InterceptionResult result)
        {
            lock (_lecturas) _lecturas.Add((command.CommandText, eventData.ReadCount));
            return base.DataReaderDisposing(command, eventData, result);
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
}
