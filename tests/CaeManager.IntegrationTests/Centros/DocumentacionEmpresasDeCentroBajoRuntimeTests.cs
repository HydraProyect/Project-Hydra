using CaeManager.Application.Centros;
using CaeManager.Application.Centros.Queries.ObtenerDocumentacionEmpresasDeCentro;
using CaeManager.Application.Centros.Queries.ObtenerDocumentacionRequeridaDeCentro;
using CaeManager.Application.Common;
using CaeManager.Application.Plataforma;
using CaeManager.Application.TiposDocumento;
using CaeManager.Domain.Asignaciones;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Common;
using CaeManager.Domain.Configuracion;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Operaciones;
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
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CaeManager.IntegrationTests.Centros;

/// <summary>Ejecuta el lector y los requisitos reales con SQL, cartera real y RLS de cae_app_runtime.</summary>
public class DocumentacionEmpresasDeCentroBajoRuntimeTests : IAsyncLifetime
{
    private readonly string _cadena = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Tenant _propietario = new("Tenant propietario de Centro");
    private readonly Tenant _ajeno = new("Tenant propietario ajeno");
    private readonly Guid _administrador = Guid.NewGuid();
    private readonly Guid _gestor = Guid.NewGuid();
    private Guid _centro;
    private Guid _fueraDeCartera;
    private Guid _centroAjeno;
    private Guid _directa;
    private Guid _contraparte;

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
            contexto.Empresas.AddRange(cliente, otroCliente, directa, contraparte);
            var centro = new Centro(cliente.Id, directa.Id, "Centro visible");
            var fuera = new Centro(otroCliente.Id, directa.Id, "Centro fuera de cartera");
            contexto.Centros.AddRange(centro, fuera);
            var trabajadorDirecto = Trabajador.DeEmpresa(directa.Id, "Ana", "Alba", "12345678Z");
            var trabajadorContraparte = Trabajador.DeSubcontrata(contraparte.Id, "Leo", "Mas", "11111111H");
            contexto.Trabajadores.AddRange(trabajadorDirecto, trabajadorContraparte);
            var tipo = new TipoDocumento("Seguro", 12, true, 1, AmbitoAplicacion.Empresa, RequisitoDocumental.Si);
            contexto.TiposDocumento.Add(tipo);
            contexto.ParametrosSistema.Add(new ParametroSistema(30, 7));
            await contexto.SaveChangesAsync();
            var hoy = DiaDeNegocio.Hoy();
            contexto.Asignaciones.AddRange(new Asignacion(trabajadorDirecto.Id, centro.Id, hoy.AddDays(-5)),
                new Asignacion(trabajadorContraparte.Id, centro.Id, hoy.AddDays(-5)));
            contexto.Documentos.AddRange(
                Documento.DeEmpresa(directa.Id, tipo.Id, hoy.AddMonths(-1), VigenciaDocumento.VenceEl(hoy.AddDays(90))),
                Documento.DeEmpresa(contraparte.Id, tipo.Id, hoy.AddMonths(-1), VigenciaDocumento.VenceEl(hoy.AddDays(-1))));
            var ahora = DateTime.UtcNow;
            contexto.AsignacionesOperacion.Add(AsignacionOperacion.Raiz(_propietario.Id, ServicioCae.Outbound, ahora.AddDays(-1), ahora));
            // Control de cartera del modelo: acota la operación; la cartera sigue siendo universal (D-7).
            var operacion = AsignacionOperacion.Interna(_propietario.Id, ServicioCae.Outbound,
                AmbitoAsignacion.DeRelacionCliente(cliente.Id), ahora.AddDays(-1), null, ahora);
            contexto.AsignacionesOperacion.Add(operacion);
            contexto.AsignacionesCartera.Add(AsignacionCartera.Interna(operacion, _gestor, AmbitoAsignacion.Universal,
                ahora.AddDays(-1), null, ahora));
            await contexto.SaveChangesAsync();
            _centro = centro.Id; _fueraDeCartera = fuera.Id; _directa = directa.Id; _contraparte = contraparte.Id;
        }
        await using (var contexto = Contexto(_ajeno.Id))
        {
            var cliente = Empresa.CrearComoCliente("Cliente empresarial ajeno", "B10380186", false, null, null);
            var empresa = new Empresa("Empresa ajena", "B10380194");
            contexto.Empresas.AddRange(cliente, empresa);
            var centro = new Centro(cliente.Id, empresa.Id, "Centro ajeno");
            contexto.Centros.Add(centro);
            await contexto.SaveChangesAsync();
            _centroAjeno = centro.Id;
        }
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadena);

    [Fact]
    public async Task Traduce_el_join_y_devuelve_ambas_Empresas_empleadoras_con_sus_documentos()
    {
        var filas = await Leer(_centro, _administrador, Roles.Administrador);
        filas.Select(f => f.EmpresaId).Should().BeEquivalentTo([_directa, _contraparte]);
        filas.Single(f => f.EmpresaId == _directa).DesdeRelacionSubcontrata.Should().BeFalse();
        filas.Single(f => f.EmpresaId == _directa).Documentos.Should().ContainSingle().Which.Estado.Should().Be(EstadoDocumento.Vigente);
        filas.Single(f => f.EmpresaId == _contraparte).DesdeRelacionSubcontrata.Should().BeTrue();
        filas.Single(f => f.EmpresaId == _contraparte).Documentos.Should().ContainSingle().Which.Estado.Should().Be(EstadoDocumento.Vencido);
        filas.SelectMany(f => f.Documentos).Should().OnlyContain(d => d.DocumentoId != null);
    }

    [Fact]
    public async Task Tenant_ajeno_es_vacio_y_RLS_oculta_el_Centro_incluso_sin_filtro_EF()
    {
        (await Leer(_centro, _administrador, Roles.Administrador)).Should().NotBeEmpty();
        (await Leer(_centroAjeno, _administrador, Roles.Administrador)).Should().BeEmpty();
        await using var contexto = Contexto(_propietario.Id, _administrador, Roles.Administrador);
        (await contexto.Database.SqlQueryRaw<string>("SELECT current_user AS \"Value\"").SingleAsync()).Should().Be("cae_app_runtime");
        (await contexto.Centros.IgnoreQueryFilters().AnyAsync(c => c.Id == _centroAjeno)).Should().BeFalse();
    }

    [Fact]
    public async Task Centro_fuera_de_la_cartera_es_vacio_con_control_positivo_del_mismo_Gestor_CAE()
    {
        (await Leer(_centro, _gestor, Roles.GestorCae)).Should().NotBeEmpty();
        (await Leer(_fueraDeCartera, _gestor, Roles.GestorCae)).Should().BeEmpty();
        (await Leer(_fueraDeCartera, _administrador, Roles.Administrador)).Should().NotBeEmpty();
    }

    private async Task<IReadOnlyList<DocumentacionEmpresaCentroDto>> Leer(Guid centroId, Guid usuario, string rol)
    {
        await using var contexto = Contexto(_propietario.Id, usuario, rol);
        var alcance = new AlcanceDatosService(contexto, new CurrentUserServiceFalso(usuario, rol, tenantOrigenId: _propietario.Id),
            new TenantActualAmbiental { TenantId = _propietario.Id }, new SesionPrivilegiadaAusente());
        var servicios = new ServiceCollection();
        servicios.AddSingleton<ICentrosQueryContext>(contexto);
        servicios.AddSingleton<ITiposDocumentoQueryContext>(contexto);
        servicios.AddSingleton<IAlcanceDatosService>(alcance);
        servicios.AddMediatR(c => c.RegisterServicesFromAssemblyContaining<ObtenerDocumentacionRequeridaDeCentroQuery>());
        using var proveedor = servicios.BuildServiceProvider();
        return await new ObtenerDocumentacionEmpresasDeCentroQueryHandler(contexto, contexto, contexto, contexto, contexto,
            contexto, alcance, proveedor.GetRequiredService<IMediator>()).Handle(new(centroId), CancellationToken.None);
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
