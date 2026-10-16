using CaeManager.Application.Common;
using CaeManager.Application.Plataforma;
using CaeManager.Application.Proyectos.Queries.ObtenerEmpresasProyecto;
using CaeManager.Application.Proyectos.Queries.ObtenerVehiculosProyecto;
using CaeManager.Application.Proyectos.Queries.ObtenerVisitasProyecto;
using CaeManager.Domain.Asignaciones;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Common;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Operaciones;
using CaeManager.Domain.Proyectos;
using CaeManager.Domain.Tenants;
using CaeManager.Domain.Trabajadores;
using CaeManager.Domain.Vehiculos;
using CaeManager.Domain.Visitas;
using CaeManager.Infrastructure.Autorizacion;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Interceptors;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests.Proyectos;

/// <summary>Ambos lectores ejecutan SQL con cartera real y RLS de cae_app_runtime, sin fracción de Empresa.</summary>
public class EmpresasYVisitasProyectoBajoRuntimeTests : IAsyncLifetime
{
    private readonly string _cadena = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Tenant _propietario = new("Tenant propietario de Proyecto");
    private readonly Tenant _ajeno = new("Tenant propietario ajeno");
    private readonly Guid _administrador = Guid.NewGuid();
    private readonly Guid _gestor = Guid.NewGuid();
    private Guid _proyecto;
    private Guid _fueraDeCartera;
    private Guid _proyectoAjeno;
    private Guid _directa;
    private Guid _contraparte;
    private Guid _visita;
    private Guid _vehiculoDirecta;
    private Guid _vehiculoContraparte;

