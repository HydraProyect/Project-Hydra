using CaeManager.Application.Documentos.Queries.ObtenerAcreditacionesPorProveedor;
using CaeManager.Application.Plataforma;
using CaeManager.Domain.Asignaciones;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Trabajadores;
using CaeManager.Infrastructure.Autorizacion;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests.Documentos;

/// <summary>
/// Incremento 2 del piloto Outbound: <c>/extension/acreditaciones-pendientes</c>
/// reexpone <see cref="ObtenerAcreditacionesPorProveedorQuery"/> con el NIF del
/// Trabajador (<see cref="AcreditacionDrillDownDto.TrabajadorDni"/>), y la
/// política <c>SesionOExtension</c> admite cualquier sesión interactiva, también
/// la del rol Cliente (usuario de portal de una empresa cliente externa). Su
/// alcance de lectura le da los Centros de su propio Cliente, y con ellos el
/// DNI de los Trabajadores de las contratistas — un artefacto interno de la
/// gestión CAE, no contenido de portal. Mismo defecto, ya corregido, que el de
/// las credenciales de un canal (REC-153, <c>ObtenerCentroIdsParaGestionAsync</c>).
///
/// Va contra el servicio de alcance REAL: la regla vive en Infrastructure
/// (necesita el rol), un doble no puede observarla.
/// </summary>
public class AcreditacionesPorProveedorRolClienteTests : IAsyncLifetime
{
    private const string Dni = "22334455Y";

    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly TenantActualAmbiental _tenantActual = new() { TenantId = Guid.NewGuid() };

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto();
        await contexto.Database.MigrateAsync();
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Fact]
    public async Task El_rol_Cliente_no_recibe_acreditaciones_ni_el_dni_de_los_trabajadores_de_las_contratistas()
    {
        var usuarioPortal = await SembrarAsync();

        // Control positivo: con acceso total la fila existe y lleva el DNI. Sin
        // esto, un resultado vacío para el rol Cliente no distinguiría «lo
        // filtré» de «no había nada que devolver».
        var conAccesoTotal = await ConsultarComoAsync(usuarioPortal, "Administrador");
        conAccesoTotal.Should().ContainSingle(d => d.TrabajadorDni == Dni);

        var comoCliente = await ConsultarComoAsync(usuarioPortal, "Cliente");

        comoCliente.Should().BeEmpty(
            "las acreditaciones por plataforma con el NIF del Trabajador son un artefacto de gestión, no de portal");
    }

    private async Task<Guid> SembrarAsync()
    {
        Guid clienteId;
        await using (var contexto = CrearContexto())
        {
            var cliente = Empresa.CrearComoCliente("Cliente Portal Acreditaciones S.L.", "B10380194", false, null, null);
            var contratista = new Empresa("Contratista Acreditaciones S.L.", "B10380186");
            contexto.Empresas.AddRange(cliente, contratista);
            await contexto.SaveChangesAsync();

            var centro = new Centro(cliente.Id, contratista.Id, "Centro del portal");
            contexto.Centros.Add(centro);
            await contexto.SaveChangesAsync();

            var proveedor = await contexto.ProveedoresPlataformaCae.FirstAsync();
            var canal = CanalGestionDocumental.DePlataforma(centro.Id, "Gestión general", proveedor.Id, null, null, null);
            contexto.CanalesGestionDocumental.Add(canal);

            var trabajador = Trabajador.DeEmpresa(contratista.Id, "Nora", "Vidal", Dni);
            contexto.Trabajadores.Add(trabajador);
            var tipo = new TipoDocumento("Apto médico portal", 12, true, 1, AmbitoAplicacion.Trabajador);
            contexto.TiposDocumento.Add(tipo);
            await contexto.SaveChangesAsync();

            contexto.Asignaciones.Add(new Asignacion(trabajador.Id, centro.Id, new DateOnly(2026, 1, 1)));
            var documento = Documento.DeTrabajador(trabajador.Id, tipo.Id, new DateOnly(2026, 1, 1), VigenciaDocumento.VenceEl(new DateOnly(2027, 1, 1)));
            contexto.Documentos.Add(documento);
            await contexto.SaveChangesAsync();

            contexto.AcreditacionesDocumentoPlataforma.Add(new AcreditacionDocumentoPlataforma(documento.Id, canal.Id));
            clienteId = cliente.Id;
            await contexto.SaveChangesAsync();
        }

        var usuarioPortal = Guid.NewGuid();
        await using (var contexto = CrearContexto())
        {
            contexto.Users.Add(new ApplicationUser
            {
                Id = usuarioPortal,
                UserName = $"portal-{usuarioPortal:N}@ejemplo.test",
                Email = $"portal-{usuarioPortal:N}@ejemplo.test",
                ClienteId = clienteId,
                TenantId = _tenantActual.TenantId!.Value
            });
            await contexto.SaveChangesAsync();
        }

        return usuarioPortal;
    }

    private async Task<List<AcreditacionDrillDownDto>> ConsultarComoAsync(Guid usuarioId, string rol)
    {
        await using var contexto = CrearContexto();
        var alcance = new AlcanceDatosService(
            contexto, new CurrentUserServiceFalso(usuarioId, rol, tenantOrigenId: _tenantActual.TenantId),
            _tenantActual, new SesionPrivilegiadaAusente());
        var handler = new ObtenerAcreditacionesPorProveedorQueryHandler(
            contexto, contexto, contexto, contexto, contexto, contexto, alcance);

        var proveedores = await handler.Handle(new ObtenerAcreditacionesPorProveedorQuery(), CancellationToken.None);
        return proveedores.SelectMany(p => p.Clientes).SelectMany(c => c.Documentos).ToList();
    }

    private CaeManagerDbContext CrearContexto()
    {
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(_tenantActual))
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), _tenantActual);
    }
}
