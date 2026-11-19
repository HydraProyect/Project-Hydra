using CaeManager.Application.Bandeja.Queries.ObtenerMiTrabajoAgregado;
using CaeManager.Application.Common;
using CaeManager.Application.Comercial.LineaOutbound;
using CaeManager.Application.DependencyInjection;
using CaeManager.Application.Plataforma;
using CaeManager.Application.Tenants;
using CaeManager.Application.Visitas.Queries.ObtenerProximaVisitaPorCentro;
using CaeManager.Domain.AltaAutonoma;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Comercial;
using CaeManager.Domain.Common;
using CaeManager.Domain.Configuracion;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Tenants;
using CaeManager.Domain.Visitas;
using CaeManager.Infrastructure.Autorizacion;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Interceptors;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using ComandoDeOutboundDePrueba = CaeManager.Application.Subcontratas.PruebasDeIntegracionSoloInbound.ComandoDeOutboundDePrueba;
using QueryDeComunicacionesDePrueba = CaeManager.Application.Comunicaciones.PruebasDeIntegracionSoloInbound.QueryDeComunicacionesDePrueba;

namespace CaeManager.IntegrationTests.Inbound.Comercial
{
    /// <summary>
    /// BAJA 3 de la revisión de Outbound sobre la PR #2379 («Inbound sin Outbound», decisión del 2026-11-18): la regla del
    /// Tenant solo Inbound, de punta a punta bajo <c>cae_app_runtime</c> con los interceptores de sellado y de sesión RLS, y
    /// con las piezas reales: <see cref="GuardaPorServicioBehavior{TRequest,TResponse}"/>, <see cref="AlcanceDatosService"/>,
    /// <see cref="CriterioDeOutbound"/> y <see cref="TenantSoloInboundService"/> leyendo <c>ProductosContratados</c> por la
    /// conexión de runtime. Los tests de Application usan dobles del contexto; aquí la lectura de las filas de línea pasa
    /// por la RLS de verdad.
    ///
    /// <para>
    /// Tres Tenants de origen, cada uno con su Cliente empresarial, su Centro y una Visita: sin ninguna fila de línea (el
    /// Outbound histórico, que conserva Outbound), con las dos líneas, y solo con la línea Inbound, que es el control de
    /// que el instrumento ve el cierre. El usuario es Administrador con el papel de Tenant de origen.
    /// </para>
    /// </summary>
    public class SoloInboundGuardaYAlcanceBajoRuntimeTests : IAsyncLifetime
    {
        public enum Lineas { Ninguna, Ambas, SoloInbound }

        private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
        private readonly Guid _usuario = Guid.NewGuid();
        private readonly Dictionary<Lineas, (Guid Tenant, Guid Centro)> _casos = [];
        private readonly List<IAsyncDisposable> _liberar = [];
        private CaeManagerDbContext _propietario = null!;

        public async Task InitializeAsync()
        {
            var tenantPorAmbito = new TenantActualPorAmbito();
            var opcionesPropietario = new DbContextOptionsBuilder<CaeManagerDbContext>()
                .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
                .AddInterceptors(new TenantSelladoInterceptor(tenantPorAmbito))
                .Options;
            _propietario = new CaeManagerDbContext(opcionesPropietario, new EphemeralDataProtectionProvider(), tenantPorAmbito);
            await _propietario.Database.MigrateAsync();

            var tenants = Enum.GetValues<Lineas>().ToDictionary(l => l, l => new Tenant($"Tenant de origen {l}"));
            _propietario.Tenants.AddRange(tenants.Values);
            await _propietario.SaveChangesAsync();

            var ahora = DateTime.UtcNow;
            var hoy = DiaDeNegocio.Hoy();
            foreach (var (lineas, tenant) in tenants)
            {
                using var ambito = AmbitoTenantExplicito.Establecer(tenant.Id);
                _propietario.ParametrosSistema.Add(new ParametroSistema(umbralAmbarDias: 30, umbralRojoDias: 15));
                var cliente = Empresa.CrearComoCliente($"Cliente empresarial {lineas}", "B10380194", false, null, null);
                var empresa = new Empresa($"Empresa {lineas}", "B10380186");
                _propietario.Empresas.AddRange(cliente, empresa);
                await _propietario.SaveChangesAsync();

                var centro = new Centro(cliente.Id, empresa.Id, $"Centro {lineas}");
                _propietario.Centros.Add(centro);
                await _propietario.SaveChangesAsync();

                _propietario.Visitas.Add(new Visita(centro.Id, hoy.AddDays(1), hoy.AddDays(2), notas: null));
                if (lineas is Lineas.Ambas or Lineas.SoloInbound)
                    _propietario.ProductosContratados.Add(Contratar(LineaProductoContratado.Inbound, $"cus_Inbound{lineas}", ahora));
                if (lineas is Lineas.Ambas)
                    _propietario.ProductosContratados.Add(Contratar(LineaProductoContratado.OutboundGestionPropia, $"cus_Outbound{lineas}", ahora));
                await _propietario.SaveChangesAsync();

                _casos[lineas] = (tenant.Id, centro.Id);
            }
        }