    public async Task InitializeAsync()
    {
        await using (var contexto = Contexto(_propietario.Id))
        {
            await contexto.Database.MigrateAsync();
            contexto.Tenants.AddRange(_propietario, _ajeno);
            var roles = await contexto.Roles.ToDictionaryAsync(r => r.Name!, r => r.Id);
            void Cuenta(Guid id, string rol)
            {
                contexto.Users.Add(new ApplicationUser
                {
                    Id = id,
                    TenantId = _propietario.Id,
                    UserName = $"{id:N}@caemanager.local",
                    NormalizedUserName = $"{id:N}@CAEMANAGER.LOCAL",
                    Email = $"{id:N}@caemanager.local",
                    NormalizedEmail = $"{id:N}@CAEMANAGER.LOCAL",
                    NombreCompleto = rol,
                    EmailConfirmed = true,
                    SecurityStamp = Guid.NewGuid().ToString(),
                    ConcurrencyStamp = Guid.NewGuid().ToString()
                });
                contexto.UserRoles.Add(new IdentityUserRole<Guid> { UserId = id, RoleId = roles[rol] });
            }
            Cuenta(_administrador, Roles.Administrador);
            Cuenta(_gestor, Roles.GestorCae);
            var cliente = Empresa.CrearComoCliente("Cliente empresarial de la cartera", "B10380186", false, null, null);
            var otroCliente = Empresa.CrearComoCliente("Cliente empresarial fuera de cartera", "B10380202", false, null, null);
            var directa = new Empresa("Empresa empleadora directa", "B10380194");
            var contraparte = Empresa.CrearComoSubcontrata("Empresa contraparte empleadora", null, "Estandar");
            var noParticipa = new Empresa("Empresa sin técnicos en el Proyecto");
            contexto.Empresas.AddRange(cliente, otroCliente, directa, contraparte, noParticipa);
            var vehiculoDirecta = Vehiculo.DeEmpresa(directa.Id, "Furgoneta de la directa", "Ford Transit", "1234ABC");
            var vehiculoContraparte = Vehiculo.DeSubcontrata(contraparte.Id, "Grúa de la contraparte", "Liebherr", "9999ZZZ");
            var vehiculoAjenoAlProyecto = Vehiculo.DeEmpresa(noParticipa.Id, "Camión que no participa", "Iveco", "0001AAA");
            contexto.Vehiculos.AddRange(vehiculoDirecta, vehiculoContraparte, vehiculoAjenoAlProyecto);
            _vehiculoDirecta = vehiculoDirecta.Id; _vehiculoContraparte = vehiculoContraparte.Id;
            var centro = new Centro(cliente.Id, directa.Id, "Centro visible");
            var fuera = new Centro(otroCliente.Id, directa.Id, "Centro fuera de cartera");
            contexto.Centros.AddRange(centro, fuera);
            var directo = Trabajador.DeEmpresa(directa.Id, "Ana", "Alba", "12345678Z");
            var externo = Trabajador.DeSubcontrata(contraparte.Id, "Leo", "Mas", "11111111H");
            var segundaRuta = Trabajador.DeSubcontrata(directa.Id, "Ada", "Roca", "87654321X");
            contexto.Trabajadores.AddRange(directo, externo, segundaRuta);
            await contexto.SaveChangesAsync();
            var hoy = DiaDeNegocio.Hoy();
            var proyecto = Proyecto.Crear(cliente.Id, centro.Id, "Proyecto visible", hoy.AddDays(-10), hoy.AddDays(10), null);
            var proyectoFuera = Proyecto.Crear(otroCliente.Id, fuera.Id, "Proyecto fuera de cartera", hoy.AddDays(-10), hoy.AddDays(10), null);
            contexto.Proyectos.AddRange(proyecto, proyectoFuera);
            contexto.ProyectosTecnicos.AddRange(new ProyectoTecnico(proyecto.Id, directo.Id, hoy.AddDays(-5)),
                new ProyectoTecnico(proyecto.Id, externo.Id, hoy.AddDays(-5)),
                new ProyectoTecnico(proyecto.Id, segundaRuta.Id, hoy.AddDays(-5)),
                new ProyectoTecnico(proyectoFuera.Id, directo.Id, hoy.AddDays(-5)));
            contexto.Asignaciones.AddRange(new Asignacion(directo.Id, centro.Id, hoy.AddDays(-5)),
                new Asignacion(externo.Id, centro.Id, hoy.AddDays(-5)), new Asignacion(segundaRuta.Id, centro.Id, hoy.AddDays(-5)));
            var visita = new Visita(centro.Id, hoy, hoy.AddDays(1), null);
            var visitaFuera = new Visita(fuera.Id, hoy, hoy.AddDays(1), null);
            var cancelada = new Visita(centro.Id, hoy.AddDays(2), hoy.AddDays(3), null);
            cancelada.Cancelar(DateTime.UtcNow, "Control negativo");
            contexto.Visitas.AddRange(visita, visitaFuera, cancelada, new Visita(centro.Id, hoy.AddDays(20), hoy.AddDays(21), null));
            contexto.VisitasTrabajadores.AddRange(new VisitaTrabajador(visita.Id, directo.Id), new VisitaTrabajador(visita.Id, externo.Id));
            var ahora = DateTime.UtcNow;
            contexto.AsignacionesOperacion.Add(AsignacionOperacion.Raiz(_propietario.Id, ServicioCae.Outbound, ahora.AddDays(-1), ahora));
            // Control del modelo: operación acotada; cartera universal, como exige D-7.
            var operacion = AsignacionOperacion.Interna(_propietario.Id, ServicioCae.Outbound,
                AmbitoAsignacion.DeRelacionCliente(cliente.Id), ahora.AddDays(-1), null, ahora);
            contexto.AsignacionesOperacion.Add(operacion);
            contexto.AsignacionesCartera.Add(AsignacionCartera.Interna(operacion, _gestor, AmbitoAsignacion.Universal,
                ahora.AddDays(-1), null, ahora));
            await contexto.SaveChangesAsync();
            _proyecto = proyecto.Id; _fueraDeCartera = proyectoFuera.Id; _directa = directa.Id; _contraparte = contraparte.Id; _visita = visita.Id;
        }
        await using (var contexto = Contexto(_ajeno.Id))
        {
            var cliente = Empresa.CrearComoCliente("Cliente empresarial ajeno", "B10380186", false, null, null);
            var empresa = new Empresa("Empresa ajena", "B10380194");
            contexto.Empresas.AddRange(cliente, empresa);
            var centro = new Centro(cliente.Id, empresa.Id, "Centro ajeno");
            contexto.Centros.Add(centro);
            var trabajador = Trabajador.DeEmpresa(empresa.Id, "Nora", "Vidal", "12345678Z");
            contexto.Trabajadores.Add(trabajador);
            await contexto.SaveChangesAsync();
            var hoy = DiaDeNegocio.Hoy();
            var proyecto = Proyecto.Crear(cliente.Id, centro.Id, "Proyecto ajeno", hoy.AddDays(-10), hoy.AddDays(10), null);
            contexto.Proyectos.Add(proyecto);
            contexto.ProyectosTecnicos.Add(new ProyectoTecnico(proyecto.Id, trabajador.Id, hoy.AddDays(-5)));
            contexto.Visitas.Add(new Visita(centro.Id, hoy, hoy.AddDays(1), null));
            await contexto.SaveChangesAsync();
            _proyectoAjeno = proyecto.Id;
        }
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadena);

