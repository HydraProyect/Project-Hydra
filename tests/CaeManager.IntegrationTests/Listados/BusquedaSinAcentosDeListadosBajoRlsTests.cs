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
/// otra en un Tenant propietario ajeno. Cada término casa con las tres. Por eso cada caso mide
/// dos cosas a la vez: sin restricción de cartera vuelven las dos del Tenant de la sesión
/// (control positivo: el término casa con la de fuera de la cartera, y RLS oculta la ajena), y
/// con la cartera puesta vuelve solo la de dentro.
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

    [Fact]
    public async Task Trabajadores_encuentra_sin_acentos_y_no_sale_de_la_cartera()
    {
        var ids = await BuscarAsync(async (contexto, alcance) =>
        {
            var handler = new ObtenerTrabajadoresQueryHandler(
                contexto, contexto, contexto, contexto, alcance, new CalculoEstadoDocumentalService(contexto, contexto));
            var resultado = await handler.Handle(new ObtenerTrabajadoresQuery("NUNEZ IBANEZ"), CancellationToken.None);
            return resultado.Elementos.Select(t => t.Id);
        });

        DebeEstrecharSinSalirDeLaCartera(ids, b => b.TrabajadorId);
    }

    [Fact]
    public async Task Empresas_encuentra_sin_acentos_y_no_sale_de_la_cartera()
    {
        var ids = await BuscarAsync((contexto, alcance) => BuscarEmpresasAsync(contexto, alcance, "montajes aragon"));

        DebeEstrecharSinSalirDeLaCartera(ids, b => b.EmpresaId);
    }

    [Fact]
    public async Task Vehiculos_encuentra_sin_acentos_y_no_sale_de_la_cartera()
    {
        var ids = await BuscarAsync(async (contexto, alcance) =>
        {
            var handler = new ObtenerVehiculosQueryHandler(
                contexto, contexto, alcance, contexto, contexto, new CalculoEstadoDocumentalService(contexto, contexto));
            var resultado = await handler.Handle(new ObtenerVehiculosQuery("camion grua"), CancellationToken.None);
            return resultado.Elementos.Select(v => v.Id);
        });

        DebeEstrecharSinSalirDeLaCartera(ids, b => b.VehiculoId);
    }

    [Fact]
    public async Task Subcontratas_encuentra_sin_acentos_y_no_sale_de_la_cartera()
    {
        var ids = await BuscarAsync(async (contexto, alcance) =>
        {
            var servicio = new CalculoEstadoSubcontrataService(contexto, contexto, contexto, contexto, contexto, contexto, alcance);
            var handler = new ObtenerSubcontratasQueryHandler(contexto, alcance, servicio);
            var resultado = await handler.Handle(new ObtenerSubcontratasQuery("INSTALACIONES CACERES"), CancellationToken.None);
            return resultado.Elementos.Select(s => s.Id);
        });

        DebeEstrecharSinSalirDeLaCartera(ids, b => b.SubcontrataId);
    }

    /// <summary>En Gestiones el nombre buscado es «Nombre Apellidos», concatenado en la consulta.</summary>
    [Fact]
    public async Task Gestiones_encuentra_sin_acentos_y_no_sale_de_la_cartera()
    {
        var ids = await BuscarAsync(async (contexto, alcance) =>
        {
            var handler = new ObtenerGestionesQueryHandler(contexto, contexto, contexto, contexto, alcance);
            var resultado = await handler.Handle(
                new ObtenerGestionesQuery("jose maria nunez", Estado: null, TrabajadorId: null), CancellationToken.None);
            return resultado.Elementos.Select(g => g.Id);
        });

        DebeEstrecharSinSalirDeLaCartera(ids, b => b.GestionId);
    }

    [Fact]
    public async Task Visitas_encuentra_sin_acentos_y_no_sale_de_la_cartera()
    {
        var ids = await BuscarAsync(async (contexto, alcance) =>
        {
            var handler = new ObtenerVisitasQueryHandler(
                contexto, contexto, contexto, contexto, contexto, contexto, alcance, contexto);
            var resultado = await handler.Handle(
                new ObtenerVisitasQuery("almacen logrono", SoloActivas: false, NotificadoCliente: null), CancellationToken.None);
            return resultado.Elementos.Select(v => v.Id);
        });

        DebeEstrecharSinSalirDeLaCartera(ids, b => b.VisitaId);
    }

    [Fact]
    public async Task Documentos_encuentra_sin_acentos_y_no_sale_de_la_cartera()
    {
        var ids = await BuscarAsync(async (contexto, alcance) =>
        {
            var handler = new ObtenerDocumentosQueryHandler(
                contexto, contexto, contexto, contexto, contexto, contexto, contexto, alcance, contexto, contexto);
            var resultado = await handler.Handle(
                new ObtenerDocumentosQuery(TrabajadorId: null, Ambito: null, Busqueda: "FORMACION ESPECIFICA"), CancellationToken.None);
            return resultado.Elementos.Select(d => d.Id);
        });

        DebeEstrecharSinSalirDeLaCartera(ids, b => b.DocumentoId);
    }

    [Fact]
    public async Task Clientes_encuentra_sin_acentos_y_no_sale_de_la_cartera()
    {
        var ids = await BuscarAsync(async (contexto, alcance) =>
        {
            var handler = new ObtenerClientesQueryHandler(contexto, contexto, contexto, new MediadorSinAlertas(), alcance);
            var resultado = await handler.Handle(new ObtenerClientesQuery("canon iberica", SoloCriticos: null), CancellationToken.None);
            return resultado.Elementos.Select(c => c.Id);
        });

        DebeEstrecharSinSalirDeLaCartera(ids, b => b.ClienteEmpresarialId);
    }

    [Fact]
    public async Task Centros_encuentra_sin_acentos_y_no_sale_de_la_cartera()
    {
        var ids = await BuscarAsync(async (contexto, alcance) =>
        {
            var handler = new ObtenerCentrosQueryHandler(
                contexto, contexto, alcance, new CalculoEstadoCentroService(contexto, contexto, contexto, contexto, contexto, contexto));
            var resultado = await handler.Handle(new ObtenerCentrosQuery("ALMACEN LOGRONO", ClienteId: null), CancellationToken.None);
            return resultado.Elementos.Select(c => c.Id);
        });

        DebeEstrecharSinSalirDeLaCartera(ids, b => b.CentroId);
    }

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

    private sealed record Resultados(IReadOnlyList<Guid> SinRestriccion, IReadOnlyList<Guid> ConCartera);

    /// <summary>La misma búsqueda dos veces bajo RLS: sin restricción de cartera y con la cartera del escenario.</summary>
    private async Task<Resultados> BuscarAsync(Func<CaeManagerDbContext, IAlcanceDatosService, Task<IEnumerable<Guid>>> buscar)
    {
        await using var contexto = escenario.ContextoRuntime();

        var sinRestriccion = (await buscar(contexto, new AlcanceDatosServiceFalso())).ToList();
        var conCartera = (await buscar(contexto, escenario.AlcanceDeLaCartera())).ToList();

        return new Resultados(sinRestriccion, conCartera);
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

    public sealed record Bloque(
        Guid ClienteEmpresarialId, Guid EmpresaId, Guid SubcontrataId, Guid CentroId, Guid TrabajadorId,
        Guid VehiculoId, Guid DocumentoId, Guid GestionId, Guid VisitaId);

    /// <summary>Una base para toda la clase: los tests solo leen.</summary>
    public sealed class Escenario : IAsyncLifetime
    {
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

            EnCartera = await SembrarBloqueAsync(tenantSesion.Id, tipoSesion, "Uno", "B12345674", "12345678Z", "1234BCD");
            FueraDeCartera = await SembrarBloqueAsync(tenantSesion.Id, tipoSesion, "Dos", "B87654323", "87654321X", "5678FGH");
            Ajeno = await SembrarBloqueAsync(tenantAjeno.Id, tipoAjeno, "Tres", "B12345674", "77189989B", "9012JKL");
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

        /// <summary>Los mismos nombres acentuados en cada bloque; solo cambia el sufijo que los distingue.</summary>
        private async Task<Bloque> SembrarBloqueAsync(
            Guid tenantId, Guid tipoDocumentoId, string sufijo, string cifCliente, string dni, string matricula)
        {
            using var ambito = AmbitoTenantExplicito.Establecer(tenantId);
            var hoy = DiaDeNegocio.Hoy();

            var cliente = Empresa.CrearComoCliente($"Cañón Ibérica {sufijo} S.A.", cifCliente, false, null, null);
            var empresa = new Empresa($"Montajes Aragón Weiß {sufijo} S.L.");
            var subcontrata = Empresa.CrearComoSubcontrata($"Instalaciones Cáceres {sufijo} S.L.", null, "Gestionada");
            Propietario.Empresas.AddRange(cliente, empresa, subcontrata);
            await Propietario.SaveChangesAsync();

            var centro = new Centro(cliente.Id, empresa.Id, $"Almacén Logroño {sufijo}");
            var trabajador = Trabajador.DeEmpresa(empresa.Id, "José María", $"Núñez Ibáñez {sufijo}", dni);
            var vehiculo = Vehiculo.DeEmpresa(empresa.Id, $"Camión grúa {sufijo}", "Citroën Jumper", matricula);
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