        public async Task DisposeAsync()
        {
            foreach (var recurso in _liberar)
                await recurso.DisposeAsync();
            await _propietario.DisposeAsync();
            await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);
        }

        private static ProductoContratado Contratar(LineaProductoContratado linea, string pagador, DateTime ahora) =>
            ProductoContratado.Contratar(linea, pagador, TramoTrabajadoresActivos.De1A10, ModalidadSuscripcionAlta.Anual,
                ahora.AddDays(-30), ahoraUtc: ahora);

        /// <summary>Control del instrumento: la lectura de las filas de línea va de verdad bajo RLS.</summary>
        [Fact]
        public async Task Las_filas_de_linea_se_leen_bajo_RLS_efectiva()
        {
            var composicion = Componer(Lineas.Ambas);

            (await composicion.Runtime.ProductosContratados.IgnoreQueryFilters().CountAsync()).Should().Be(2,
                "app.tenant_id es el Tenant de origen: la RLS oculta la fila Inbound del Tenant solo Inbound");
        }

        [Theory]
        [InlineData(Lineas.Ninguna, false)]
        [InlineData(Lineas.Ambas, false)]
        [InlineData(Lineas.SoloInbound, true)]
        public async Task La_guarda_cierra_el_comando_de_Outbound_solo_al_Tenant_solo_Inbound(Lineas lineas, bool cierra)
        {
            var composicion = Componer(lineas);
            var guarda = new GuardaPorServicioBehavior<ComandoDeOutboundDePrueba, Result>(
                composicion.Usuario, composicion.Servicios.GetRequiredService<ICriterioDeOutbound>());
            var ejecutado = false;

            var resultado = await guarda.Handle(
                new ComandoDeOutboundDePrueba(),
                _ => { ejecutado = true; return Task.FromResult(Result.Exito()); },
                CancellationToken.None);

            ejecutado.Should().Be(!cierra);
            resultado.EsExitoso.Should().Be(!cierra);
            if (cierra) resultado.Error.Codigo.Should().Be("Autorizacion.ComandoDeOutboundSinLineaOutbound");
        }

        [Theory]
        [InlineData(Lineas.Ninguna, false)]
        [InlineData(Lineas.Ambas, false)]
        [InlineData(Lineas.SoloInbound, true)]
        public async Task La_query_de_Outbound_con_alcance_ve_sus_Visitas_salvo_el_Tenant_solo_Inbound(Lineas lineas, bool cierra)
        {
            var composicion = Componer(lineas);
            var centro = _casos[lineas].Centro;
            var handler = new ObtenerProximaVisitaPorCentroQueryHandler(
                composicion.Runtime, composicion.Servicios.GetRequiredService<IAlcanceDatosService>());

            IReadOnlyDictionary<Guid, IReadOnlyList<VisitaResumenDto>> visitas;
            // El marco que AmbitoInboundBehavior fija a toda petición de Outbound (PeticionDeOutbound.CerradaAlSoloInbound).
            using (AmbitoPeticionOutbound.Fijar(true))
                visitas = await handler.Handle(new ObtenerProximaVisitaPorCentroQuery([centro]), CancellationToken.None);

            if (cierra)
                visitas.Should().BeEmpty("al Tenant solo Inbound el alcance no le da ningún Centro dentro de una petición de Outbound");
            else
                visitas.Should().ContainKey(centro).WhoseValue.Should().ContainSingle();
        }

        [Theory]
        [InlineData(Lineas.Ninguna, false)]
        [InlineData(Lineas.Ambas, false)]
        [InlineData(Lineas.SoloInbound, true)]
        public async Task Comunicaciones_solo_se_acota_al_Tenant_solo_Inbound(Lineas lineas, bool acota)
        {
            var composicion = Componer(lineas);
            var guarda = new GuardaPorServicioBehavior<QueryDeComunicacionesDePrueba, bool>(
                composicion.Usuario, composicion.Servicios.GetRequiredService<ICriterioDeOutbound>());

            var acotada = await guarda.Handle(
                new QueryDeComunicacionesDePrueba(),
                _ => Task.FromResult(AmbitoComunicacionesSoloInbound.Activo),
                CancellationToken.None);

            acotada.Should().Be(acota, "D6=a: solo el Tenant solo Inbound queda en los hilos anclados a una Empresa contratista");
        }

        [Theory]
        [InlineData(Lineas.Ninguna, false)]
        [InlineData(Lineas.Ambas, false)]
        [InlineData(Lineas.SoloInbound, true)]
        public async Task La_parte_de_origen_de_Mi_Trabajo_conserva_su_alcance_salvo_en_el_Tenant_solo_Inbound(Lineas lineas, bool alcanceCero)
        {
            var composicion = Componer(lineas);

            var resultado = await composicion.Servicios.GetRequiredService<IMediator>().Send(new ObtenerMiTrabajoAgregadoQuery());

            resultado.Tenants.Single(t => t.TenantId == _casos[lineas].Tenant).AlcanceCero.Should().Be(alcanceCero,
                "el fan-out pasa por el mediador, que fija el ámbito de Outbound, y el alcance del Tenant de origen es el del " +
                "Administrador salvo que el Producto Contratado sea solo Inbound");
        }