    [Fact]
    public async Task Ambos_lectores_traducen_SQL_y_conservan_los_dos_papeles_y_las_Visitas_del_periodo()
    {
        var lectura = await Leer(_proyecto, _administrador, Roles.Administrador);
        lectura.Empresas.Select(e => e.Id).Should().BeEquivalentTo([_directa, _contraparte]);
        var directa = lectura.Empresas.Single(e => e.Id == _directa);
        directa.DesdeEmpresaDirecta.Should().BeTrue();
        directa.DesdeRelacionSubcontrata.Should().BeTrue();
        directa.TecnicosActivos.Should().Be(2);
        lectura.Empresas.Single(e => e.Id == _contraparte).DesdeEmpresaDirecta.Should().BeFalse();
        lectura.Empresas.Single(e => e.Id == _contraparte).DesdeRelacionSubcontrata.Should().BeTrue();
        lectura.Visitas.Should().ContainSingle().Which.Id.Should().Be(_visita);
        lectura.Visitas.Single().Trabajadores.Should().Be(2);
    }

    [Fact]
    public async Task Ambos_lectores_ocultan_el_Tenant_ajeno_y_RLS_se_ejecuta_bajo_runtime()
    {
        var positivo = await Leer(_proyecto, _administrador, Roles.Administrador);
        positivo.Empresas.Should().NotBeEmpty(); positivo.Visitas.Should().NotBeEmpty();
        var ajeno = await Leer(_proyectoAjeno, _administrador, Roles.Administrador);
        ajeno.Empresas.Should().BeEmpty(); ajeno.Visitas.Should().BeEmpty();
        await using var contexto = Contexto(_propietario.Id, _administrador, Roles.Administrador);
        (await contexto.Database.SqlQueryRaw<string>("SELECT current_user AS \"Value\"").SingleAsync()).Should().Be("cae_app_runtime");
        (await contexto.Proyectos.IgnoreQueryFilters().AnyAsync(p => p.Id == _proyectoAjeno)).Should().BeFalse();
    }

