using CaeManager.Application.Alertas.Queries.ObtenerAlertas;
using CaeManager.Application.Centros;
using CaeManager.Application.Centros.Queries.ObtenerCentros;
using CaeManager.Application.Clientes.Queries.ObtenerClientes;
using CaeManager.Application.Common;
using CaeManager.Application.Documentos;
using CaeManager.Application.Documentos.Queries.ObtenerDocumentos;
using CaeManager.Application.Empresas.Queries.ObtenerEmpresas;
using CaeManager.Application.Gestiones.Queries.ObtenerGestiones;
using CaeManager.Application.Subcontratas;
using CaeManager.Application.Subcontratas.Queries.ObtenerSubcontratas;
using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadores;
using CaeManager.Application.Vehiculos.Queries.ObtenerVehiculos;
using CaeManager.Application.Visitas.Queries.ObtenerVisitas;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Common;
using CaeManager.Domain.Configuracion;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Gestiones;
using CaeManager.Domain.Tenants;
using CaeManager.Domain.Trabajadores;
using CaeManager.Domain.Vehiculos;
using CaeManager.Domain.Visitas;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Interceptors;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests.Listados;

/// <summary>
/// El buscador de los nueve listados que filtran en SQL ignora acentos y mayúsculas, y sigue
/// sin enseñar nada que el usuario no viera ya sin buscar.
///
/// Se lee <b>como <c>cae_app_runtime</c></b>, con los interceptores de sellado y de sesión RLS de
/// producción (mismo arnés que <c>CumplimientoCentroDocumentosDuplicadosBajoRlsTests</c>); la
/// siembra va como propietario de la base, porque no es lo que se mide.
///
/// Hay tres copias de los mismos datos, con los mismos nombres acentuados: una dentro de la
/// Asignación de Cartera del usuario, otra en su mismo Tenant propietario pero fuera de ella, y
/// otra en un Tenant propietario ajeno, que repite además el DNI, los CIF, la matrícula y el
/// código de Centro de la primera. Dos familias de casos:
/// <list type="bullet">
/// <item><c>…_no_sale_de_la_cartera</c>: sin restricción de cartera vuelven las dos del Tenant de
/// la sesión (control positivo: el término casa con la de fuera de la cartera, y RLS oculta la
/// ajena), y con la cartera puesta vuelve solo la de dentro.</item>
/// <item><c>…_por_cada_columna</c>: un término por cada brazo del predicado de búsqueda, sin
/// acento y en otra caja, que solo aparece en esa columna. Sin él, un brazo que siguiera
/// comparando solo mayúsculas pasaría inadvertido detrás de otro que sí casa.</item>
/// </list>
/// </summary>
public class BusquedaSinAcentosDeListadosBajoRlsTests(BusquedaSinAcentosDeListadosBajoRlsTests.Escenario escenario)
    : IClassFixture<BusquedaSinAcentosDeListadosBajoRlsTests.Escenario>
{
    /// <summary>Control del instrumento: la lectura va de verdad bajo RLS y la fila ajena casa con el término.</summary>
    [Fact]
    public async Task La_lectura_va_bajo_RLS_y_el_termino_casa_tambien_con_la_fila_del_Tenant_ajeno()
    {
        const string termino = "almacen logrono";
        await using var runtime = escenario.ContextoRuntime();

        var segunElPropietario = await escenario.Propietario.Centros.IgnoreQueryFilters()
            .CountAsync(c => TextoDeBusqueda.Contiene(c.Nombre, termino));
        var segunElRuntime = await runtime.Centros.IgnoreQueryFilters()
            .CountAsync(c => TextoDeBusqueda.Contiene(c.Nombre, termino));

        segunElPropietario.Should().Be(3, "el término casa con los tres Centros sembrados, el del Tenant ajeno incluido");
        segunElRuntime.Should().Be(2, "sin el filtro global de EF, solo RLS puede ocultar el Centro del Tenant ajeno");
    }

    // ------------------------------------------------------------ Buscar solo estrecha

    [Fact]
    public async Task Trabajadores_encuentra_sin_acentos_y_no_sale_de_la_cartera() =>
        DebeEstrecharSinSalirDeLaCartera(
            await BuscarAsync((contexto, alcance) => BuscarTrabajadoresAsync(contexto, alcance, "NUNEZ IBANEZ")),
            b => b.TrabajadorId);

    [Fact]
    public async Task Empresas_encuentra_sin_acentos_y_no_sale_de_la_cartera() =>
        DebeEstrecharSinSalirDeLaCartera(
            await BuscarAsync((contexto, alcance) => BuscarEmpresasAsync(contexto, alcance, "montajes aragon")),
            b => b.EmpresaId);

    [Fact]
    public async Task Vehiculos_encuentra_sin_acentos_y_no_sale_de_la_cartera() =>
        DebeEstrecharSinSalirDeLaCartera(
            await BuscarAsync((contexto, alcance) => BuscarVehiculosAsync(contexto, alcance, "camion grua")),
            b => b.VehiculoId);

    [Fact]
    public async Task Subcontratas_encuentra_sin_acentos_y_no_sale_de_la_cartera() =>
        DebeEstrecharSinSalirDeLaCartera(
            await BuscarAsync((contexto, alcance) => BuscarSubcontratasAsync(contexto, alcance, "INSTALACIONES CACERES")),
            b => b.SubcontrataId);

    [Fact]
    public async Task Gestiones_encuentra_sin_acentos_y_no_sale_de_la_cartera() =>
        DebeEstrecharSinSalirDeLaCartera(
            await BuscarAsync((contexto, alcance) => BuscarGestionesAsync(contexto, alcance, "jose maria nunez")),
            b => b.GestionId);

    [Fact]
    public async Task Visitas_encuentra_sin_acentos_y_no_sale_de_la_cartera() =>
        DebeEstrecharSinSalirDeLaCartera(
            await BuscarAsync((contexto, alcance) => BuscarVisitasAsync(contexto, alcance, "almacen logrono")),
            b => b.VisitaId);

    [Fact]
    public async Task Documentos_encuentra_sin_acentos_y_no_sale_de_la_cartera() =>
        DebeEstrecharSinSalirDeLaCartera(
            await BuscarAsync((contexto, alcance) => BuscarDocumentosAsync(contexto, alcance, "FORMACION ESPECIFICA")),
            b => b.DocumentoId);

    [Fact]
    public async Task Clientes_encuentra_sin_acentos_y_no_sale_de_la_cartera() =>
        DebeEstrecharSinSalirDeLaCartera(
            await BuscarAsync((contexto, alcance) => BuscarClientesAsync(contexto, alcance, "canon iberica")),
            b => b.ClienteEmpresarialId);

    [Fact]
    public async Task Centros_encuentra_sin_acentos_y_no_sale_de_la_cartera() =>
        DebeEstrecharSinSalirDeLaCartera(
            await BuscarAsync((contexto, alcance) => BuscarCentrosAsync(contexto, alcance, "ALMACEN LOGRONO")),
            b => b.CentroId);

    // ------------------------------------------- Cada brazo del predicado, por separado

    [Theory]
    [InlineData("Nombre", "JOSE MARIA")]
    [InlineData("Apellidos", "nunez ibanez")]
    [InlineData("Dni", "12345678z")]
    [InlineData("Alias", "PEPON")]
    public async Task Trabajadores_busca_sin_acentos_por_cada_columna(string columna, string termino) =>
        DebeEncontrarLaDeLaCartera(await BuscarSinRestriccionAsync(BuscarTrabajadoresAsync, termino), b => b.TrabajadorId, columna);

    [Theory]
    [InlineData("RazonSocial", "MONTAJES ARAGON")]
    [InlineData("Cif", "b10380186")]
    public async Task Empresas_busca_sin_acentos_por_cada_columna(string columna, string termino) =>
        DebeEncontrarLaDeLaCartera(await BuscarSinRestriccionAsync(BuscarEmpresasAsync, termino), b => b.EmpresaId, columna);

    [Theory]
    [InlineData("Nombre", "CAMION GRUA")]
    [InlineData("Modelo", "citroen jumper")]
    [InlineData("NumeroPlaca", "1234bcd")]
    public async Task Vehiculos_busca_sin_acentos_por_cada_columna(string columna, string termino) =>
        DebeEncontrarLaDeLaCartera(await BuscarSinRestriccionAsync(BuscarVehiculosAsync, termino), b => b.VehiculoId, columna);

    [Theory]
    [InlineData("RazonSocial", "instalaciones caceres")]
    [InlineData("Cif", "b10380236")]
    public async Task Subcontratas_busca_sin_acentos_por_cada_columna(string columna, string termino) =>
        DebeEncontrarLaDeLaCartera(await BuscarSinRestriccionAsync(BuscarSubcontratasAsync, termino), b => b.SubcontrataId, columna);

    /// <summary>En Gestiones el nombre buscado es «Nombre Apellidos», concatenado en la consulta.</summary>
    [Theory]
    [InlineData("Trabajador (Nombre y Apellidos)", "MARIA NUNEZ")]
    [InlineData("Centro.Nombre", "almacen logrono")]
    [InlineData("TipoDocumento.Nombre", "FORMACION ESPECIFICA")]
    public async Task Gestiones_busca_sin_acentos_por_cada_columna(string columna, string termino) =>
        DebeEncontrarLaDeLaCartera(await BuscarSinRestriccionAsync(BuscarGestionesAsync, termino), b => b.GestionId, columna);

    [Theory]
    [InlineData("Centro.Nombre", "ALMACEN LOGRONO")]
    [InlineData("Cliente empresarial (RazonSocial)", "canon iberica")]
    [InlineData("Empresa.RazonSocial", "MONTAJES ARAGON")]
    public async Task Visitas_busca_sin_acentos_por_cada_columna(string columna, string termino) =>
        DebeEncontrarLaDeLaCartera(await BuscarSinRestriccionAsync(BuscarVisitasAsync, termino), b => b.VisitaId, columna);

    [Theory]
    [InlineData("PropietarioNombre", "maria nunez")]
    [InlineData("TipoDocumentoNombre", "formacion especifica")]
    public async Task Documentos_busca_sin_acentos_por_cada_columna(string columna, string termino) =>
        DebeEncontrarLaDeLaCartera(await BuscarSinRestriccionAsync(BuscarDocumentosAsync, termino), b => b.DocumentoId, columna);

    [Theory]
    [InlineData("RazonSocial", "CANON IBERICA")]
    public async Task Clientes_busca_sin_acentos_por_cada_columna(string columna, string termino) =>
        DebeEncontrarLaDeLaCartera(await BuscarSinRestriccionAsync(BuscarClientesAsync, termino), b => b.ClienteEmpresarialId, columna);

    [Theory]
    [InlineData("Nombre", "almacen logrono")]
    [InlineData("CodigoCentro", "AREA-UNO")]
    [InlineData("Cliente empresarial (RazonSocial)", "CANON IBERICA")]
    [InlineData("Empresa.RazonSocial", "montajes aragon")]
    public async Task Centros_busca_sin_acentos_por_cada_columna(string columna, string termino) =>
        DebeEncontrarLaDeLaCartera(await BuscarSinRestriccionAsync(BuscarCentrosAsync, termino), b => b.CentroId, columna);

    // ------------------------------------------------------- El término, en PostgreSQL

    /// <summary>
    /// Acentos y caja no cuentan en ninguno de los dos lados. «weiß» y «weiss» fijan que el
    /// término se normaliza en PostgreSQL igual que la columna: la normalización de C# deja «ß»
    /// como está, y con ella «weiß» no encontraría la razón social que lo contiene.
    /// </summary>
    [Theory]
    [InlineData("aragon")]
    [InlineData("ARAGÓN")]
    [InlineData("aragón weiß")]
    [InlineData("weiß")]
    [InlineData("WEISS")]
    public async Task El_termino_se_normaliza_igual_que_la_columna(string termino)
    {
        await using var contexto = escenario.ContextoRuntime();

        var ids = await BuscarEmpresasAsync(contexto, new AlcanceDatosServiceFalso(), termino);

        ids.Should().BeEquivalentTo([escenario.EnCartera.EmpresaId, escenario.FueraDeCartera.EmpresaId]);
    }

    /// <summary>
    /// El término es literal: «%» y «_» no son comodines. Ninguna razón social sembrada los
    /// contiene, así que no vuelve nada; si se colaran como comodín volverían todas o varias.
    /// </summary>
    [Theory]
    [InlineData("%")]
    [InlineData("_")]
    [InlineData("arag_n")]
    [InlineData("montajes%weiß")]
    [InlineData("\\")]
    public async Task Los_comodines_de_LIKE_se_buscan_como_texto(string termino)
    {
        await using var contexto = escenario.ContextoRuntime();

        var conComodin = await BuscarEmpresasAsync(contexto, new AlcanceDatosServiceFalso(), termino);
        var control = await BuscarEmpresasAsync(contexto, new AlcanceDatosServiceFalso(), "montajes");

        conComodin.Should().BeEmpty();
        control.Should().HaveCount(2, "control positivo: el buscador encuentra las dos Empresas del Tenant de la sesión");
    }

    // --------------------------------------------------------------------- Listados

    private static async Task<IEnumerable<Guid>> BuscarTrabajadoresAsync(
        CaeManagerDbContext contexto, IAlcanceDatosService alcance, string termino)
    {
        var handler = new ObtenerTrabajadoresQueryHandler(
            contexto, contexto, contexto, contexto, alcance, new CalculoEstadoDocumentalService(contexto, contexto));
        var resultado = await handler.Handle(new ObtenerTrabajadoresQuery(termino), CancellationToken.None);
        return resultado.Elementos.Select(t => t.Id).ToList();
    }

    private static async Task<IEnumerable<Guid>> BuscarEmpresasAsync(
        CaeManagerDbContext contexto, IAlcanceDatosService alcance, string termino)
    {
        var handler = new ObtenerEmpresasQueryHandler(
            contexto, alcance, new CalculoEstadoDocumentalService(contexto, contexto),
            contexto, contexto, contexto, contexto,
            new CalculoEstadoCentroService(contexto, contexto, contexto, contexto, contexto, contexto));
        var resultado = await handler.Handle(new ObtenerEmpresasQuery(termino), CancellationToken.None);
        return resultado.Elementos.Select(e => e.Id).ToList();
    }

    private static async Task<IEnumerable<Guid>> BuscarVehiculosAsync(
        CaeManagerDbContext contexto, IAlcanceDatosService alcance, string termino)
    {
        var handler = new ObtenerVehiculosQueryHandler(
            contexto, contexto, alcance, contexto, contexto, new CalculoEstadoDocumentalService(contexto, contexto));
        var resultado = await handler.Handle(new ObtenerVehiculosQuery(termino), CancellationToken.None);
        return resultado.Elementos.Select(v => v.Id).ToList();
    }

    private static async Task<IEnumerable<Guid>> BuscarSubcontratasAsync(
        CaeManagerDbContext contexto, IAlcanceDatosService alcance, string termino)
    {
        var servicio = new CalculoEstadoSubcontrataService(contexto, contexto, contexto, contexto, contexto, contexto, alcance);
        var handler = new ObtenerSubcontratasQueryHandler(contexto, alcance, servicio);
        var resultado = await handler.Handle(new ObtenerSubcontratasQuery(termino), CancellationToken.None);
        return resultado.Elementos.Select(s => s.Id).ToList();
    }

    private static async Task<IEnumerable<Guid>> BuscarGestionesAsync(
        CaeManagerDbContext contexto, IAlcanceDatosService alcance, string termino)
    {
        var handler = new ObtenerGestionesQueryHandler(contexto, contexto, contexto, contexto, alcance);
        var resultado = await handler.Handle(
            new ObtenerGestionesQuery(termino, Estado: null, TrabajadorId: null), CancellationToken.None);
        return resultado.Elementos.Select(g => g.Id).ToList();
    }

    private static async Task<IEnumerable<Guid>> BuscarVisitasAsync(
        CaeManagerDbContext contexto, IAlcanceDatosService alcance, string termino)
    {
        var handler = new ObtenerVisitasQueryHandler(
            contexto, contexto, contexto, contexto, contexto, contexto, alcance, contexto);
        var resultado = await handler.Handle(
            new ObtenerVisitasQuery(termino, SoloActivas: false, NotificadoCliente: null), CancellationToken.None);
        return resultado.Elementos.Select(v => v.Id).ToList();
    }

    private static async Task<IEnumerable<Guid>> BuscarDocumentosAsync(
        CaeManagerDbContext contexto, IAlcanceDatosService alcance, string termino)
    {
        var handler = new ObtenerDocumentosQueryHandler(
            contexto, contexto, contexto, contexto, contexto, contexto, contexto, alcance, contexto, contexto);
        var resultado = await handler.Handle(
            new ObtenerDocumentosQuery(TrabajadorId: null, Ambito: null, Busqueda: termino), CancellationToken.None);
        return resultado.Elementos.Select(d => d.Id).ToList();
    }

    private static async Task<IEnumerable<Guid>> BuscarClientesAsync(
        CaeManagerDbContext contexto, IAlcanceDatosService alcance, string termino)
    {
        var handler = new ObtenerClientesQueryHandler(contexto, contexto, contexto, new MediadorSinAlertas(), alcance);
        var resultado = await handler.Handle(new ObtenerClientesQuery(termino, SoloCriticos: null), CancellationToken.None);
        return resultado.Elementos.Select(c => c.Id).ToList();
    }

    private static async Task<IEnumerable<Guid>> BuscarCentrosAsync(
        CaeManagerDbContext contexto, IAlcanceDatosService alcance, string termino)
    {
        var handler = new ObtenerCentrosQueryHandler(
            contexto, contexto, alcance, new CalculoEstadoCentroService(contexto, contexto, contexto, contexto, contexto, contexto));
        var resultado = await handler.Handle(new ObtenerCentrosQuery(termino, ClienteId: null), CancellationToken.None);
        return resultado.Elementos.Select(c => c.Id).ToList();
    }

    // ------------------------------------------------------------------ Afirmaciones

    private sealed record Resultados(IReadOnlyList<Guid> SinRestriccion, IReadOnlyList<Guid> ConCartera);

    /// <summary>La misma búsqueda dos veces bajo RLS: sin restricción de cartera y con la cartera del escenario.</summary>
    private async Task<Resultados> BuscarAsync(Func<CaeManagerDbContext, IAlcanceDatosService, Task<IEnumerable<Guid>>> buscar)
    {
        await using var contexto = escenario.ContextoRuntime();

        var sinRestriccion = (await buscar(contexto, new AlcanceDatosServiceFalso())).ToList();
        var conCartera = (await buscar(contexto, escenario.AlcanceDeLaCartera())).ToList();

        return new Resultados(sinRestriccion, conCartera);
    }

    private async Task<IReadOnlyList<Guid>> BuscarSinRestriccionAsync(
        Func<CaeManagerDbContext, IAlcanceDatosService, string, Task<IEnumerable<Guid>>> buscar, string termino)
    {
        await using var contexto = escenario.ContextoRuntime();

        return (await buscar(contexto, new AlcanceDatosServiceFalso(), termino)).ToList();
    }

    private void DebeEstrecharSinSalirDeLaCartera(Resultados resultados, Func<Bloque, Guid> id)
    {
        resultados.SinRestriccion.Should().BeEquivalentTo(
            [id(escenario.EnCartera), id(escenario.FueraDeCartera)],
            "control positivo: el término sin acentos casa con las dos filas del Tenant de la sesión, y la del Tenant ajeno no se ve");
        resultados.ConCartera.Should().Equal(
            [id(escenario.EnCartera)],
            "buscar solo estrecha: lo que está fuera de la Asignación de Cartera no vuelve aunque case con el término");
    }

    private void DebeEncontrarLaDeLaCartera(IReadOnlyList<Guid> ids, Func<Bloque, Guid> id, string columna)
    {
        ids.Should().Contain(id(escenario.EnCartera), $"el término, sin acentos y en otra caja, casa por {columna}");
        ids.Should().NotContain(id(escenario.Ajeno), "la fila del Tenant ajeno lleva el mismo valor y no se ve");
    }

    public sealed record Bloque(
        Guid ClienteEmpresarialId, Guid EmpresaId, Guid SubcontrataId, Guid CentroId, Guid TrabajadorId,
        Guid VehiculoId, Guid DocumentoId, Guid GestionId, Guid VisitaId);

    /// <summary>Lo que distingue un bloque de otro; el resto de los datos es idéntico en los tres.</summary>
    private sealed record Identificadores(
        string Sufijo, string CifCliente, string CifEmpresa, string CifSubcontrata, string Dni, string Matricula);

    /// <summary>Una base para toda la clase: los tests solo leen.</summary>
    public sealed class Escenario : IAsyncLifetime
    {
        // El bloque del Tenant ajeno repite los identificadores del de la cartera: un término que
        // case por DNI, CIF, matrícula o código casa también con su fila.
        private static readonly Identificadores DeLaCartera = new("Uno", "B12345674", "B10380186", "B10380236", "12345678Z", "1234BCD");
        private static readonly Identificadores DeFuera = new("Dos", "B87654323", "B10380194", "B10380251", "87654321X", "5678FGH");

        private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
        private Guid _tenantSesion;

        public CaeManagerDbContext Propietario { get; private set; } = null!;
        public Bloque EnCartera { get; private set; } = null!;
        public Bloque FueraDeCartera { get; private set; } = null!;
        public Bloque Ajeno { get; private set; } = null!;

        public async Task InitializeAsync()
        {
            var tenantPorAmbito = new TenantActualPorAmbito();
            var opciones = new DbContextOptionsBuilder<CaeManagerDbContext>()
                .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
                .AddInterceptors(new TenantSelladoInterceptor(tenantPorAmbito))
                .Options;
            Propietario = new CaeManagerDbContext(opciones, new EphemeralDataProtectionProvider(), tenantPorAmbito);
            await Propietario.Database.MigrateAsync();

            var tenantSesion = new Tenant("Tenant propietario de la sesión");
            var tenantAjeno = new Tenant("Tenant propietario ajeno");
            Propietario.Tenants.AddRange(tenantSesion, tenantAjeno);
            await Propietario.SaveChangesAsync();
            _tenantSesion = tenantSesion.Id;

            var tipoSesion = await SembrarCatalogoAsync(tenantSesion.Id);
            var tipoAjeno = await SembrarCatalogoAsync(tenantAjeno.Id);

            EnCartera = await SembrarBloqueAsync(tenantSesion.Id, tipoSesion, DeLaCartera);
            FueraDeCartera = await SembrarBloqueAsync(tenantSesion.Id, tipoSesion, DeFuera);
            Ajeno = await SembrarBloqueAsync(tenantAjeno.Id, tipoAjeno, DeLaCartera);
        }

        public async Task DisposeAsync()
        {
            await Propietario.DisposeAsync();
            await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);
        }

        /// <summary>Contexto de lectura como <c>cae_app_runtime</c>, con el Tenant de la sesión y RLS de producción.</summary>
        public CaeManagerDbContext ContextoRuntime()
        {
            var tenantDeLaPeticion = new TenantActualDeLaPeticion(_tenantSesion);
            var usuario = new CurrentUserServiceFalso(Guid.NewGuid(), tenantOrigenId: _tenantSesion);
            var opciones = new DbContextOptionsBuilder<CaeManagerDbContext>()
                .UseNpgsql(BaseDatosPostgresDePruebas.CadenaComoRuntime(_cadenaConexion))
                .AddInterceptors(
                    new TenantSelladoInterceptor(tenantDeLaPeticion),
                    new TenantRlsConnectionInterceptor(tenantDeLaPeticion, new SinClienteActivo(), usuario, BaseDatosPostgresDePruebas.FirmanteContextoRls))
                .Options;
            return new CaeManagerDbContext(opciones, new EphemeralDataProtectionProvider(), tenantDeLaPeticion);
        }

        /// <summary>La Asignación de Cartera del usuario: solo el primer bloque, en todas las dimensiones del alcance.</summary>
        public AlcanceDatosServiceFalso AlcanceDeLaCartera() => new(
            clienteIds: [EnCartera.ClienteEmpresarialId],
            centroIds: [EnCartera.CentroId],
            empresaIds: [EnCartera.EmpresaId],
            subcontrataIds: [EnCartera.SubcontrataId],
            trabajadorIds: [EnCartera.TrabajadorId],
            vehiculoIds: [EnCartera.VehiculoId]);

        private async Task<Guid> SembrarCatalogoAsync(Guid tenantId)
        {
            using var ambito = AmbitoTenantExplicito.Establecer(tenantId);

            Propietario.ParametrosSistema.Add(new ParametroSistema(umbralAmbarDias: 30, umbralRojoDias: 15));
            var tipo = new TipoDocumento(
                "Formación específica de riesgo eléctrico", null, aplicaVencimientoAutomatico: false, 1,
                AmbitoAplicacion.Trabajador, requerido: RequisitoDocumental.Si);
            Propietario.TiposDocumento.Add(tipo);
            await Propietario.SaveChangesAsync();

            return tipo.Id;
        }

        /// <summary>
        /// Los mismos nombres acentuados en cada bloque. Cada valor aparece en una sola columna de
        /// su listado, para que el término que lo busca solo pueda casar por ella.
        /// </summary>
        private async Task<Bloque> SembrarBloqueAsync(Guid tenantId, Guid tipoDocumentoId, Identificadores i)
        {
            using var ambito = AmbitoTenantExplicito.Establecer(tenantId);
            var hoy = DiaDeNegocio.Hoy();

            var cliente = Empresa.CrearComoCliente($"Cañón Ibérica {i.Sufijo} S.A.", i.CifCliente, false, null, null);
            var empresa = new Empresa($"Montajes Aragón Weiß {i.Sufijo} S.L.", i.CifEmpresa);
            var subcontrata = Empresa.CrearComoSubcontrata($"Instalaciones Cáceres {i.Sufijo} S.L.", i.CifSubcontrata, "Gestionada");
            Propietario.Empresas.AddRange(cliente, empresa, subcontrata);
            await Propietario.SaveChangesAsync();

            var centro = new Centro(cliente.Id, empresa.Id, $"Almacén Logroño {i.Sufijo}", $"Área-{i.Sufijo}");
            var trabajador = Trabajador.DeEmpresa(empresa.Id, "José María", $"Núñez Ibáñez {i.Sufijo}", i.Dni);
            trabajador.AsignarAlias("Pepón");
            var vehiculo = Vehiculo.DeEmpresa(empresa.Id, $"Camión grúa {i.Sufijo}", "Citroën Jumper", i.Matricula);
            Propietario.Centros.Add(centro);
            Propietario.Trabajadores.Add(trabajador);
            Propietario.Vehiculos.Add(vehiculo);
            await Propietario.SaveChangesAsync();

            var documento = Documento.DeTrabajador(
                trabajador.Id, tipoDocumentoId, hoy.AddDays(-5), VigenciaDocumento.VenceEl(hoy.AddYears(1)));
            var gestion = new Gestion(trabajador.Id, centro.Id, tipoDocumentoId);
            var visita = new Visita(centro.Id, hoy.AddDays(2), hoy.AddDays(3), null);
            Propietario.Documentos.Add(documento);
            Propietario.Gestiones.Add(gestion);
            Propietario.Visitas.Add(visita);
            await Propietario.SaveChangesAsync();

            return new Bloque(
                cliente.Id, empresa.Id, subcontrata.Id, centro.Id, trabajador.Id,
                vehiculo.Id, documento.Id, gestion.Id, visita.Id);
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

    /// <summary>
    /// <c>ObtenerClientesQueryHandler</c> pide las alertas de vigencia por el mediador para pintar el
    /// estado documental de cada fila; aquí no hay ninguna y no es lo que se mide.
    /// </summary>
    private sealed class MediadorSinAlertas : IMediator
    {
        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            request is ObtenerAlertasQuery
                ? Task.FromResult((TResponse)(object)Array.Empty<AlertaDto>())
                : throw new NotSupportedException($"MediadorSinAlertas no atiende {request.GetType().Name}.");

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest =>
            throw new NotSupportedException("MediadorSinAlertas solo atiende ObtenerAlertasQuery.");

        public Task<object?> Send(object request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("MediadorSinAlertas solo atiende ObtenerAlertasQuery.");

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("MediadorSinAlertas no soporta streams.");

        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("MediadorSinAlertas no soporta streams.");

        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification => Task.CompletedTask;
    }
}