        private sealed record Composicion(ServiceProvider Servicios, CaeManagerDbContext Runtime, ICurrentUserService Usuario);

        private Composicion Componer(Lineas lineas)
        {
            var tenant = _casos[lineas].Tenant;
            var tenantDeLaPeticion = new TenantActualDeLaPeticion(tenant);
            var usuario = new CurrentUserServiceFalso(_usuario, Roles.Administrador, tenant, papel: PapelActivo.TenantDeOrigen);
            var opcionesRuntime = new DbContextOptionsBuilder<CaeManagerDbContext>()
                .UseNpgsql(BaseDatosPostgresDePruebas.CadenaComoRuntime(_cadenaConexion))
                .AddInterceptors(
                    new TenantSelladoInterceptor(tenantDeLaPeticion),
                    new TenantRlsConnectionInterceptor(tenantDeLaPeticion, new SinClienteActivo(), usuario, BaseDatosPostgresDePruebas.FirmanteContextoRls))
                .Options;
            var runtime = new CaeManagerDbContext(opcionesRuntime, new EphemeralDataProtectionProvider(), tenantDeLaPeticion);
            _liberar.Add(runtime);

            var servicios = new ServiceCollection();
            servicios.AddApplication();
            servicios.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
            servicios.AddLogging();
            servicios.AddSingleton<ITenantActual>(tenantDeLaPeticion);
            servicios.AddSingleton<IUnitOfWork>(runtime);
            servicios.AddSingleton<ITenantsQueryContext>(runtime);
            servicios.AddSingleton<ILineaOutboundQueryContext>(runtime);
            servicios.AddSingleton<CaeManager.Application.Operaciones.IOperacionesQueryContext>(runtime);
            servicios.AddSingleton<CaeManager.Application.Empresas.IEmpresasQueryContext>(runtime);
            servicios.AddSingleton<CaeManager.Application.Proyectos.IProyectosQueryContext>(runtime);
            servicios.AddSingleton<CaeManager.Application.Proyectos.IRequisitosEventoQueryContext>(runtime);
            servicios.AddSingleton<CaeManager.Application.Centros.ICentrosQueryContext>(runtime);
            servicios.AddSingleton<CaeManager.Application.EstadoMaterializado.IEstadoMaterializadoQueryContext>(runtime);
            servicios.AddSingleton<CaeManager.Application.EstadoMaterializado.Frescura.ILectorVersionEstadoTenant>(new CaeManager.Infrastructure.EstadoMaterializado.LectorVersionEstadoTenant(runtime));
            servicios.AddSingleton<CaeManager.Application.Trabajadores.ITrabajadoresQueryContext>(runtime);
            servicios.AddSingleton<CaeManager.Application.TiposDocumento.ITiposDocumentoQueryContext>(runtime);
            servicios.AddSingleton<CaeManager.Application.Documentos.IDocumentosQueryContext>(runtime);
            servicios.AddSingleton<CaeManager.Application.DocumentosIa.IDocumentosIaQueryContext>(runtime);
            servicios.AddSingleton<CaeManager.Application.Asignaciones.IAsignacionesQueryContext>(runtime);
            servicios.AddSingleton<CaeManager.Application.Configuracion.IConfiguracionQueryContext>(runtime);
            servicios.AddSingleton<CaeManager.Application.Visitas.IVisitasQueryContext>(runtime);
            servicios.AddSingleton<CaeManager.Application.Comunicaciones.IComunicacionesQueryContext>(runtime);
            servicios.AddSingleton<CaeManager.Application.Integraciones.IProveedoresPlataformaCaeQueryContext>(runtime);
            servicios.AddSingleton<ICurrentUserService>(usuario);
            // El alcance real, con el criterio real de AddApplication (CriterioDeOutbound → TenantSoloInboundService →
            // ProductosContratados por la conexión de runtime): una sola instancia, como el ámbito scoped de una petición.
            servicios.AddSingleton<IAlcanceDatosService>(sp => new AlcanceDatosService(
                runtime, usuario, tenantDeLaPeticion, new SesionPrivilegiadaAusente(),
                criterioDeOutbound: sp.GetRequiredService<ICriterioDeOutbound>()));
            var proveedor = servicios.BuildServiceProvider();
            _liberar.Add(proveedor);
            return new Composicion(proveedor, runtime, usuario);
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
}

// Peticiones de prueba en las áreas que clasifica PeticionDeOutbound (por namespace, como ComandosDePruebaDeOutbound de
// Application.Tests): Subcontratas, cerrada al Tenant solo Inbound; Comunicaciones, abierta y acotada (D6=a).
namespace CaeManager.Application.Subcontratas.PruebasDeIntegracionSoloInbound
{
    public sealed record ComandoDeOutboundDePrueba : CaeManager.Application.Common.ICommand;
}

namespace CaeManager.Application.Comunicaciones.PruebasDeIntegracionSoloInbound
{
    public sealed record QueryDeComunicacionesDePrueba : MediatR.IRequest<bool>;
}