    [Fact]
    public async Task Ambos_lectores_excluyen_el_Proyecto_fuera_de_cartera_con_controles_positivos()
    {
        var positivo = await Leer(_proyecto, _gestor, Roles.GestorCae);
        positivo.Empresas.Should().NotBeEmpty(); positivo.Visitas.Should().NotBeEmpty();
        var fuera = await Leer(_fueraDeCartera, _gestor, Roles.GestorCae);
        fuera.Empresas.Should().BeEmpty(); fuera.Visitas.Should().BeEmpty();
        var administrador = await Leer(_fueraDeCartera, _administrador, Roles.Administrador);
        administrador.Empresas.Should().NotBeEmpty(); administrador.Visitas.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Vehiculos_del_Proyecto_traducen_SQL_por_las_dos_rutas_y_excluyen_Empresas_que_no_participan()
    {
        var vehiculos = await LeerVehiculos(_proyecto, _administrador, Roles.Administrador);
        vehiculos.Select(v => v.Id).Should().BeEquivalentTo([_vehiculoDirecta, _vehiculoContraparte]);
        vehiculos.Single(v => v.Id == _vehiculoDirecta).EmpleadorId.Should().Be(_directa);
        vehiculos.Single(v => v.Id == _vehiculoContraparte).EmpleadorNombre.Should().Be("Empresa contraparte empleadora");
    }

    [Fact]
    public async Task Vehiculos_del_Proyecto_respetan_Tenant_y_cartera_con_controles_positivos()
    {
        (await LeerVehiculos(_proyecto, _gestor, Roles.GestorCae)).Should().NotBeEmpty();
        (await LeerVehiculos(_fueraDeCartera, _gestor, Roles.GestorCae)).Should().BeEmpty();
        (await LeerVehiculos(_fueraDeCartera, _administrador, Roles.Administrador)).Should().NotBeEmpty();
        (await LeerVehiculos(_proyectoAjeno, _administrador, Roles.Administrador)).Should().BeEmpty();
    }

    private async Task<IReadOnlyList<VehiculoProyectoDto>> LeerVehiculos(Guid proyectoId, Guid usuario, string rol)
    {
        await using var contexto = Contexto(_propietario.Id, usuario, rol);
        var alcance = new AlcanceDatosService(contexto, new CurrentUserServiceFalso(usuario, rol, tenantOrigenId: _propietario.Id),
            new TenantActualAmbiental { TenantId = _propietario.Id }, new SesionPrivilegiadaAusente());
        return await new ObtenerVehiculosProyectoQueryHandler(contexto, contexto, contexto, contexto, contexto, alcance)
            .Handle(new(proyectoId), CancellationToken.None);
    }

    private sealed record Lectura(IReadOnlyList<EmpresaProyectoDto> Empresas, IReadOnlyList<VisitaProyectoDto> Visitas);
    private async Task<Lectura> Leer(Guid proyectoId, Guid usuario, string rol)
    {
        await using var contexto = Contexto(_propietario.Id, usuario, rol);
        var alcance = new AlcanceDatosService(contexto, new CurrentUserServiceFalso(usuario, rol, tenantOrigenId: _propietario.Id),
            new TenantActualAmbiental { TenantId = _propietario.Id }, new SesionPrivilegiadaAusente());
        var empresas = await new ObtenerEmpresasProyectoQueryHandler(contexto, contexto, contexto, contexto, alcance)
            .Handle(new(proyectoId), CancellationToken.None);
        var visitas = await new ObtenerVisitasProyectoQueryHandler(contexto, contexto, contexto, alcance)
            .Handle(new(proyectoId), CancellationToken.None);
        return new(empresas, visitas);
    }

    private CaeManagerDbContext Contexto(Guid tenantId, Guid? usuario = null, string? rol = null)
    {
        var tenant = new TenantActualAmbiental { TenantId = tenantId };
        var opciones = new DbContextOptionsBuilder<CaeManagerDbContext>().UseNpgsql(
            usuario is null ? _cadena : BaseDatosPostgresDePruebas.CadenaComoRuntime(_cadena),
            n => n.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"));
        opciones.AddInterceptors(new TenantSelladoInterceptor(tenant));
        if (usuario is not null)
            opciones.AddInterceptors(new TenantRlsConnectionInterceptor(tenant, new SinTenantSeleccionado(),
                new CurrentUserServiceFalso(usuario.Value, rol!, tenantOrigenId: tenantId), BaseDatosPostgresDePruebas.FirmanteContextoRls),
                new ConcurrenciaOptimistaInterceptor());
        return new(opciones.Options, new EphemeralDataProtectionProvider(), tenant);
    }

    private sealed class SinTenantSeleccionado : IClienteActivoSeleccionado
    {
        public Guid? TenantIdSeleccionado => null;
        public Guid? AsignacionOperacionIdSeleccionada => null;
        public Guid? SesionPrivilegiadaIdSeleccionada => null;
    }
}
